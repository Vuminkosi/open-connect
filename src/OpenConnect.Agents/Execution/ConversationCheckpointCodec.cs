namespace OpenConnect.Agents.Execution;

/// <summary>Opaque checkpoint metadata for resuming a conversation's monotonic turn counter.</summary>
public sealed record ConversationCheckpoint(
    string Version,
    string LeadAgentId,
    string SessionKey,
    long TurnSequence,
    IReadOnlyList<ChildSessionCheckpoint> ChildSessions);

/// <summary>Metadata identifying a child session without copying its MAF history.</summary>
public sealed record ChildSessionCheckpoint(string AgentId, string SessionKey, int HistoryCount);

/// <summary>Creates and validates versioned conversation checkpoint metadata.</summary>
public static class ConversationCheckpointCodec
{
    /// <summary>Current checkpoint wire-format version.</summary>
    public const string CurrentVersion = "oc-checkpoint/v1";

    // Run budgets and delegation policy have no single positive default child count (the budget default
    // is zero and the gate's limits vary by execution). Keep opaque checkpoint metadata independently bounded.
    /// <summary>Maximum number of opaque child-session references accepted in a checkpoint.</summary>
    public const int MaxCheckpointChildren = 64;

    /// <summary>Creates an immutable checkpoint. No MAF transcript content is included.</summary>
    public static ConversationCheckpoint Export(string leadAgentId, string sessionKey, long turnSequence,
        IEnumerable<ChildSessionCheckpoint> childSessions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leadAgentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);
        if (turnSequence < 0 || turnSequence == long.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(turnSequence), "Turn sequence must allow a next monotonic value.");
        ArgumentNullException.ThrowIfNull(childSessions);
        var children = childSessions.ToArray();
        ValidateChildCount(children.Length, nameof(childSessions));
        if (children.Any(child => child is null || string.IsNullOrWhiteSpace(child.AgentId) ||
                string.IsNullOrWhiteSpace(child.SessionKey) || child.HistoryCount < 0))
            throw new ArgumentException("Child session checkpoint metadata is invalid.", nameof(childSessions));
        if (children.Select(child => (child.AgentId, child.SessionKey)).Distinct().Count() != children.Length)
            throw new ArgumentException("Child agent and session key pairs must be unique within a checkpoint.", nameof(childSessions));

        return new ConversationCheckpoint(CurrentVersion, leadAgentId, sessionKey, turnSequence, Array.AsReadOnly(children));
    }

    /// <summary>Validates checkpoint compatibility and expected lead/session ownership.</summary>
    public static ConversationCheckpoint Import(ConversationCheckpoint checkpoint, string expectedLeadAgentId,
        string expectedSessionKey)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedLeadAgentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSessionKey);
        if (!StringComparer.Ordinal.Equals(checkpoint.Version, CurrentVersion))
            throw new NotSupportedException(
                $"Checkpoint version '{checkpoint.Version}' is not supported; expected '{CurrentVersion}'. " +
                "Export a new checkpoint from the current runner; cross-version restore is not supported.");
        if (!StringComparer.Ordinal.Equals(checkpoint.LeadAgentId, expectedLeadAgentId) ||
            !StringComparer.Ordinal.Equals(checkpoint.SessionKey, expectedSessionKey))
            throw new InvalidOperationException("Checkpoint belongs to a different lead agent or conversation session.");
        if (checkpoint.TurnSequence < 0 || checkpoint.TurnSequence == long.MaxValue)
            throw new ArgumentException("Checkpoint metadata is invalid.", nameof(checkpoint));
        ArgumentNullException.ThrowIfNull(checkpoint.ChildSessions);
        ValidateChildCount(checkpoint.ChildSessions.Count, nameof(checkpoint));
        if (checkpoint.ChildSessions.Any(child => child is null || string.IsNullOrWhiteSpace(child.AgentId) ||
                string.IsNullOrWhiteSpace(child.SessionKey) || child.HistoryCount < 0) ||
            checkpoint.ChildSessions.Select(child => (child.AgentId, child.SessionKey)).Distinct().Count() != checkpoint.ChildSessions.Count)
            throw new ArgumentException("Checkpoint metadata is invalid.", nameof(checkpoint));

        return checkpoint;
    }

    private static void ValidateChildCount(int actualCount, string parameterName)
    {
        if (actualCount > MaxCheckpointChildren)
            throw new ArgumentException(
                $"Checkpoint child count exceeds the maximum of {MaxCheckpointChildren}; actual count is {actualCount}.",
                parameterName);
    }
}
