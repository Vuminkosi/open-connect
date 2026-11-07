using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenConnect.Abstractions;

namespace OpenConnect.Agents.Execution;

/// <summary>Checks current authorization immediately before an execution effect.</summary>
/// <param name="context">Execution context to verify.</param>
/// <param name="cancellationToken">Cancellation token for the check.</param>
/// <returns>True when authorization has been revoked.</returns>
public delegate ValueTask<bool> IsExecutionRevoked(AgentExecutionContext context, CancellationToken cancellationToken);

/// <summary>One requested child and its catalog-approved narrower authority.</summary>
/// <param name="AgentId">Authorized child agent identifier.</param>
/// <param name="TenantScope">Authorized tenant scope.</param>
/// <param name="PrincipalOrWorkloadScope">Authorized principal or workload scope.</param>
/// <param name="Budgets">Authorized child budgets.</param>
/// <param name="CatalogRevision">Catalog revision used to approve this grant.</param>
public sealed record DelegationGrant(AgentId AgentId, string TenantScope, string PrincipalOrWorkloadScope,
    IReadOnlyDictionary<string, long> Budgets, long CatalogRevision);

/// <summary>Host-side gate responsible for resolving a capability into an approved delegation.</summary>
public interface IDelegationGate
{
    /// <summary>Authorizes a capability for a child execution.</summary>
    /// <param name="context">The current execution context.</param>
    /// <param name="capability">Requested capability name.</param>
    /// <param name="cancellationToken">Cancellation token for authorization.</param>
    /// <returns>The authorized grant, or null when no grant is available.</returns>
    ValueTask<DelegationGrant?> AuthorizeAsync(AgentExecutionContext context, string capability, CancellationToken cancellationToken);
}

/// <summary>Runs orchestration using the installed Microsoft Agent Framework AIAgent.RunAsync surface.</summary>
public sealed class OrchestrationAdapters
{
    private readonly MafRealAgentFactory _factory;
    private readonly MafConversationRunner _conversation;
    private readonly IDelegationGate _gate;
    private readonly IsExecutionRevoked _isRevoked;
    private readonly int _maxConcurrentSubagents;

    /// <summary>Creates adapters that execute agents and enforce host authorization.</summary>
    /// <param name="factory">Factory for creating agent instances.</param>
    /// <param name="conversation">Runner that executes and records child conversations.</param>
    /// <param name="gate">Gate used to authorize delegated capabilities.</param>
    /// <param name="isRevoked">Callback that checks current authorization state.</param>
    /// <param name="maxConcurrentSubagents">Maximum simultaneous child executions.</param>
    public OrchestrationAdapters(MafRealAgentFactory factory, MafConversationRunner conversation,
        IDelegationGate gate, IsExecutionRevoked isRevoked, int maxConcurrentSubagents)
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
    public async Task<IReadOnlyList<ChildResult>> SequentialRunner(ExecutionId parent, IReadOnlyList<DelegationGrant> grants,
        string prompt, AgentExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(grants);
        var results = new List<ChildResult>(grants.Count);
        for (var i = 0; i < grants.Count; i++)
        {
            var grant = grants[i];
            var child = CreateChild(context, grant);
            var response = await RunChildAsync(parent, grant.AgentId, prompt, child).ConfigureAwait(false);
            results.Add(new ChildResult(grant.AgentId, child.AgentExecutionId, response, i));
        }
        return results;
    }

    /// <summary>Runs authorized children with bounded concurrency and returns results in request order.</summary>
    /// <param name="parent">Parent execution identifier.</param>
    /// <param name="grants">Authorized child grants.</param>
    /// <param name="prompt">Prompt supplied to each child.</param>
    /// <param name="context">Parent execution context.</param>
    /// <param name="mergeMode">Requested result merge mode to validate.</param>
    /// <returns>Child results in request order.</returns>
    public async Task<IReadOnlyList<ChildResult>> ParallelRunner(ExecutionId parent, IReadOnlyList<DelegationGrant> grants,
        string prompt, AgentExecutionContext context, AgentResultMergeMode mergeMode)
    {
        ArgumentNullException.ThrowIfNull(grants);
        ValidateMergeMode(mergeMode);
        using var limiter = new SemaphoreSlim(_maxConcurrentSubagents);
        var tasks = grants.Select(async (grant, index) =>
        {
            await limiter.WaitAsync(context.CancellationToken).ConfigureAwait(false);
            try
            {
                var child = CreateChild(context, grant);
                // Every child gets its own AIAgent instance; no mutable conversation history is shared.
                var response = await RunChildAsync(parent, grant.AgentId, prompt, child).ConfigureAwait(false);
                return new ChildResult(grant.AgentId, child.AgentExecutionId, response, index);
            }
            finally { limiter.Release(); }
        }).ToArray();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.OrderBy(result => result.Order).ToArray();
    }

    /// <summary>Handoff transfers only the caller-supplied projection; shared conversation history is not passed to the child.</summary>
    /// <param name="parent">Parent execution identifier.</param>
    /// <param name="grant">Authorized child grant.</param>
    /// <param name="explicitProjection">Explicit context projection for the child.</param>
    /// <param name="context">Parent execution context.</param>
    /// <returns>The child response.</returns>
    public async Task<AgentResponse> HandoffRunner(ExecutionId parent, DelegationGrant grant, string explicitProjection,
        AgentExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentNullException.ThrowIfNull(explicitProjection);
        var child = CreateChild(context, grant);
        return await RunChildAsync(parent, grant.AgentId, explicitProjection, child).ConfigureAwait(false);
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
        var grant = await _gate.AuthorizeAsync(context, capability, context.CancellationToken).ConfigureAwait(false)
            ?? throw new UnauthorizedAccessException("No authorized child agent is available for this capability.");
        var child = CreateChild(context, grant);
        var response = await RunChildAsync(parent, grant.AgentId, prompt, child).ConfigureAwait(false);
        return new ChildResult(grant.AgentId, child.AgentExecutionId, response, order);
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

    private static AgentExecutionContext CreateChild(AgentExecutionContext parent, DelegationGrant grant)
    {
        if (grant.CatalogRevision != parent.CatalogRevision)
            throw new UnauthorizedAccessException("Delegation grant catalog revision does not match the execution context.");
        return parent.CreateChild(grant.TenantScope, grant.PrincipalOrWorkloadScope, grant.Budgets, parent.CancellationToken);
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
