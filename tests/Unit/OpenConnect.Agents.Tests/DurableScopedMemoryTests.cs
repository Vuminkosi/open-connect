using OpenConnect.Abstractions;
using OpenConnect.Agents.Execution;
using Xunit;

namespace OpenConnect.Agents.Tests;

/// <summary>
/// Proves OC2-D-SESSION-014: Implement optional scoped durable memory key/value/summary records
/// with consent, provenance, retention and deletion tombstones.
/// </summary>
public sealed class DurableScopedMemoryTests
{
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public async Task MemoryWrite_WithConsentAndProvenance_StoresAndIncrementsRevision()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        var store = new InMemorySessionMemoryStore(timeProvider: clock);

        var request = new MemoryWriteRequest(
            Scope: "session:s123",
            MemoryId: "user-preference",
            Summary: "Prefers dark mode and concise summaries",
            DataClass: "session",
            Provenance: "user.chat.turn.3",
            Ttl: TimeSpan.FromHours(2),
            WritePolicy: AgentMemoryWritePolicy.ExplicitApproved,
            ConsentToken: "valid-user-consent");

        var doc = await store.UpsertAsync(request);

        Assert.Equal("session:s123", doc.Scope);
        Assert.Equal("user-preference", doc.MemoryId);
        Assert.Equal(1, doc.Revision);
        Assert.Equal("user.chat.turn.3", doc.Provenance);
        Assert.False(doc.Tombstoned);
        Assert.Equal(clock.GetUtcNow().AddHours(2), doc.ExpiresAt);

        // Update increments revision
        var updateRequest = request with
        {
            Summary = "Prefers light mode",
            Provenance = "user.chat.turn.5"
        };
        var updatedDoc = await store.UpsertAsync(updateRequest);
        Assert.Equal(2, updatedDoc.Revision);
        Assert.Equal("Prefers light mode", updatedDoc.Summary);
    }

    [Fact]
    public async Task DenyByDefault_WithoutConsentToken_ThrowsUnauthorizedAccessException()
    {
        var store = new InMemorySessionMemoryStore();
        var request = new MemoryWriteRequest(
            Scope: "session:s123",
            MemoryId: "secret-note",
            Summary: "some content",
            DataClass: "session",
            Provenance: "unauthorized",
            Ttl: null,
            WritePolicy: AgentMemoryWritePolicy.DenyByDefault,
            ConsentToken: null); // No consent

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.UpsertAsync(request).AsTask());
    }

    [Fact]
    public async Task Delete_CreatesAuthoritativeTombstoneAndPreventsResurrection()
    {
        var store = new InMemorySessionMemoryStore();
        var request = new MemoryWriteRequest(
            "session:s123", "key1", "initial content", "session", "user", null,
            AgentMemoryWritePolicy.ExplicitApproved, "token");
        await store.UpsertAsync(request);

        // Delete with reason
        await store.DeleteAsync(new OpenConnect.Agents.Execution.MemoryDeletionRequest("session:s123", "key1", "User requested GDPR erasure"));

        // Lookup returns null because it is tombstoned
        var tombstone = await store.GetAsync(new MemoryLookup("session:s123", "key1"));
        Assert.Null(tombstone);

        // Resurrection attempt throws InvalidOperationException
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpsertAsync(request).AsTask());
    }
}
