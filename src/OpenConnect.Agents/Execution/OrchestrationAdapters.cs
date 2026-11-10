using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenConnect.Abstractions;
using System.Text;

namespace OpenConnect.Agents.Execution;

/// <summary>Describes the settled outcomes of a parallel child run that did not complete successfully.</summary>
public sealed class ParallelPartialFailureException : Exception
{
    /// <summary>Creates a failure carrying the outcomes of every settled child.</summary>
    public ParallelPartialFailureException(string failedChildId, Exception error,
        IReadOnlyList<string> completedIds, IReadOnlyList<string> cancelledIds)
        : base($"Parallel child '{failedChildId}' failed.", error)
    {
        FailedChildId = failedChildId;
        Error = error;
        CompletedIds = completedIds;
        CancelledIds = cancelledIds;
    }

    /// <summary>Gets the identifier of the failed child.</summary>
    public string FailedChildId { get; }
    /// <summary>Gets the original failure.</summary>
    public Exception Error { get; }
    /// <summary>Gets the identifiers of children that completed successfully.</summary>
    public IReadOnlyList<string> CompletedIds { get; }
    /// <summary>Gets the identifiers of children cancelled before completion.</summary>
    public IReadOnlyList<string> CancelledIds { get; }
}

/// <summary>Checks current authorization immediately before an execution effect.</summary>
/// <param name="context">Execution context to verify.</param>
/// <param name="cancellationToken">Cancellation token for the check.</param>
/// <returns>True when authorization has been revoked.</returns>
public delegate ValueTask<bool> IsExecutionRevoked(AgentExecutionContext context, CancellationToken cancellationToken);

/// <summary>Runs orchestration using the installed Microsoft Agent Framework AIAgent.RunAsync surface.</summary>
public sealed class OrchestrationAdapters
{
    private readonly MafRealAgentFactory _factory;
    private readonly MafConversationRunner _conversation;
    private readonly Func<AgentExecutionContext, string, CancellationToken, ValueTask<OpenConnect.Abstractions.DelegationGrant?>> _gate;
    private readonly IsExecutionRevoked _isRevoked;
    private readonly int _maxConcurrentSubagents;
    private readonly RunBudgetLedger? _budgetLedger;
    private readonly IRateLimitGate? _rateLimitGate;

    /// <summary>Creates adapters that execute agents and enforce host authorization.</summary>
    /// <param name="factory">Factory for creating agent instances.</param>
    /// <param name="conversation">Runner that executes and records child conversations.</param>
    /// <param name="gate">Gate used to authorize delegated capabilities.</param>
    /// <param name="isRevoked">Callback that checks current authorization state.</param>
    /// <param name="maxConcurrentSubagents">Maximum simultaneous child executions.</param>
    /// <param name="budgetLedger">Optional run-wide ledger shared by all orchestration adapters in this run.</param>
    /// <param name="rateLimitGate">Optional tenant/application rate gate for direct execution. Child delegations are gated exactly once inside the conversation runner with the forwarded tenant; hosts must configure the runner gate to enforce child limits.</param>
    public OrchestrationAdapters(MafRealAgentFactory factory, MafConversationRunner conversation,
        Func<AgentExecutionContext, string, CancellationToken, ValueTask<OpenConnect.Abstractions.DelegationGrant?>> gate,
        IsExecutionRevoked isRevoked, int maxConcurrentSubagents, RunBudgetLedger? budgetLedger = null,
        IRateLimitGate? rateLimitGate = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _conversation = conversation ?? throw new ArgumentNullException(nameof(conversation));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _isRevoked = isRevoked ?? throw new ArgumentNullException(nameof(isRevoked));
        if (maxConcurrentSubagents < 1) throw new ArgumentOutOfRangeException(nameof(maxConcurrentSubagents));
        _maxConcurrentSubagents = maxConcurrentSubagents;
        _budgetLedger = budgetLedger;
        _rateLimitGate = rateLimitGate;
    }

    /// <summary>Runs an agent directly.</summary>
    /// <param name="agentId">Agent to run.</param>
    /// <param name="prompt">Input prompt.</param>
    /// <param name="context">Host-authorized execution context.</param>
    /// <returns>The agent response.</returns>
    public Task<AgentResponse> DirectRunner(AgentId agentId, string prompt, AgentExecutionContext context) =>
        RunAgentAsync(agentId, prompt, context);

    /// <summary>Runs authorized child agents one at a time in request order.</summary>
    /// <param name="parent">Parent execution identifier.</param>
    /// <param name="grants">Authorized child grants.</param>
    /// <param name="prompt">Prompt supplied to each child.</param>
    /// <param name="context">Parent execution context.</param>
    /// <returns>Child results in request order.</returns>
    public async Task<MergedAgentResult> SequentialRunner(ExecutionId parent, IReadOnlyList<OpenConnect.Abstractions.DelegationGrant> grants,
        string prompt, AgentExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(grants);
        var results = new List<ChildResult>(grants.Count);
        for (var i = 0; i < grants.Count; i++)
        {
            var grant = grants[i];
            using var child = CreateChild(context, grant);
            var response = await RunChildAsync(parent, grant.ChildAgentId, prompt, child).ConfigureAwait(false);
            results.Add(ToChildResult(grant, child, response, i));
        }
        return ResultMerger.Merge(results, ToTraceId(parent), context.TenantScope, AgentResultMergeMode.OrderedTypedResults);
    }

    /// <summary>Runs authorized children with bounded concurrency and returns results in request order.</summary>
    /// <param name="parent">Parent execution identifier.</param>
    /// <param name="grants">Authorized child grants.</param>
    /// <param name="prompt">Prompt supplied to each child.</param>
    /// <param name="context">Parent execution context.</param>
    /// <param name="mergeMode">Requested result merge mode to validate.</param>
    /// <returns>Child results in request order.</returns>
    public async Task<MergedAgentResult> ParallelRunner(ExecutionId parent, IReadOnlyList<OpenConnect.Abstractions.DelegationGrant> grants,
        string prompt, AgentExecutionContext context, AgentResultMergeMode mergeMode)
    {
        ArgumentNullException.ThrowIfNull(grants);
        ValidateMergeMode(mergeMode);
        using var scopeCancellation = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        using var limiter = new SemaphoreSlim(_maxConcurrentSubagents);
        var tasks = grants.Select(async (grant, index) =>
        {
            try { await limiter.WaitAsync(scopeCancellation.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return ChildOutcome.Cancelled(grant.ChildAgentId.Value); }
            try
            {
                try
                {
                    using var child = CreateChild(context, grant, scopeCancellation.Token);
                    // Every child gets its own AIAgent instance; no mutable conversation history is shared.
                    var response = await RunChildAsync(parent, grant.ChildAgentId, prompt, child).ConfigureAwait(false);
                    return ChildOutcome.Completed(grant.ChildAgentId.Value, ToChildResult(grant, child, response, index));
                }
                catch
                {
                    scopeCancellation.Cancel();
                    throw;
                }
            }
            catch (OperationCanceledException) when (scopeCancellation.IsCancellationRequested)
            {
                return ChildOutcome.Cancelled(grant.ChildAgentId.Value);
            }
            catch (Exception exception)
            {
                scopeCancellation.Cancel();
                return ChildOutcome.Failed(grant.ChildAgentId.Value, exception);
            }
            finally { limiter.Release(); }
        }).ToArray();
        var outcomes = await Task.WhenAll(tasks).ConfigureAwait(false);
        var failures = outcomes.Where(outcome => outcome.Error is not null).ToArray();
        if (failures.Length > 0)
        {
            var failure = failures[0];
            throw new ParallelPartialFailureException(failure.ChildId, failure.Error!,
                outcomes.Where(outcome => outcome.Result is not null).Select(outcome => outcome.ChildId).ToArray(),
                outcomes.Where(outcome => outcome.IsCancelled).Select(outcome => outcome.ChildId).ToArray());
        }
        context.CancellationToken.ThrowIfCancellationRequested();
        return ResultMerger.Merge(outcomes.Where(outcome => outcome.Result is not null).Select(outcome => outcome.Result!).ToArray(),
            ToTraceId(parent), context.TenantScope, mergeMode);
    }

    /// <summary>Handoff transfers only the caller-supplied projection; shared conversation history is not passed to the child.</summary>
    /// <param name="parent">Parent execution identifier.</param>
    /// <param name="grant">Authorized child grant.</param>
    /// <param name="explicitProjection">Explicit context projection for the child.</param>
    /// <param name="context">Parent execution context.</param>
    /// <returns>The child response.</returns>
    public async Task<AgentResponse> HandoffRunner(ExecutionId parent, OpenConnect.Abstractions.DelegationGrant grant, string explicitProjection,
        AgentExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentNullException.ThrowIfNull(explicitProjection);
        using var child = CreateChild(context, grant);
        return await RunChildAsync(parent, grant.ChildAgentId, explicitProjection, child).ConfigureAwait(false);
    }

    /// <summary>Authorizes and runs one child selected for a capability.</summary>
    /// <param name="parent">Parent execution identifier.</param>
    /// <param name="capability">Capability used to select an authorized child.</param>
    /// <param name="prompt">Prompt supplied to the child.</param>
    /// <param name="context">Parent execution context.</param>
    /// <param name="order">Result ordering value.</param>
    /// <returns>The child result.</returns>
    public async Task<ChildResult> SupervisorRunner(ExecutionId parent, string capability, string prompt,
        AgentExecutionContext context, int order = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capability);
        var grant = await _gate(context, capability, context.CancellationToken).ConfigureAwait(false)
            ?? throw new UnauthorizedAccessException("No authorized child agent is available for this capability.");
        using var child = CreateChild(context, grant);
        var response = await RunChildAsync(parent, grant.ChildAgentId, prompt, child).ConfigureAwait(false);
        return ToChildResult(grant, child, response, order);
    }

    /// <summary>Rejects profiles that require an orchestration primitive this host adapter does not implement.</summary>
    /// <param name="mode">Requested orchestration mode.</param>
    /// <param name="profileSupported">Whether the configured host supports that profile.</param>
    public static void ValidateProfile(AgentOrchestrationMode mode, bool profileSupported)
    {
        if (profileSupported && mode != AgentOrchestrationMode.Direct)
            OrchestrationProfileRouter.ValidateRuntimeProfile("Workflow");

        if (!profileSupported) throw new InvalidOperationException($"The {mode} orchestration profile is not supported by the configured host.");
    }

    private async Task<AgentResponse> RunAgentAsync(AgentId agentId, string prompt, AgentExecutionContext context)
    {
        await EnsureAuthorizedAsync(context).ConfigureAwait(false);
        ThrowIfRateLimited(context, agentId.Value);
        if (_budgetLedger is not null && !_budgetLedger.TryReserveModelCall(Encoding.UTF8.GetByteCount(prompt)))
            throw new InvalidOperationException("run-wide model budget exhausted");
        return await _factory.CreateAgent(agentId).RunAsync(prompt, cancellationToken: context.CancellationToken).ConfigureAwait(false);
    }

    private async Task<AgentResponse> RunChildAsync(ExecutionId parent, AgentId agentId, string prompt, AgentExecutionContext child)
    {
        await EnsureAuthorizedAsync(child).ConfigureAwait(false);
        AcquireSubagentBudget();
        try
        {
            // MafConversationRunner records the actual parent/child execution and invokes the MAF AIAgent.
            // It owns the single tenant-attributed gate evaluation and the single model-call reservation;
            // do not gate or reserve here as well. The child tenant is forwarded so legitimate gated
            // traffic is attributable instead of universally denied; truly unattributed calls still deny.
            return await _conversation.DelegateAsync(ToTraceId(parent), agentId, prompt, child.CancellationToken,
                childSessionKey: null, tenant: child.TenantScope).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception) when (IsRateLimitDenial(exception))
        {
            // The gate denied before any model reservation or MAF invocation: refund the child
            // allowance so a denied delegation leaves ledger.Remaining unchanged and makes zero MAF calls.
            _budgetLedger?.ReleaseSubagentReservation();
            throw;
        }
        finally { _budgetLedger?.ReleaseActiveChild(); }
    }

    /// <summary>Identifies a tenant/application rate-gate denial by its stable fail-closed message.</summary>
    private static bool IsRateLimitDenial(InvalidOperationException exception) =>
        exception.Message.Contains("rate limit exceeded", StringComparison.Ordinal);

    private async ValueTask EnsureAuthorizedAsync(AgentExecutionContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        if (DateTimeOffset.UtcNow >= context.Deadline || await _isRevoked(context, context.CancellationToken).ConfigureAwait(false))
            throw new UnauthorizedAccessException("The execution is expired or authorization has been revoked.");
    }

    /// <summary>Atomically reserves one run-wide child allowance and active-child slot when a ledger was configured.</summary>
    /// <exception cref="InvalidOperationException">The run-wide child budget is exhausted.</exception>
    private void AcquireSubagentBudget()
    {
        if (_budgetLedger is null) return;
        // The ledger checks its own limits and reserves both resources under one lock.
        if (!_budgetLedger.TryReserveSubagentWithSlot())
            throw new InvalidOperationException("The run-wide subagent budget or concurrent subagent limit is exhausted.");
        // The caller must release the slot in its finally block after child completion.
    }

    /// <summary>Enforces the tenant/application rate gate for direct execution after authorization and before budget reservation.</summary>
    /// <remarks>Child delegations are not gated here: the conversation runner evaluates the gate exactly once per delegation with the forwarded tenant. Cancellation propagates as <see cref="OperationCanceledException"/>; other gate failures deny fail-closed.</remarks>
    /// <exception cref="InvalidOperationException">The gate denied the attempt or failed closed.</exception>
    private void ThrowIfRateLimited(AgentExecutionContext context, string application)
    {
        if (_rateLimitGate is null) return;
        try
        {
            var decision = _rateLimitGate.TryAcquire(
                new RateLimitKey(context.TenantScope, application, RateLimitModes.Interactive),
                context.AgentExecutionId.Value.ToString(), context.CancellationToken);
            if (!decision.Allowed)
                throw new InvalidOperationException($"Interactive rate limit exceeded ({decision.LimitName}).");
        }
        catch (OperationCanceledException) { throw; }
        catch (InvalidOperationException) { throw; }
        catch (Exception) { throw new InvalidOperationException("Interactive rate limit exceeded."); }
    }

    private static AgentExecutionContext CreateChild(AgentExecutionContext parent, OpenConnect.Abstractions.DelegationGrant grant,
        CancellationToken cancellationToken = default)
    {
        if (grant.ChildDepth != parent.Depth + 1 || grant.ParentExecutionId != ToTraceId(parent.AgentExecutionId))
            throw new UnauthorizedAccessException("Delegation grant lineage does not match the execution context.");
        if (grant.EffectiveScopes.Count == 0)
            throw new UnauthorizedAccessException("Delegation grant has no effective authority scope.");
        var budget = grant.RemainingBudget;
        var mapped = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["MaxToolCalls"] = budget.MaxToolCalls, ["MaxModelCalls"] = budget.MaxModelCalls,
            ["MaxTokens"] = budget.MaxTokens, ["MaxConcurrent"] = budget.MaxConcurrentAgents,
            ["MaxQueued"] = budget.MaxQueuedAgents, ["MaxTotal"] = budget.MaxTotalAgents,
            ["MaxRuntimeTicks"] = budget.MaxRuntime?.Ticks ?? long.MaxValue
        };
        return parent.CreateChild(parent.TenantScope, parent.PrincipalOrWorkloadScope, mapped,
            cancellationToken.CanBeCanceled ? cancellationToken : parent.CancellationToken);
    }

    private static ChildResult ToChildResult(OpenConnect.Abstractions.DelegationGrant grant, AgentExecutionContext child,
        AgentResponse response, int order) => new(grant.ChildAgentId, child.AgentExecutionId, response, order,
        grant.ParentExecutionId, order, child.TenantScope, grant.EffectiveScopes);

    private sealed record ChildOutcome(string ChildId, ChildResult? Result, Exception? Error, bool IsCancelled)
    {
        public static ChildOutcome Completed(string id, ChildResult result) => new(id, result, null, false);
        public static ChildOutcome Failed(string id, Exception error) => new(id, null, error, false);
        public static ChildOutcome Cancelled(string id) => new(id, null, null, true);
    }

    private static void ValidateMergeMode(AgentResultMergeMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
    }

    private static Guid ToTraceId(ExecutionId id)
    {
        // Existing trace records use Guid while the public execution identifier is a ULID.
        var digest = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(id.Value.ToString()));
        return new Guid(digest.AsSpan(0, 16));
    }
}
