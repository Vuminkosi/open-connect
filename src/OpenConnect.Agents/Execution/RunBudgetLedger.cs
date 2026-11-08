using OpenConnect.Abstractions;

namespace OpenConnect.Agents.Execution;

/// <summary>Thread-safe accounting for limits shared by all executions in one agent run.</summary>
public sealed class RunBudgetLedger
{
    private readonly object _sync = new();
    private readonly AgentBudget _limit;
    private int _subagents;
    private int _activeChildren;
    private int _toolCalls;
    private int _modelCalls;
    private long _tokens;

    /// <summary>Creates a run ledger bounded by the run budget and, when supplied, the inherited residual budget.</summary>
    /// <param name="runBudget">The immutable run-wide limits.</param>
    /// <param name="parentRemainingBudgets">Optional narrower residual limits inherited from a parent.</param>
    public RunBudgetLedger(AgentBudget runBudget, AgentBudget? parentRemainingBudgets = null)
    {
        ArgumentNullException.ThrowIfNull(runBudget);
        if (!IsValid(runBudget) || (parentRemainingBudgets is not null && !IsValid(parentRemainingBudgets)))
            throw new ArgumentOutOfRangeException(nameof(runBudget), "Budget limits cannot be negative.");

        _limit = parentRemainingBudgets is null ? runBudget : Intersect(runBudget, parentRemainingBudgets);
    }

    /// <summary>Gets an immutable snapshot of remaining run-wide capacity.</summary>
    public AgentBudget Remaining
    {
        get
        {
            lock (_sync)
                return new AgentBudget(
                    Math.Max(0, _limit.MaxToolCalls - _toolCalls),
                    Math.Max(0, _limit.MaxModelCalls - _modelCalls),
                    Math.Max(0, _limit.MaxTokens - _tokens),
                    _limit.MaxRuntime,
                    _limit.MaxConcurrentAgents,
                    _limit.MaxQueuedAgents,
                    Math.Max(0, _limit.MaxTotalAgents - _subagents));
        }
    }

    /// <summary>
    /// Legacy budget-only reservation. Caller counts are advisory and are not ledger-owned; this method
    /// must not be used to admit an active child. Use <see cref="TryReserveSubagentWithSlot"/> instead.
    /// </summary>
    /// <param name="activeCount">Number of currently active children, including the proposed child if it starts immediately.</param>
    /// <param name="queuedCount">Number of children currently queued before this reservation.</param>
    /// <returns>True when reserved; false when any applicable limit is exhausted.</returns>
    public bool TryReserveSubagent(int activeCount, int queuedCount)
    {
        if (activeCount < 0 || queuedCount < 0) return false;
        lock (_sync)
        {
            if (_subagents >= _limit.MaxTotalAgents ||
                (activeCount > 0 && activeCount > _limit.MaxConcurrentAgents) ||
                (queuedCount > 0 && queuedCount >= _limit.MaxQueuedAgents)) return false;
            _subagents++;
            return true;
        }
    }

    /// <summary>Atomically reserves one child allowance and one active slot using ledger-owned counters.</summary>
    /// <returns>True when both resources are reserved; false with no partial reservation otherwise.</returns>
    public bool TryReserveSubagentWithSlot()
    {
        lock (_sync)
        {
            if (_subagents >= _limit.MaxTotalAgents || _activeChildren >= _limit.MaxConcurrentAgents)
                return false;

            _subagents++;
            _activeChildren++;
            return true;
        }
    }

    /// <summary>
    /// Acquires an active-child slot without reserving child budget. For admission, use
    /// <see cref="TryReserveSubagentWithSlot"/> to avoid partial reservations and caller-count races.
    /// </summary>
    /// <returns>True when a slot is available; false when the concurrent-child limit is exhausted.</returns>
    public bool TryAcquireActiveChild()
    {
        lock (_sync)
        {
            if (_activeChildren >= _limit.MaxConcurrentAgents) return false;
            _activeChildren++;
            return true;
        }
    }

    /// <summary>Releases a previously acquired active-child slot. Callers must release in a finally block.</summary>
    /// <returns>True when a slot was released; false when no slot was held.</returns>
    public bool ReleaseActiveChild()
    {
        lock (_sync)
        {
            if (_activeChildren == 0) return false;
            _activeChildren--;
            return true;
        }
    }

    /// <summary>Atomically reserves one tool-call attempt.</summary>
    /// <returns>True when reserved; false when the run tool-call limit is exhausted.</returns>
    public bool TryReserveToolCall()
    {
        lock (_sync)
        {
            if (_toolCalls >= _limit.MaxToolCalls) return false;
            _toolCalls++;
            return true;
        }
    }

    /// <summary>Atomically reserves one model-call attempt and its token usage.</summary>
    /// <param name="tokens">Nonnegative token count for this attempt.</param>
    /// <returns>True when both call and token capacity are reserved; false with no debit otherwise.</returns>
    public bool TryReserveModelCall(long tokens)
    {
        if (tokens < 0) return false;
        lock (_sync)
        {
            if (_modelCalls >= _limit.MaxModelCalls || tokens > _limit.MaxTokens - _tokens) return false;
            _modelCalls++;
            _tokens += tokens;
            return true;
        }
    }

    /// <summary>Atomically reserves token capacity independently of a model-call reservation.</summary>
    /// <param name="tokens">Nonnegative token count to reserve.</param>
    /// <returns>True when reserved; false when invalid or exhausted.</returns>
    public bool TryReserveTokens(long tokens)
    {
        if (tokens < 0) return false;
        lock (_sync)
        {
            if (tokens > _limit.MaxTokens - _tokens) return false;
            _tokens += tokens;
            return true;
        }
    }

    private static bool IsValid(AgentBudget budget) => budget.MaxToolCalls >= 0 && budget.MaxModelCalls >= 0 &&
        budget.MaxTokens >= 0 && budget.MaxConcurrentAgents >= 0 && budget.MaxQueuedAgents >= 0 &&
        budget.MaxTotalAgents >= 0 && budget.MaxRuntime >= TimeSpan.Zero;

    private static AgentBudget Intersect(AgentBudget left, AgentBudget right) => new(
        Math.Min(left.MaxToolCalls, right.MaxToolCalls), Math.Min(left.MaxModelCalls, right.MaxModelCalls),
        Math.Min(left.MaxTokens, right.MaxTokens), Min(left.MaxRuntime, right.MaxRuntime),
        Math.Min(left.MaxConcurrentAgents, right.MaxConcurrentAgents), Math.Min(left.MaxQueuedAgents, right.MaxQueuedAgents),
        Math.Min(left.MaxTotalAgents, right.MaxTotalAgents));

    private static TimeSpan? Min(TimeSpan? left, TimeSpan? right) => (left, right) switch
    {
        (null, null) => null,
        (null, var value) => value,
        (var value, null) => value,
        _ => left < right ? left : right
    };
}
