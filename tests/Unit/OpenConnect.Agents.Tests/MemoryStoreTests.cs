using OpenConnect.Abstractions;
using OpenConnect.Agents.Execution;
using Xunit;

namespace OpenConnect.Agents.Tests;

public sealed class MemoryStoreTests
{
    [Fact]
    public async Task DenyByDefault_RequiresConsent_AndExplicitApprovedRequiresConsent()
    {
        var store = new InMemorySessionMemoryStore();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.UpsertAsync(Write()).AsTask());
        var document = await store.UpsertAsync(Write() with { ConsentToken = "approved" });
        Assert.Equal(1, document.Revision);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.UpsertAsync(Write() with
            { WritePolicy = AgentMemoryWritePolicy.ExplicitApproved }).AsTask());
    }

    [Fact]
    public async Task ApplicationPolicy_OnlyAllowsConfiguredDataClasses()
    {
        var store = new InMemorySessionMemoryStore(["session"]);
        Assert.Equal("session", (await store.UpsertAsync(Write() with
            { WritePolicy = AgentMemoryWritePolicy.ApplicationPolicy })).DataClass);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.UpsertAsync(Write() with
            { DataClass = "sensitive", WritePolicy = AgentMemoryWritePolicy.ApplicationPolicy }).AsTask());
    }

    [Fact]
    public async Task Upsert_IncrementsRevision_AndLookupCannotCrossScope()
    {
        var store = new InMemorySessionMemoryStore();
        var first = await store.UpsertAsync(Write() with { ConsentToken = "yes" });
        var second = await store.UpsertAsync(Write() with { ConsentToken = "yes", Summary = "updated" });
        Assert.Equal(first.Revision + 1, second.Revision);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.GetAsync(new("tenant-b", "memory")).AsTask());
    }

    [Fact]
    public async Task ExpiredMemory_IsTombstonedAndCannotBeResurrected()
    {
        var clock = new TestTimeProvider();
        var store = new InMemorySessionMemoryStore(timeProvider: clock);
        await store.UpsertAsync(Write() with { ConsentToken = "yes", Ttl = TimeSpan.FromSeconds(1) });
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(await store.GetAsync(new("tenant-a", "memory")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpsertAsync(Write() with { ConsentToken = "yes" }).AsTask());
    }

    [Fact]
    public async Task Delete_RetainsReasonInAuthoritativeTombstone()
    {
        var store = new InMemorySessionMemoryStore();
        await store.UpsertAsync(Write() with { ConsentToken = "yes" });
        await store.DeleteAsync(new("tenant-a", "memory", "user requested deletion"));
        Assert.Null(await store.GetAsync(new("tenant-a", "memory")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpsertAsync(Write() with { ConsentToken = "yes" }).AsTask());
    }

    [Fact]
    public void Compaction_RespectsBudgetAndRetainsLineage()
    {
        var result = MemoryCompaction.Compact(["recent turn"], ["decision"], "handoff", ["approved"], 12, ["approval pending"]);
        Assert.True(result.TokenEstimate <= 12);
        Assert.Contains("must-retain:0", result.SourceLineage);
        Assert.Contains("session-checkpoint/v1", result.CheckpointProvenance);
        Assert.NotEmpty(result.TruncatedItems);
    }

    [Fact]
    public void Compaction_ThrowsRatherThanDroppingMustRetainItems()
    {
        var error = Assert.Throws<InvalidOperationException>(() => MemoryCompaction.Compact([], [], "handoff", [], 1,
            ["unresolved approval that must remain"]));
        Assert.Contains("budget 1", error.Message);
    }

    [Fact]
    public void Compaction_PreservesApprovalReferencesAndTheirLineageUnderBudgetPressure()
    {
        var result = MemoryCompaction.Compact(["optional recent turn"], [], "handoff", [], 10, [],
            ["pending-HITL: approval-42"]);

        Assert.Contains("pending-HITL: approval-42", result.Text);
        Assert.Contains("approval:0", result.SourceLineage);
        Assert.Contains("recent-turn:0", result.TruncatedItems);
    }

    [Fact]
    public void Compaction_ThrowsWhenApprovalReferencesExceedBudget()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            MemoryCompaction.Compact([], [], "handoff", [], 1, [], ["pending-HITL: approval-42"]));

        Assert.Contains("Required memory items exceed token budget 1", error.Message);
    }

    [Fact]
    public void Compaction_OptionalNonApprovalItemsRemainTruncatable()
    {
        var result = MemoryCompaction.Compact(["optional context"], [], "handoff", [], 2, []);

        Assert.DoesNotContain("optional context", result.Text);
        Assert.Contains("recent-turn:0", result.TruncatedItems);
    }

    private static MemoryWriteRequest Write() => new("tenant-a", "memory", "summary", "session", "turn:4", null);

    private sealed class TestTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
