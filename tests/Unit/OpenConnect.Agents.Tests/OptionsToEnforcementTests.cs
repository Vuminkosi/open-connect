using System.Collections.Immutable;
using Microsoft.Extensions.AI;
using OpenConnect.Abstractions;
using OpenConnect.Core.Configuration;
using OpenConnect.Agents.Execution;
using Xunit;

namespace OpenConnect.Agents.Tests;

public sealed class OptionsToEnforcementTests
{
    [Fact]
    public async Task Mapped_total_subagent_limit_is_enforced_by_sequential_runner()
    {
        var options = new AgentRegistryOptions
        {
            MaxTotalSubagentsPerRun = 1,
            MaxConcurrentSubagents = 2,
            MaxTotalToolCalls = 10,
            MaxModelCalls = 10
        };
        var ledger = new RunBudgetLedger(AgentOptionsMapping.ToRunBudget(options));
        var execution = new ExecutionId(Ulid.NewUlid());
        var parentTrace = ToTraceId(execution);
        var factory = new MafRealAgentFactory(new Registry(new AgentDefinition(new AgentId("child"), "child")), new RecordingChatClient());
        var conversation = new MafConversationRunner(factory, new AgentId("lead"), runLedger: ledger);
        var adapter = new OrchestrationAdapters(factory, conversation,
            (_, _, _) => ValueTask.FromResult<DelegationGrant?>(null),
            (_, _) => ValueTask.FromResult(false), options.MaxConcurrentSubagents, ledger);
        var context = new AgentExecutionContext
        {
            TenantScope = "tenant", PrincipalOrWorkloadScope = "principal", IdentityKind = AgentIdentityMode.ActiveUser,
            SessionId = new SessionId(Ulid.NewUlid()), TaskId = new TaskId(Ulid.NewUlid()), AgentExecutionId = execution,
            Depth = 0, CatalogRevision = 1, PolicyRevision = 1, AuthRevision = 1,
            Deadline = DateTimeOffset.UtcNow.AddMinutes(1),
            RemainingBudgets = ImmutableDictionary<string, long>.Empty.Add("MaxToolCalls", 10)
                .Add("MaxModelCalls", 10).Add("MaxTokens", long.MaxValue).Add("MaxConcurrent", 2)
                .Add("MaxQueued", int.MaxValue).Add("MaxTotal", 1).Add("MaxRuntimeTicks", long.MaxValue),
            CancellationToken = CancellationToken.None
        };
        var grant = new DelegationGrant(new AgentId("lead"), new AgentId("child"), 1,
            new HashSet<ToolId>(), new HashSet<string> { "scope" }, AgentOptionsMapping.ToRunBudget(options),
            parentTrace, "policy");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.SequentialRunner(execution, [grant, grant], "run", context));

        Assert.Single(conversation.GetTrace(), record => record.Kind == "agent" && record.CompletedAt is not null);
        Assert.Equal(0, ledger.Remaining.MaxTotalAgents);
    }

    private static Guid ToTraceId(ExecutionId id)
    {
        var digest = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(id.Value.ToString()));
        return new Guid(digest.AsSpan(0, 16));
    }

    private sealed class Registry(params AgentDefinition[] definitions) : IAgentDefinitionRegistry
    {
        public AgentDefinition? Get(AgentId id) => definitions.FirstOrDefault(item => item.Id == id);
        public IReadOnlyList<AgentDefinition> GetAll() => definitions;
        public void Register(AgentDefinition value) => throw new NotSupportedException();
    }

    private sealed class RecordingChatClient : IChatClient
    {
        public ChatClientMetadata Metadata { get; } = new("options-enforcement-test");
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "done")));
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
