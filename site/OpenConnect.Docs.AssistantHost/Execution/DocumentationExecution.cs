using System.Collections.Immutable;
using Microsoft.Extensions.AI;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Persistence;
using OpenConnect.Agents.Execution;
using OpenConnect.Agents.Templates;
using OpenConnect.Hosting.AspNetCore.Execution;
using OpenConnect.LiveUI.Hosting;
using OpenConnect.Privacy;

namespace OpenConnect.Docs.AssistantHost.Execution;

internal sealed class DocumentationTaskAuthorizer : IHostTaskExecutionAuthorizer
{
    public Task AuthorizeAsync(HostTaskExecution task, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (task.TenantId == default || task.PrincipalId == default)
            throw new UnauthorizedAccessException();
        return Task.CompletedTask;
    }
}

internal sealed class DocumentationExecutionInputsFactory(
    IAgentDefinitionRegistry registry,
    IChatClient chat,
    IEgressGate gate,
    INativeAgentSessionStore store,
    INativeAgentSessionProtector protector,
    INativeSessionTurnGate turnGate) : ILiveUIHostExecutionInputsFactory
{
    private const string Instructions = "You are the assistant for OpenConnect, an open-source .NET-first, privacy-first " +
        "AI application framework. It provides SDKs, a privacy firewall, LiveUI, workers and Microsoft Agent Framework " +
        "integration. This OpenConnect is not the VPN client. Explain the framework accurately and say when you do not know.";

    public Task<LiveUIHostExecutionInputs> CreateAsync(HostTaskExecution task, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var binding = new NativeAgentSessionBinding(task.SessionId, task.TenantId, task.PrincipalId,
            "documentation-assistant", "documentation-v1", "maf", "v1", "configured-provider");
        var context = new AgentExecutionContext
        {
            TenantScope = task.TenantId.Value.ToString(), PrincipalOrWorkloadScope = task.PrincipalId.Value.ToString(),
            IdentityKind = AgentIdentityMode.ActiveUser, SessionId = task.SessionId, TaskId = task.TaskId,
            AgentExecutionId = new ExecutionId(Ulid.NewUlid()), Depth = 0, CatalogRevision = 1, PolicyRevision = 1,
            AuthRevision = 1, Deadline = DateTimeOffset.UtcNow.AddMinutes(3), RemainingBudgets = ImmutableDictionary<string, long>.Empty,
            CancellationToken = cancellationToken
        };
        var egress = new EgressContext(task.TenantId, task.PrincipalId, task.SessionId, EgressDestination.ExternalModel);
        var factory = new MafRealAgentFactory(registry, chat, gate, new FixedEgressContextProvider(egress));
        var persistence = new NativeSessionTurnPersistenceContext(new NativeAgentSessionPersistence(store, protector), binding, turnGate);
        return Task.FromResult(new LiveUIHostExecutionInputs(factory, new AgentId("documentation-assistant"), context,
            (_, ct) => { ct.ThrowIfCancellationRequested(); return ValueTask.FromResult(false); }, persistence, Instructions));
    }
}
