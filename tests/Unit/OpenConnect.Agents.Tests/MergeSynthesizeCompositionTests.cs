using System.Collections.Immutable;
using Microsoft.Extensions.AI;
using OpenConnect.Abstractions;
using OpenConnect.Agents.Execution;
using Xunit;

namespace OpenConnect.Agents.Tests;

public sealed class MergeSynthesizeCompositionTests
{
    [Fact]
    public async Task LeadSynthesizes_merge_flows_into_single_synthesis_call()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var client = new EchoClient();
        var (factory, runner, adapters, synthesizer, context, ledger, parent) = Create(client);
        var grants = Grants(parent);

        var merged = await adapters.ParallelRunner(parent, grants, "child output", context,
            AgentResultMergeMode.LeadSynthesizes);
        Assert.True(merged.RequiresLeadSynthesis);
        Assert.Equal(2, merged.Results.Count);
        Assert.Equal(new[] { "first", "second" }, merged.Results.Select(result => result.AgentId.Value));

        var text = await synthesizer.SynthesizeAsync(parent, new AgentId("lead"), merged, context, ledger, timeout.Token);

        Assert.Contains("child output", text);
        Assert.Equal(3, client.CallCount);
    }

    [Fact]
    public async Task Ordered_merge_rejects_synthesis_misuse()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var client = new EchoClient();
        var (_, _, adapters, synthesizer, context, ledger, parent) = Create(client);

        var merged = await adapters.ParallelRunner(parent, Grants(parent), "child output", context,
            AgentResultMergeMode.OrderedTypedResults);
        var callsAfterMerge = client.CallCount;
        Assert.False(merged.RequiresLeadSynthesis);

        await Assert.ThrowsAsync<InvalidOperationException>(() => synthesizer.SynthesizeAsync(
            parent, new AgentId("lead"), merged, context, ledger, timeout.Token));

        Assert.Equal(callsAfterMerge, client.CallCount);
        Assert.Equal(2, callsAfterMerge);
    }

    [Fact]
    public async Task Exhausted_budget_between_merge_and_synthesis_preserves_merged_results()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var client = new EchoClient();
        var (factory, runner, adapters, synthesizer, context, ledger, parent) = Create(client, modelCalls: 2);
        var grants = Grants(parent);

        var merged = await adapters.ParallelRunner(parent, grants, "child output", context,
            AgentResultMergeMode.LeadSynthesizes);
        var acceptedBefore = merged.Results.ToArray();
        Assert.Equal(2, acceptedBefore.Length);
        Assert.Empty(merged.Quarantined);
        Assert.Equal(0, ledger.Remaining.MaxModelCalls);

        await Assert.ThrowsAsync<InvalidOperationException>(() => synthesizer.SynthesizeAsync(
            parent, new AgentId("lead"), merged, context, ledger, timeout.Token));

        Assert.Equal(2, merged.Results.Count);
        Assert.Empty(merged.Quarantined);
        Assert.Equal(acceptedBefore, merged.Results);
        Assert.Equal(2, client.CallCount);
    }

    private static (MafRealAgentFactory Factory, MafConversationRunner Runner, OrchestrationAdapters Adapters,
        LeadSynthesizer Synthesizer, AgentExecutionContext Context, RunBudgetLedger Ledger, ExecutionId Parent)
        Create(EchoClient client, int modelCalls = 3)
    {
        var registry = new Registry();
        var factory = new MafRealAgentFactory(registry, client);
        var ledger = new RunBudgetLedger(new AgentBudget(10, modelCalls, 1_000, TimeSpan.FromMinutes(1), 2, 2, 2));
        var context = new AgentExecutionContext
        {
            TenantScope = "tenant", PrincipalOrWorkloadScope = "principal", IdentityKind = AgentIdentityMode.ActiveUser,
            SessionId = new SessionId(Ulid.NewUlid()), TaskId = new TaskId(Ulid.NewUlid()),
            AgentExecutionId = new ExecutionId(Ulid.NewUlid()), Depth = 0, CatalogRevision = 1,
            PolicyRevision = 1, AuthRevision = 1, Deadline = DateTimeOffset.UtcNow.AddMinutes(1),
            RemainingBudgets = ImmutableDictionary<string, long>.Empty
                .Add("MaxToolCalls", 1).Add("MaxModelCalls", 1).Add("MaxTokens", 100)
                .Add("MaxConcurrent", 2).Add("MaxQueued", 2).Add("MaxTotal", 2)
                .Add("MaxRuntimeTicks", long.MaxValue),
            CancellationToken = CancellationToken.None
        };
        var parent = context.AgentExecutionId;
        var runner = new MafConversationRunner(factory, new AgentId("lead"), runLedger: ledger);
        var adapters = new OrchestrationAdapters(factory, runner,
            (_, _, _) => ValueTask.FromResult<DelegationGrant?>(null), (_, _) => ValueTask.FromResult(false), 2, ledger);
        var synthesizer = new LeadSynthesizer(factory, (_, _) => ValueTask.FromResult(false));
        return (factory, runner, adapters, synthesizer, context, ledger, parent);
    }

    private static DelegationGrant[] Grants(ExecutionId parent)
    {
        var parentTrace = ToTraceId(parent);
        return [Grant("first", parentTrace), Grant("second", parentTrace)];
    }

    private static DelegationGrant Grant(string child, Guid parent) => new(
        new AgentId("lead"), new AgentId(child), 1, new HashSet<ToolId>(), new HashSet<string> { "scope" },
        new AgentBudget(1, 1, 100, TimeSpan.FromMinutes(1), 2, 2, 2), parent, "policy");

    private static Guid ToTraceId(ExecutionId id)
    {
        var digest = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(id.Value.ToString()));
        return new Guid(digest.AsSpan(0, 16));
    }

    private sealed class Registry : IAgentDefinitionRegistry
    {
        private static readonly AgentDefinition[] Definitions =
        [new(new AgentId("lead"), "lead"), new(new AgentId("first"), "first"), new(new AgentId("second"), "second")];
        public AgentDefinition? Get(AgentId id) => Definitions.FirstOrDefault(definition => definition.Id == id);
        public IReadOnlyList<AgentDefinition> GetAll() => Definitions;
        public void Register(AgentDefinition value) => throw new NotSupportedException();
    }

    private sealed class EchoClient : IChatClient
    {
        private int _callCount;
        public ChatClientMetadata Metadata { get; } = new("merge-synthesis-composition-test");
        public int CallCount => Volatile.Read(ref _callCount);

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _callCount);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, messages.Last().Text)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
