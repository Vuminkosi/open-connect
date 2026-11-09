using Microsoft.Agents.AI;
using OpenConnect.Abstractions;
using OpenConnect.Agents.Execution;
using Xunit;

namespace OpenConnect.Agents.Tests;

public sealed class ResultMergerTests
{
    private static readonly Guid Parent = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    [Fact]
    public void OrderedResults_AreSortedAndDuplicateExecutionIsQuarantined()
    {
        var first = Child("a", 0, 1);
        var duplicate = Child("b", 1, 2) with { ExecutionId = first.ExecutionId };
        var last = Child("c", 2, 3);
        var result = Merge([last, duplicate, first], AgentResultMergeMode.OrderedTypedResults);

        Assert.Equal(new[] { 0, 2 }, result.Results.Select(x => x.Order));
        Assert.Single(result.Quarantined);
        Assert.Equal("duplicate_execution", result.Quarantined[0].Reason);
    }

    [Fact]
    public void ExplicitReducer_RequiresReducerAndReturnsAuditedReduction()
    {
        var child = Child("a", 0, 1);
        Assert.Throws<ArgumentNullException>(() => Merge([child], AgentResultMergeMode.ExplicitReducer));

        var reduced = ResultMerger.Merge([child], Parent, "tenant", AgentResultMergeMode.ExplicitReducer,
            explicitReducer: accepted => new MergedAgentResult(accepted, [], false, new(AgentResultMergeMode.OrderedTypedResults, 1, 1, Parent, [1])));
        Assert.Equal(AgentResultMergeMode.ExplicitReducer, reduced.Audit.Mode);
        Assert.Equal(1, reduced.Audit.AcceptedCount);
    }

    [Fact]
    public void LeadSynthesizes_MarksSynthesisRequirement()
    {
        var result = Merge([Child("a", 0, 1)], AgentResultMergeMode.LeadSynthesizes);
        Assert.True(result.RequiresLeadSynthesis);
    }

    [Fact]
    public void Merge_QuarantinesMixedParentLineageAgainstAuthoritativeParent()
    {
        var valid = Child("a", 0, 0);
        var wrong = Child("b", 1, 1) with { ParentExecutionId = Guid.NewGuid() };
        var result = Merge([valid, wrong], AgentResultMergeMode.OrderedTypedResults);
        Assert.Single(result.Results);
        Assert.Equal("invalid_parent_lineage", Assert.Single(result.Quarantined).Reason);
        Assert.Equal(Parent, result.Audit.ParentExecutionId);
    }

    [Fact]
    public void Merge_QuarantinesEveryUniformlyWrongParent()
    {
        var wrongParent = Guid.NewGuid();
        var result = ResultMerger.Merge([Child("a", 0, 0) with { ParentExecutionId = wrongParent }],
            Parent, "tenant", AgentResultMergeMode.OrderedTypedResults);
        Assert.Empty(result.Results);
        Assert.Equal("invalid_parent_lineage", Assert.Single(result.Quarantined).Reason);
        Assert.Equal(Parent, result.Audit.ParentExecutionId);
    }

    [Fact]
    public void Merge_QuarantinesTenantMismatchAndNegativeSequence()
    {
        var result = Merge([Child("a", 0, -1), Child("b", 1, 1) with { TenantScope = "other" }],
            AgentResultMergeMode.OrderedTypedResults);
        Assert.Empty(result.Results);
        Assert.Equal(new[] { "invalid_source_sequence", "scope_mismatch" }, result.Quarantined.Select(x => x.Reason));
    }

    private static MergedAgentResult Merge(IReadOnlyList<ChildResult> children, AgentResultMergeMode mode) =>
        ResultMerger.Merge(children, Parent, "tenant", mode);

    private static ChildResult Child(string id, int order, long sequence) => new(
        new AgentId(id), new ExecutionId(Ulid.NewUlid()), new AgentResponse(), order, Parent, sequence,
        "tenant", new HashSet<string>(["read"], StringComparer.Ordinal));
}
