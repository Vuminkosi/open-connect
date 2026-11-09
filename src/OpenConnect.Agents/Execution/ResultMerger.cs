using OpenConnect.Abstractions;

namespace OpenConnect.Agents.Execution;

/// <summary>Validates child lineage and produces deterministic, audited orchestration results.</summary>
public static class ResultMerger
{
    /// <summary>Merges candidates in requested order and quarantines invalid or duplicate execution results.</summary>
    /// <param name="children">Candidate child results.</param>
    /// <param name="expectedParentExecutionId">Authoritative parent execution trace identifier.</param>
    /// <param name="expectedTenantScope">Authoritative tenant scope.</param>
    /// <param name="mode">Configured merge strategy.</param>
    /// <param name="validator">Additional host privacy and policy validation.</param>
    /// <param name="explicitReducer">Reducer required for explicit-reducer mode.</param>
    /// <returns>Accepted ordered results and quarantined candidates.</returns>
    public static MergedAgentResult Merge(IReadOnlyList<ChildResult> children, Guid expectedParentExecutionId,
        string expectedTenantScope, AgentResultMergeMode mode,
        Func<ChildResult, bool>? validator = null,
        Func<IReadOnlyList<ChildResult>, MergedAgentResult>? explicitReducer = null)
    {
        ArgumentNullException.ThrowIfNull(children);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedTenantScope);
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (mode == AgentResultMergeMode.ExplicitReducer && explicitReducer is null)
            throw new ArgumentNullException(nameof(explicitReducer), "Explicit reducer mode requires a reducer.");

        var accepted = new List<ChildResult>();
        var rejected = new List<QuarantinedChildResult>();
        var ids = new HashSet<ExecutionId>();
        foreach (var child in children.OrderBy(c => c.Order).ThenBy(c => c.SourceSequence))
        {
            var reason = child.ParentExecutionId != expectedParentExecutionId ? "invalid_parent_lineage"
                : child.SourceSequence < 0 ? "invalid_source_sequence"
                : !ids.Add(child.ExecutionId) ? "duplicate_execution"
                : !string.Equals(child.TenantScope, expectedTenantScope, StringComparison.Ordinal) ? "scope_mismatch"
                : validator is not null && !validator(child) ? "policy_or_privacy_rejected"
                : null;
            if (reason is not null) { rejected.Add(new(child, reason)); continue; }
            accepted.Add(child);
        }

        var audit = new MergeAudit(mode, children.Count, accepted.Count, expectedParentExecutionId,
            accepted.Select(c => c.SourceSequence).ToArray());
        var result = new MergedAgentResult(accepted, rejected, mode == AgentResultMergeMode.LeadSynthesizes, audit);
        if (mode != AgentResultMergeMode.ExplicitReducer) return result;
        var reduced = explicitReducer!(accepted) ?? throw new InvalidOperationException("Explicit reducer returned no result.");
        return reduced with { Audit = audit with { AcceptedCount = reduced.Results.Count } };
    }

}
