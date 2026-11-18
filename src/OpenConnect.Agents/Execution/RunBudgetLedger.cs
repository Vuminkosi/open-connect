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
    private long _costMicrocents;
    private readonly long _microcentsPerThousandTokens;

    /// <summary>Creates a run ledger bounded by the run budget and, when supplied, the inherited residual budget.</summary>
    /// <param name="runBudget">The immutable run-wide limits.</param>
    /// <param name="parentRemainingBudgets">Optional narrower residual limits inherited from a parent.</param>
    /// <param name="microcentsPerThousandTokens">Cost rate applied to model-call token estimates as
    /// <c>ceil(tokens * rate / 1000)</c>. Zero (the default) disables cost debiting while preserving
    /// cost-limit enforcement for explicitly reserved cost.</param>
    public RunBudgetLedger(AgentBudget runBudget, AgentBudget? parentRemainingBudgets = null,
        long microcentsPerThousandTokens = 0)
    {
        ArgumentNullException.ThrowIfNull(runBudget);
        if (microcentsPerThousandTokens < 0)
            throw new ArgumentOutOfRangeException(nameof(microcentsPerThousandTokens), "Cost rate cannot be negative.");
        if (!IsValid(runBudget) || (parentRemainingBudgets is not null && !IsValid(parentRemainingBudgets)))
            throw new ArgumentOutOfRangeException(nameof(runBudget), "Budget limits cannot be negative.");

        _limit = parentRemainingBudgets is null ? runBudget : Intersect(runBudget, parentRemainingBudgets);
        _microcentsPerThousandTokens = microcentsPerThousandTokens;
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
                    Math.Max(0, _limit.MaxTotalAgents - _subagents),
                    MaxCostMicrocents: Math.Max(0, _limit.MaxCostMicrocents - _costMicrocents));
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

    /// <summary>Refunds one previously reserved child allowance without touching the active-child slot.</summary>
    /// <remarks>Only for pre-execution denials (rate-limit, authorization, or budget refusal) where the child never started: no model call was made and no MAF invocation occurred. The caller must still release the active slot via <see cref="ReleaseActiveChild"/>.</remarks>
    /// <returns>True when an allowance was refunded; false when none was held.</returns>
    public bool ReleaseSubagentReservation()
    {
        lock (_sync)
        {
            if (_subagents == 0) return false;
            _subagents--;
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

    /// <summary>Atomically reserves one model-call attempt, its token usage, and its metered cost.</summary>
    /// <remarks>Cost is metered as <c>ceil(tokens * rate / 1000)</c> from the ledger's configured rate.
    /// All three dimensions are checked before any debit, so denial leaves every counter unchanged.</remarks>
    /// <param name="tokens">Nonnegative token count for this attempt.</param>
    /// <returns>True when call, token, and cost capacity are all reserved; false with no debit otherwise.</returns>
    public bool TryReserveModelCall(long tokens)
    {
        if (tokens < 0) return false;
        lock (_sync)
        {
            if (_modelCalls >= _limit.MaxModelCalls || tokens > _limit.MaxTokens - _tokens) return false;
            long cost;
            try
            {
                cost = MeterCost(tokens);
            }
            catch (OverflowException)
            {
                return false;
            }
            if (cost > _limit.MaxCostMicrocents - _costMicrocents) return false;
            _modelCalls++;
            _tokens += tokens;
            _costMicrocents += cost;
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

    /// <summary>Atomically reserves cost capacity independently of a model-call reservation.</summary>
    /// <param name="microcents">Nonnegative cost in microcents to reserve.</param>
    /// <returns>True when reserved; false when invalid or exhausted, with no partial debit.</returns>
    public bool TryReserveCost(long microcents)
    {
        if (microcents < 0) return false;
        lock (_sync)
        {
            if (microcents > _limit.MaxCostMicrocents - _costMicrocents) return false;
            _costMicrocents += microcents;
            return true;
        }
    }

    private static bool IsValid(AgentBudget budget) => budget.MaxToolCalls >= 0 && budget.MaxModelCalls >= 0 &&
        budget.MaxTokens >= 0 && budget.MaxConcurrentAgents >= 0 && budget.MaxQueuedAgents >= 0 &&
        budget.MaxTotalAgents >= 0 && budget.MaxCostMicrocents >= 0 && (budget.MaxRuntime is null || budget.MaxRuntime >= TimeSpan.Zero);

    private static AgentBudget Intersect(AgentBudget left, AgentBudget right) => new(
        Math.Min(left.MaxToolCalls, right.MaxToolCalls), Math.Min(left.MaxModelCalls, right.MaxModelCalls),
        Math.Min(left.MaxTokens, right.MaxTokens), Min(left.MaxRuntime, right.MaxRuntime),
        Math.Min(left.MaxConcurrentAgents, right.MaxConcurrentAgents), Math.Min(left.MaxQueuedAgents, right.MaxQueuedAgents),
        Math.Min(left.MaxTotalAgents, right.MaxTotalAgents), Math.Min(left.MaxCostMicrocents, right.MaxCostMicrocents));

    /// <summary>Computes metered cost for a token estimate without mutating ledger state.</summary>
    /// <remarks>Must be called under <see cref="_sync"/>. A zero rate always meters zero cost.</remarks>
    private long MeterCost(long tokens)
    {
        if (tokens == 0 || _microcentsPerThousandTokens == 0) return 0;
        checked
        {
            return (tokens / 1000) * _microcentsPerThousandTokens +
                ((tokens % 1000) * _microcentsPerThousandTokens + 999) / 1000;
        }
    }

    private static TimeSpan? Min(TimeSpan? left, TimeSpan? right) => (left, right) switch
    {
        (null, null) => null,
        (null, var value) => value,
        (var value, null) => value,
        _ => left < right ? left : right
    };
}
