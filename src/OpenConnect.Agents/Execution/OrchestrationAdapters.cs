using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenConnect.Abstractions;

namespace OpenConnect.Agents.Execution;

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

    /// <summary>Creates adapters that execute agents and enforce host authorization.</summary>
    /// <param name="factory">Factory for creating agent instances.</param>
    /// <param name="conversation">Runner that executes and records child conversations.</param>
    /// <param name="gate">Gate used to authorize delegated capabilities.</param>
    /// <param name="isRevoked">Callback that checks current authorization state.</param>
    /// <param name="maxConcurrentSubagents">Maximum simultaneous child executions.</param>
    public OrchestrationAdapters(MafRealAgentFactory factory, MafConversationRunner conversation,
        Func<AgentExecutionContext, string, CancellationToken, ValueTask<OpenConnect.Abstractions.DelegationGrant?>> gate,
        IsExecutionRevoked isRevoked, int maxConcurrentSubagents)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _conversation = conversation ?? throw new ArgumentNullException(nameof(conversation));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _isRevoked = isRevoked ?? throw new ArgumentNullException(nameof(isRevoked));
        if (maxConcurrentSubagents < 1) throw new ArgumentOutOfRangeException(nameof(maxConcurrentSubagents));
        _maxConcurrentSubagents = maxConcurrentSubagents;
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
            await limiter.WaitAsync(scopeCancellation.Token).ConfigureAwait(false);
            try
            {
                try
                {
                    using var child = CreateChild(context, grant, scopeCancellation.Token);
                    // Every child gets its own AIAgent instance; no mutable conversation history is shared.
                    var response = await RunChildAsync(parent, grant.ChildAgentId, prompt, child).ConfigureAwait(false);
                    return ToChildResult(grant, child, response, index);
                }
                catch
                {
                    scopeCancellation.Cancel();
                    throw;
                }
            }
            finally { limiter.Release(); }
        }).ToArray();
        try
        {
            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            return ResultMerger.Merge(results, ToTraceId(parent), context.TenantScope, mergeMode);
        }
        catch
        {
            scopeCancellation.Cancel();
            try { await Task.WhenAll(tasks).ConfigureAwait(false); }
            catch { /* Observe every sibling failure; each child context is disposed by its owner. */ }
            throw;
        }
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
        if (!profileSupported) throw new InvalidOperationException($"The {mode} orchestration profile is not supported by the configured host.");
    }

    private async Task<AgentResponse> RunAgentAsync(AgentId agentId, string prompt, AgentExecutionContext context)
    {
        await EnsureAuthorizedAsync(context).ConfigureAwait(false);
        return await _factory.CreateAgent(agentId).RunAsync(prompt, cancellationToken: context.CancellationToken).ConfigureAwait(false);
    }

    private async Task<AgentResponse> RunChildAsync(ExecutionId parent, AgentId agentId, string prompt, AgentExecutionContext child)
    {
        await EnsureAuthorizedAsync(child).ConfigureAwait(false);
        // MafConversationRunner records the actual parent/child execution and invokes the MAF AIAgent.
        return await _conversation.DelegateAsync(ToTraceId(parent), agentId, prompt, child.CancellationToken).ConfigureAwait(false);
    }

    private async ValueTask EnsureAuthorizedAsync(AgentExecutionContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        if (DateTimeOffset.UtcNow >= context.Deadline || await _isRevoked(context, context.CancellationToken).ConfigureAwait(false))
            throw new UnauthorizedAccessException("The execution is expired or authorization has been revoked.");
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
