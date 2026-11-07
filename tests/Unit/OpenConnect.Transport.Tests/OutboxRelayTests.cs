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

    private sealed class FakeBroker : IJetStreamPublisher
    {
        public bool Fail { get; init; }
        public string? MessageId { get; private set; }
        public ValueTask PublishAsync(string subject, ReadOnlyMemory<byte> payload, string messageId, CancellationToken cancellationToken)
        {
            if (Fail) throw new InvalidOperationException("No acknowledgement.");
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
