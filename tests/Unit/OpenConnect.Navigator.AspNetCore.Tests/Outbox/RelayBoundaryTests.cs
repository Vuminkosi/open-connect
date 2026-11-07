using OpenConnect.Navigator.AspNetCore.Outbox;
using OpenConnect.Navigator.Events;
using OpenConnect.Transport.Nats;
using Xunit;

namespace OpenConnect.Navigator.AspNetCore.Tests.Outbox;

public sealed class RelayBoundaryTests
{
    [Fact]
    public async Task WriterProducesEnvelopeAcceptedBySharedWorkerParser()
    {
        var sink = new CapturingOutboxWriter();
        var writer = new NavigatorOutboxWriter(sink);
        var entryId = Guid.NewGuid().ToString("D");
        var value = new CommittedNavigatorEvent(
            entryId, "session-1", "task-1", 1, null, "command.completed",
            DateTimeOffset.UtcNow, new Dictionary<string, string>());

        await writer.WriteAsync(value);

        var envelope = WorkerEnvelope.Parse(System.Text.Encoding.UTF8.GetBytes(sink.Payload!));

        Assert.Equal("openconnect.v1", envelope.SchemaVersion);
        Assert.Equal(Guid.Parse(sink.MessageId!), envelope.MessageId);
        Assert.Equal(Guid.Parse(entryId), envelope.MessageId);
        Assert.Equal("session-1", envelope.TaskId);
        Assert.Equal("navigator.command.completed", envelope.Kind);
        Assert.Equal($"navigator/session-1/{entryId}", envelope.PayloadRef);
        Assert.False(envelope.PayloadRef.StartsWith('{'));
        Assert.InRange(envelope.PayloadRef.Length, 1, 2048);
        Assert.True(envelope.ExpiresAt > envelope.IssuedAt);
        // Navigator notices intentionally expire after one hour; allow five minutes for clock skew.
        Assert.True(envelope.ExpiresAt - envelope.IssuedAt <= TimeSpan.FromMinutes(65));
        // Lease values are temporary valid placeholders until durable outbox lease fencing is wired.
        Assert.NotEqual(Guid.Empty, envelope.Lease.LeaseId);
        Assert.True(envelope.Lease.FencingToken >= 1);
    }

    [Fact]
    public async Task WriterUsesGeneratedGuidAsMessageIdForNonGuidEventId()
    {
        var sink = new CapturingOutboxWriter();
        var writer = new NavigatorOutboxWriter(sink);
        var eventId = "event-from-legacy-source";
        var value = new CommittedNavigatorEvent(
            eventId, "session-1", "task-1", 1, null, "command.completed",
            DateTimeOffset.UtcNow, new Dictionary<string, string>());

        await writer.WriteAsync(value);

        Assert.True(Guid.TryParse(sink.MessageId, out var messageId));
        var envelope = WorkerEnvelope.Parse(System.Text.Encoding.UTF8.GetBytes(sink.Payload!));
        Assert.Equal(messageId, envelope.MessageId);
    }

    private sealed class CapturingOutboxWriter : INavigatorOutboxWriter
    {
        public string? MessageId { get; private set; }
        public string? Payload { get; private set; }

        public ValueTask WriteAsync(string topic, string messageId, string payload, CancellationToken cancellationToken = default)
        {
            MessageId = messageId;
            Payload = payload;
            return ValueTask.CompletedTask;
        }
    }
}
