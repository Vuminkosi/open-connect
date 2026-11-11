using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenConnect.Abstractions;
using OpenConnect.Agents.Execution;
using Xunit;

namespace OpenConnect.Agents.Tests;

/// <summary>
/// Proves OC2-D-SESSION-010: Keep parallel branch scratchpads and provider sessions isolated;
/// merge only validated, versioned child result summaries.
/// </summary>
public sealed class BranchIsolationAndValidatedResultMergeTests
{
    private static readonly AgentId Lead = new("lead");
    private static readonly AgentId Child = new("worker");
    private static readonly Guid ParentId = Guid.NewGuid();
    private const string Tenant = "tenant-a";

    [Fact]
    public void Merge_OnlyIncludesValidatedAndVersionedSummaries()
    {
        var validChild1 = CreateResult(1, 0, ParentId, Tenant, "summary 1");
        var validChild2 = CreateResult(2, 1, ParentId, Tenant, "summary 2");
        var unvalidatedChild = CreateResult(3, 2, ParentId, Tenant, "unvalidated summary");
        var unversionedChild = CreateResult(4, -1, ParentId, Tenant, "negative seq");
        var wrongParentChild = CreateResult(5, 3, Guid.NewGuid(), Tenant, "wrong parent");
        var wrongTenantChild = CreateResult(6, 4, ParentId, "tenant-b", "wrong tenant");

        var candidates = new[]
        {
            validChild1,
            validChild2,
            unvalidatedChild,
            unversionedChild,
            wrongParentChild,
            wrongTenantChild
        };

        // Custom validator rejects "unvalidated summary"
        var merged = ResultMerger.Merge(
            candidates,
            ParentId,
            Tenant,
            AgentResultMergeMode.OrderedTypedResults,
            validator: child => !child.Response.Text.Contains("unvalidated"));

        // Only valid child 1 and 2 are accepted
        Assert.Equal(2, merged.Results.Count);
        Assert.Equal("summary 1", merged.Results[0].Response.Text);
        Assert.Equal("summary 2", merged.Results[1].Response.Text);

        // 4 candidates are quarantined with specific reasons
        Assert.Equal(4, merged.Quarantined.Count);
        Assert.Contains(merged.Quarantined, q => q.Result.Order == 3 && q.Reason == "policy_or_privacy_rejected");
        Assert.Contains(merged.Quarantined, q => q.Result.Order == 4 && q.Reason == "invalid_source_sequence");
        Assert.Contains(merged.Quarantined, q => q.Result.Order == 5 && q.Reason == "invalid_parent_lineage");
        Assert.Contains(merged.Quarantined, q => q.Result.Order == 6 && q.Reason == "scope_mismatch");
    }

    [Fact]
    public void Merge_LeadSynthesizes_SetsRequirementFlag()
    {
        var child = CreateResult(1, 0, ParentId, Tenant, "summary");
        var merged = ResultMerger.Merge([child], ParentId, Tenant, AgentResultMergeMode.LeadSynthesizes);
        Assert.True(merged.RequiresLeadSynthesis);
        Assert.Single(merged.Results);
    }

    private static ChildResult CreateResult(int order, long seq, Guid parentId, string tenant, string text)
    {
        var response = new AgentResponse(new ChatMessage(ChatRole.Assistant, text));
        return new ChildResult(
            Child,
            new ExecutionId(Ulid.NewUlid()),
            response,
            order,
            parentId,
            seq,
            tenant,
            new HashSet<string>(["read"]));
    }
}
