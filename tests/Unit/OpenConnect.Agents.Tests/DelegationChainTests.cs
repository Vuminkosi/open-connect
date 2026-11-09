using System.Collections.Immutable;
using Microsoft.Extensions.AI;
using OpenConnect.Abstractions;
using OpenConnect.Agents.Catalog;
using OpenConnect.Agents.Execution;
using Xunit;

namespace OpenConnect.Agents.Tests;

public sealed class DelegationChainTests
{
    [Fact]
    public async Task Routed_capability_is_gated_and_runs_child_but_unknown_capability_is_denied()
    {
        var leadId = new AgentId("lead");
        var researcherId = new AgentId("researcher");
        var tool = new ToolId("docs.search");
        var principalScope = "principal";
        var catalog = new AgentCatalog();
        catalog.Register(new AgentDefinition(researcherId, "researcher")
        {
            ToolPolicy = new([tool], [principalScope]),
            Budget = Budget()
        });
        catalog.Register(new AgentDefinition(leadId, "lead")
        {
            Orchestration = new(AllowedChildAgents: [researcherId]),
            ToolPolicy = new([tool], [principalScope]),
            Budget = Budget()
        });
        var router = new LeadCapabilityRouter(new Dictionary<string, IReadOnlyList<AgentId>>(StringComparer.Ordinal)
        {
            ["docs.lookup"] = [researcherId]
        });
        router.Validate(catalog, leadId);

        var calls = 0;
        var client = new RecordingChatClient((messages, _) =>
        {
            Interlocked.Increment(ref calls);
            Assert.Contains(messages, message => message.Text == "find the document");
            return Task.FromResult("found it");
        });
        var factory = new MafRealAgentFactory(catalog, client);
        var execution = new ExecutionId(Ulid.NewUlid());
        var context = Context(execution);
        var conversation = new MafConversationRunner(factory, leadId);
        var gate = new DelegationGate(catalog);
        var adapters = new OrchestrationAdapters(factory, conversation, (ctx, capability, _) =>
        {
            var child = router.ResolveChild(capability);
            var parent = new DelegationParentContext(leadId, ToTraceId(ctx.AgentExecutionId), ctx.Depth,
                [leadId], new HashSet<ToolId> { tool }, new HashSet<string> { principalScope }, Budget(),
                AgentExecutionMode.Interactive);
            var grant = gate.AuthorizeDelegation(parent, child,
                new DelegationRequest(new HashSet<ToolId> { tool }, new HashSet<string> { principalScope }));
            return ValueTask.FromResult<DelegationGrant?>(grant);
        }, (_, _) => ValueTask.FromResult(false), 2);

        var result = await adapters.SupervisorRunner(execution, "docs.lookup", "find the document", context, 0);

        Assert.Equal(researcherId, result.AgentId);
        Assert.Equal(0, result.Order);
        Assert.Equal("found it", result.Response.Text);
        Assert.Equal(1, calls);
        var trace = Assert.Single(conversation.GetTrace(), record => record.Kind == "agent");
        Assert.Equal(researcherId.Value, trace.AgentId);
        Assert.NotNull(trace.CompletedAt);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => adapters.SupervisorRunner(
            execution, "unknown.cap", "must not run", context));
        Assert.Equal(1, calls);
    }

    private static AgentBudget Budget() => new(10, 10, 100, TimeSpan.FromMinutes(1), 2, 2, 4);

    private static AgentExecutionContext Context(ExecutionId execution) => new()
    {
        TenantScope = "tenant", PrincipalOrWorkloadScope = "principal", IdentityKind = AgentIdentityMode.ActiveUser,
        SessionId = new SessionId(Ulid.NewUlid()), TaskId = new TaskId(Ulid.NewUlid()), AgentExecutionId = execution,
        Depth = 0, CatalogRevision = 1, PolicyRevision = 1, AuthRevision = 1,
        Deadline = DateTimeOffset.UtcNow.AddMinutes(1),
        RemainingBudgets = ImmutableDictionary<string, long>.Empty.Add("MaxToolCalls", 10)
            .Add("MaxModelCalls", 10).Add("MaxTokens", 100).Add("MaxConcurrent", 2)
            .Add("MaxQueued", 2).Add("MaxTotal", 4).Add("MaxRuntimeTicks", long.MaxValue),
        CancellationToken = CancellationToken.None
    };

    private static Guid ToTraceId(ExecutionId id)
    {
        var digest = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(id.Value.ToString()));
        return new Guid(digest.AsSpan(0, 16));
    }

    private sealed class RecordingChatClient(Func<IEnumerable<ChatMessage>, CancellationToken, Task<string>> respond) : IChatClient
    {
        public ChatClientMetadata Metadata { get; } = new("delegation-chain-test");
        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            new(new ChatMessage(ChatRole.Assistant, await respond(messages, cancellationToken)));
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant,
                (await GetResponseAsync(messages, options, cancellationToken)).Text);
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
