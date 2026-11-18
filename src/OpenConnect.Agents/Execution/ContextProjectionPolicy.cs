using OpenConnect.Abstractions;

namespace OpenConnect.Agents.Execution;

/// <summary>Defines approved context projection boundaries, size limits, and TTL policies.</summary>
public static class ContextProjectionBounds
{
    /// <summary>Maximum allowable token budget for a task summary projection.</summary>
    public const int MaxTaskSummaryTokenBudget = 8192;

    /// <summary>Maximum allowable token budget for relevant history projection.</summary>
    public const int MaxRelevantHistoryTokenBudget = 16384;

    /// <summary>Maximum allowable token budget for approved artifacts projection.</summary>
    public const int MaxApprovedArtifactsTokenBudget = 32768;

    /// <summary>Maximum allowable token budget for scoped memory projection.</summary>
    public const int MaxScopedMemoryTokenBudget = 8192;

    /// <summary>Maximum permitted TTL for any context projection (24 hours).</summary>
    public static readonly TimeSpan MaxAllowableTtl = TimeSpan.FromHours(24);

    /// <summary>Minimum permitted TTL for any context projection (1 second).</summary>
    public static readonly TimeSpan MinAllowableTtl = TimeSpan.FromSeconds(1);

    /// <summary>Gets the maximum token budget permitted for the specified context sharing mode.</summary>
    public static int GetMaxBudget(AgentContextSharingMode mode) => mode switch
    {
        AgentContextSharingMode.None => 0,
        AgentContextSharingMode.TaskSummary => MaxTaskSummaryTokenBudget,
        AgentContextSharingMode.RelevantHistory => MaxRelevantHistoryTokenBudget,
        AgentContextSharingMode.ApprovedArtifacts => MaxApprovedArtifactsTokenBudget,
        AgentContextSharingMode.ScopedMemory => MaxScopedMemoryTokenBudget,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), $"Unrecognized context sharing mode: {mode}")
    };

    /// <summary>Validates that the requested context projection parameters adhere to approved bounds.</summary>
    /// <param name="mode">The context sharing mode.</param>
    /// <param name="tokenBudget">The requested token budget.</param>
    /// <param name="ttl">The requested projection TTL.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when limits or TTL violate bounds.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when sharing mode is None but a budget is requested.</exception>
    public static void ValidateBounds(AgentContextSharingMode mode, int tokenBudget, TimeSpan ttl)
    {
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode), $"Invalid mode: {mode}");

        if (tokenBudget < 0)
            throw new ArgumentOutOfRangeException(nameof(tokenBudget), "Token budget must be non-negative.");

        if (mode == AgentContextSharingMode.None && tokenBudget > 0)
            throw new UnauthorizedAccessException("Context sharing mode is None; token budget must be zero.");

        var maxBudget = GetMaxBudget(mode);
        if (tokenBudget > maxBudget)
            throw new ArgumentOutOfRangeException(nameof(tokenBudget),
                $"Requested token budget {tokenBudget} exceeds maximum allowable budget of {maxBudget} for mode {mode}.");

        if (ttl < MinAllowableTtl || ttl > MaxAllowableTtl)
            throw new ArgumentOutOfRangeException(nameof(ttl),
                $"Requested TTL {ttl} must be between {MinAllowableTtl} and {MaxAllowableTtl}.");
    }
}
