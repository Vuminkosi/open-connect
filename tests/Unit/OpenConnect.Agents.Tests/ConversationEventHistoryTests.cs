using OpenConnect.Abstractions;
using OpenConnect.Agents.Execution;
using Xunit;

namespace OpenConnect.Agents.Tests;

public sealed class ConversationEventHistoryTests
{
    private static readonly SessionId Session1 = new(Ulid.NewUlid());
    private static readonly SessionId Session2 = new(Ulid.NewUlid());

    [Fact]
    public async Task AppendAndRetrieve_PreservesAppLevelEventsSeparatelyFromTranscript()
    {
        var store = new InMemoryConversationEventHistory();
        var evt1 = new ConversationTurnEvent(
            Guid.NewGuid(),
            "conversation.turn.started",
            Session1,
            "tenant-1",
            1,
            "user",
            DateTimeOffset.UtcNow,
            "api.gateway",
            "User asked a question.");

        var evt2 = new ConversationTurnEvent(
            Guid.NewGuid(),
            "conversation.turn.completed",
            Session1,
            "tenant-1",
            2,
            "lead-agent",
            DateTimeOffset.UtcNow,
            "maf.conversation.runner",
            "Assistant answered coherently.");

        await store.AppendEventAsync(evt1);
        await store.AppendEventAsync(evt2);

        var retrieved = await store.GetEventsAsync(Session1, "tenant-1");
        Assert.Equal(2, retrieved.Count);
        Assert.Equal(evt1.EventId, retrieved[0].EventId);
        Assert.Equal(evt2.EventId, retrieved[1].EventId);
        Assert.Equal("conversation.turn.started", retrieved[0].EventType);
        Assert.Equal("conversation.turn.completed", retrieved[1].EventType);
        Assert.Equal("api.gateway", retrieved[0].Provenance);
        Assert.Equal("maf.conversation.runner", retrieved[1].Provenance);
    }

    [Fact]
    public async Task Append_EnforcesStrictMonotonicSequence()
    {
        var store = new InMemoryConversationEventHistory();
        var evt1 = new ConversationTurnEvent(
            Guid.NewGuid(), "turn.step", Session1, "tenant-1", 5, "agent", DateTimeOffset.UtcNow, "prov", "summary 1");
        var evt2 = new ConversationTurnEvent(
            Guid.NewGuid(), "turn.step", Session1, "tenant-1", 5, "agent", DateTimeOffset.UtcNow, "prov", "duplicate sequence");

        await store.AppendEventAsync(evt1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AppendEventAsync(evt2).AsTask());
    }

    [Fact]
    public async Task Events_AreStrictlyIsolatedByTenantAndSession()
    {
        var store = new InMemoryConversationEventHistory();
        var evt1 = new ConversationTurnEvent(
            Guid.NewGuid(), "turn.step", Session1, "tenant-1", 1, "agent", DateTimeOffset.UtcNow, "prov", "summary");
        var evt2 = new ConversationTurnEvent(
            Guid.NewGuid(), "turn.step", Session2, "tenant-2", 1, "agent", DateTimeOffset.UtcNow, "prov", "summary");

        await store.AppendEventAsync(evt1);
        await store.AppendEventAsync(evt2);

        var tenant1Session1 = await store.GetEventsAsync(Session1, "tenant-1");
        var tenant2Session1 = await store.GetEventsAsync(Session1, "tenant-2");
        var tenant1Session2 = await store.GetEventsAsync(Session2, "tenant-1");

        Assert.Single(tenant1Session1);
        Assert.Empty(tenant2Session1);
        Assert.Empty(tenant1Session2);
    }
}
