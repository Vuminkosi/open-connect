using System.Text.Json;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Messaging;
using OpenConnect.Transport.Nats;
using OpenConnect.Transport.Nats.Messaging;
using Xunit;

namespace OpenConnect.Transport.Tests;

public sealed class MessagingNatsRoutingTests
{
    private static readonly TenantId Tenant = new(Ulid.NewUlid());
    private static readonly ConversationId Conversation = new(Ulid.NewUlid());

    [Fact]
    public void Builds_canonical_durable_and_live_subjects()
    {
        Assert.Equal("OC_MESSAGING", MessagingNatsSubjects.StreamName);
        Assert.Equal("oc.v1.messaging.durable.>", MessagingNatsSubjects.DurableWildcardFilter);

        var durable = MessagingNatsSubjects.DurableConversation(Tenant, Conversation);
        Assert.Equal($"oc.v1.messaging.durable.{Tenant.Value}.{Conversation.Value}", durable);
        Assert.Equal(durable, MessagingNatsSubjects.ValidateSubject(durable));

        var live = MessagingNatsSubjects.LiveGateway(Tenant, "gw-eu-01");
        Assert.Equal($"oc.v1.messaging.live.{Tenant.Value}.gw-eu-01", live);
        Assert.Equal(live, MessagingNatsSubjects.ValidateSubject(live));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("tenant.*")]
    [InlineData("tenant.>")]
    [InlineData("a.b")]
    [InlineData("tenant space")]
    [InlineData("tenant/slash")]
    [InlineData("tenant\\backslash")]
    [InlineData("tenant\r\ninjected")]
    [InlineData("tenant$money")]
    public void Rejects_subject_injection_and_wildcard_escape_tokens(string malicious)
    {
        Assert.Throws<ArgumentException>(() => MessagingNatsSubjects.ValidateOpaqueToken(malicious, "token"));
        Assert.Throws<ArgumentException>(() => MessagingNatsSubjects.DurableConversation(malicious, "conv1"));
        Assert.Throws<ArgumentException>(() => MessagingNatsSubjects.LiveGateway("tenant1", malicious));
    }

    [Fact]
    public void Rejects_oversized_tokens_and_invalid_full_subjects()
    {
        var tooLong = new string('a', MessagingNatsSubjects.MaxTokenLength + 1);
        Assert.Throws<ArgumentException>(() => MessagingNatsSubjects.ValidateOpaqueToken(tooLong, "token"));
        Assert.Throws<ArgumentException>(() => MessagingNatsSubjects.ValidateSubject("oc.v1.notifications.inapp"));
        Assert.Throws<ArgumentException>(() => MessagingNatsSubjects.ValidateSubject("oc.v1.messaging.durable.t1.*"));
        Assert.Throws<ArgumentException>(() => MessagingNatsSubjects.ValidateSubject("oc.v1.messaging.durable.t1.>"));
        Assert.Throws<ArgumentException>(() => MessagingNatsSubjects.ValidateSubject("oc.v1.messaging.durable.t1.c1.extra"));
    }

    [Fact]
    public async Task Publisher_routes_durable_and_live_events_with_bounded_deduplication()
    {
        var broker = new RecordingBroker();
        var publisher = new NatsMessagingEventPublisher(broker, dedupCapacity: 2);

        var env1 = MakeEnvelope("evt-001", Conversation);
        Assert.True(await publisher.DispatchAsync(env1));
        Assert.False(await publisher.DispatchAsync(env1)); // duplicate suppressed
        Assert.Single(broker.Published);
        Assert.Equal($"oc.v1.messaging.durable.{Tenant.Value}.{Conversation.Value}", broker.Published[0].Subject);
        Assert.Equal("evt-001", broker.Published[0].MessageId);

        // Fill bounded dedup capacity (2) and evict evt-001
        Assert.True(await publisher.DispatchAsync(MakeEnvelope("evt-002", Conversation)));
        Assert.True(await publisher.DispatchAsync(MakeEnvelope("evt-003", Conversation)));
        Assert.True(await publisher.DispatchAsync(env1)); // evicted, allowed again
        Assert.Equal(4, broker.Published.Count);

        // Targeted live publication
        var liveEnv = MakeEnvelope("evt-live-1", conversationId: null);
        Assert.True(await publisher.PublishLiveAsync(liveEnv, "gw-01"));
        Assert.Equal($"oc.v1.messaging.live.{Tenant.Value}.gw-01", broker.Published[^1].Subject);
    }

    [Fact]
    public async Task Publisher_unmarks_dedup_on_broker_failure_so_retry_succeeds()
    {
        var broker = new RecordingBroker { FailNext = true };
        var publisher = new NatsMessagingEventPublisher(broker);
        var env = MakeEnvelope("evt-retry-1", Conversation);

        await Assert.ThrowsAsync<InvalidOperationException>(() => publisher.DispatchAsync(env).AsTask());
        broker.FailNext = false;

        Assert.True(await publisher.DispatchAsync(env));
        Assert.Single(broker.Published);
    }

    [Theory]
    [InlineData("""{"plaintext":"secret"}""")]
    [InlineData("""{"nested":{"body":"hello"}}""")]
    [InlineData("""{"accessToken":"tok-123"}""")]
    [InlineData("""{"refreshToken":"rt-123"}""")]
    [InlineData("""{"privateKey":"pem"}""")]
    [InlineData("""{"pushToken":"fcm-token"}""")]
    [InlineData("""{"prompt":"system instructions"}""")]
    public async Task Publisher_rejects_plaintext_or_secret_payload_fields_fail_closed(string forbiddenPayload)
    {
        var broker = new RecordingBroker();
        var publisher = new NatsMessagingEventPublisher(broker);
        var env = MakeEnvelope("evt-bad-payload", Conversation, forbiddenPayload);

        await Assert.ThrowsAsync<JsonException>(() => publisher.DispatchAsync(env).AsTask());
        Assert.Empty(broker.Published);
    }

    [Fact]
    public async Task Publisher_rejects_invalid_event_type_oversized_payload_and_missing_routing_target()
    {
        var broker = new RecordingBroker();
        var publisher = new NatsMessagingEventPublisher(broker, maxEventBytes: 512);

        var wrongType = MakeEnvelope("evt-type", Conversation) with { Type = "other.event" };
        await Assert.ThrowsAsync<ArgumentException>(() => publisher.DispatchAsync(wrongType).AsTask());

        var noTarget = MakeEnvelope("evt-no-target", conversationId: null);
        await Assert.ThrowsAsync<ArgumentException>(() => publisher.DispatchAsync(noTarget).AsTask());

        var huge = MakeEnvelope("evt-huge", Conversation, $$"""{"ciphertext":"{{new string('A', 1024)}}"}""");
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => publisher.DispatchAsync(huge).AsTask());

        Assert.Empty(broker.Published);
    }

    private static MessagingEventEnvelope MakeEnvelope(
        string eventId,
        ConversationId? conversationId,
        string payloadJson = """{"messageId":"m1","epoch":1,"ciphertext":"Y3Q="}""") =>
        new(
            SchemaVersion: 1,
            EventId: eventId,
            Type: MessagingEventTypes.MessageCommitted,
            TenantId: Tenant,
            ConversationId: conversationId,
            Sequence: 1,
            Cursor: "cur-1",
            OccurredAt: DateTimeOffset.UtcNow,
            PayloadJson: payloadJson);

    private sealed class RecordingBroker : IJetStreamPublisher
    {
        public bool FailNext { get; set; }
        public List<(string Subject, string MessageId, byte[] Payload)> Published { get; } = [];

        public ValueTask PublishAsync(string subject, ReadOnlyMemory<byte> payload, string messageId, CancellationToken cancellationToken)
        {
            if (FailNext)
                throw new InvalidOperationException("Simulated NATS outage.");
            Published.Add((subject, messageId, payload.ToArray()));
            return ValueTask.CompletedTask;
        }
    }
}
