using System.Text;
using OpenConnect.Abstractions;
using OpenConnect.Transport.Nats;
using Xunit;

namespace OpenConnect.Transport.Tests;

public sealed class OutboxRelayTests
{
    private static readonly Guid MessageId = Guid.Parse("123e4567-e89b-42d3-a456-426614174000");
    private static readonly string Json = $$"""{"schemaVersion":"openconnect.v1","messageId":"{{MessageId:D}}","taskId":"task-1","kind":"classification","issuedAt":"2026-01-01T00:00:00Z","expiresAt":"2026-01-01T00:01:00Z","lease":{"leaseId":"123e4567-e89b-42d3-a456-426614174001","fencingToken":1},"payloadRef":"opaque://payload/abc"}""";

    [Fact]
    public async Task Publishes_with_message_id_and_marks_only_after_ack()
    {
        var broker = new FakeBroker();
        var source = new FakeSource(new OutboxEntry(MessageId, "task", Json));
        var relay = new OutboxRelay(source, new NatsPublisher(broker));
        Assert.Equal(1, await relay.RelayOnceAsync("tasks", 10, Guid.NewGuid()));
        Assert.Equal(MessageId.ToString("D"), broker.MessageId);
        Assert.Equal(1, source.MarkCount);
    }

    [Fact]
    public async Task Broker_failure_does_not_mark_entry_delivered()
    {
        var broker = new FakeBroker { Fail = true };
        var source = new FakeSource(new OutboxEntry(MessageId, "task", Json));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new OutboxRelay(source, new NatsPublisher(broker)).RelayOnceAsync("tasks", 1, Guid.NewGuid()));
        Assert.Equal(0, source.MarkCount);
    }

    [Fact]
    public async Task Rejects_raw_payload_fields()
    {
        var broker = new FakeBroker();
        var raw = Json.Replace("\"payloadRef\":", "\"prompt\":\"secret\",\"payloadRef\":");
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => new NatsPublisher(broker).PublishAsync("tasks", Encoding.UTF8.GetBytes(raw)).AsTask());
        Assert.Null(broker.MessageId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Poison_first_entry_does_not_block_later_valid_entry(bool oversized)
    {
        var poisonId = Guid.NewGuid();
        var poison = oversized ? Json.Replace(MessageId.ToString("D"), poisonId.ToString("D"))
            .Replace("opaque://payload/abc", "opaque://" + new string('x', 65536)) : "not-json";
        var source = new BatchSource([new(poisonId, "task", poison), new(MessageId, "task", Json)]);
        var broker = new FakeBroker();
        await Assert.ThrowsAnyAsync<Exception>(() =>
            new OutboxRelay(source, new NatsPublisher(broker)).RelayOnceAsync("tasks", 2, Guid.NewGuid()));
        Assert.Equal(MessageId.ToString("D"), broker.MessageId);
        Assert.Equal([MessageId], source.Delivered);
    }

    [Fact]
    public async Task Cancellation_stops_the_batch_immediately()
    {
        using var cancelled = new CancellationTokenSource();
        var source = new BatchSource([new(MessageId, "task", Json), new(MessageId, "task", Json)]);
        var broker = new CancellingBroker(cancelled);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new OutboxRelay(source, new NatsPublisher(broker)).RelayOnceAsync("tasks", 2, Guid.NewGuid(), cancelled.Token));
        Assert.Equal(1, broker.Calls);
        Assert.Empty(source.Delivered);
    }

    private sealed class CancellingBroker(CancellationTokenSource cancelled) : IJetStreamPublisher
    {
        public int Calls { get; private set; }
        public ValueTask PublishAsync(string subject, ReadOnlyMemory<byte> payload, string messageId, CancellationToken ct)
        {
            Calls++;
            cancelled.Cancel();
            ct.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BatchSource(IReadOnlyList<OutboxEntry> entries) : IOutboxRelaySource
    {
        public List<Guid> Delivered { get; } = [];
        public Task<IReadOnlyList<OutboxEntry>> GetPendingAsync(int limit, CancellationToken cancellationToken) => Task.FromResult(entries);
        public Task MarkDeliveredAsync(Guid entryId, Guid leaseId, CancellationToken cancellationToken)
        {
            Delivered.Add(entryId);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeBroker : IJetStreamPublisher
    {
        public bool Fail { get; init; }
        public string? MessageId { get; private set; }
        public ValueTask PublishAsync(string subject, ReadOnlyMemory<byte> payload, string messageId, CancellationToken cancellationToken)
        {
            if (Fail)
                throw new InvalidOperationException("No acknowledgement.");
            MessageId = messageId;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeSource(OutboxEntry entry) : IOutboxRelaySource
    {
        public int MarkCount { get; private set; }
        public Task<IReadOnlyList<OutboxEntry>> GetPendingAsync(int limit, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<OutboxEntry>>([entry]);
        public Task MarkDeliveredAsync(Guid entryId, Guid leaseId, CancellationToken cancellationToken) { MarkCount++; return Task.CompletedTask; }
    }
}
