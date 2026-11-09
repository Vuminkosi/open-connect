namespace OpenConnect.Agents.Execution;

/// <summary>Result of deterministic memory compaction with source lineage.</summary>
public sealed record CompactedMemory(string Text, IReadOnlyList<string> SourceLineage, int TokenEstimate,
    IReadOnlyList<string> TruncatedItems, string CheckpointProvenance);

/// <summary>Builds a bounded context while preserving non-droppable safety and workflow state.</summary>
public static class MemoryCompaction
{
    /// <summary>Compacts ordered context sections; lower-priority content is removed first.</summary>
    /// <param name="recentTurns">Recent conversation turns, lowest priority first within the section.</param>
    /// <param name="prioritizedDecisions">Decisions in priority order, highest priority first.</param>
    /// <param name="handoffSummary">Recoverable handoff/checkpoint summary.</param>
    /// <param name="approvedMemories">Approved session memories.</param>
    /// <param name="tokenBudget">Maximum estimated tokens.</param>
    /// <param name="mustRetain">Unresolved approvals, commitments, authorization restrictions, and failed outcomes.</param>
    /// <param name="approvalRefs">Approval and pending-HITL references that must survive compaction.</param>
    /// <returns>Compacted content and source lineage.</returns>
    /// <exception cref="InvalidOperationException">Required content exceeds the budget.</exception>
    public static CompactedMemory Compact(IReadOnlyList<string> recentTurns, IReadOnlyList<string> prioritizedDecisions,
        string handoffSummary, IReadOnlyList<string> approvedMemories, int tokenBudget, IReadOnlyList<string> mustRetain,
        IReadOnlyList<string>? approvalRefs = null)
    {
        ArgumentNullException.ThrowIfNull(recentTurns);
        ArgumentNullException.ThrowIfNull(prioritizedDecisions);
        ArgumentNullException.ThrowIfNull(approvedMemories);
        ArgumentNullException.ThrowIfNull(mustRetain);
        approvalRefs ??= [];
        ArgumentException.ThrowIfNullOrWhiteSpace(handoffSummary);
        if (tokenBudget < 0) throw new ArgumentOutOfRangeException(nameof(tokenBudget));
        var required = mustRetain.Select((text, i) => NewItem(text, $"must-retain:{i}", true)).ToList();
        required.AddRange(approvalRefs.Select((text, i) => NewItem(text, $"approval:{i}", true)));
        var candidates = new List<Item>();
        candidates.AddRange(prioritizedDecisions.Select((text, i) => NewItem(text, $"decision:{i}")));
        candidates.Add(NewItem(handoffSummary, "handoff"));
        candidates.AddRange(approvedMemories.Select((text, i) => NewItem(text, $"approved-memory:{i}")));
        candidates.AddRange(recentTurns.Select((text, i) => NewItem(text, $"recent-turn:{i}")));
        // Workflow references remain mandatory even when supplied inside an otherwise optional section.
        required.AddRange(candidates.Where(item => IsApprovalReference(item.Text))
            .Select(item => item with { Required = true }));
        var optional = candidates.Where(item => !IsApprovalReference(item.Text)).ToList();

        if (Estimate(required) > tokenBudget)
            throw new InvalidOperationException($"Required memory items exceed token budget {tokenBudget}.");
        var included = new List<Item>(required);
        foreach (var item in optional)
        {
            if (Estimate(included.Append(item)) <= tokenBudget) included.Add(item);
        }
        var includedSet = included.ToHashSet();
        var truncated = optional.Where(item => !includedSet.Contains(item)).Select(item => item.Lineage).ToArray();
        var text = string.Join(Environment.NewLine, included.Select(item => item.Text));
        var lineage = included.Select(item => item.Lineage).ToArray();
        return new CompactedMemory(text, lineage, Estimate(included), truncated,
            $"session-checkpoint/v1; sources={string.Join(',', lineage)}");
    }

    private static Item NewItem(string text, string lineage, bool required = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        return new Item(text, lineage, required);
    }

    private static int Estimate(IEnumerable<Item> items) => (int)Math.Ceiling(items.Sum(item => item.Text.Length) / 4d);
    private static bool IsApprovalReference(string text) =>
        text.Contains("approval", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("pending-hitl", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("pending hitl", StringComparison.OrdinalIgnoreCase);
    private sealed record Item(string Text, string Lineage, bool Required = false);
}
