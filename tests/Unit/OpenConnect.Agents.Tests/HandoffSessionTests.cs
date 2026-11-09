using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.Extensions.AI;
using OpenConnect.Abstractions;
using OpenConnect.Agents.Execution;
using Xunit;

namespace OpenConnect.Agents.Tests;

public sealed class HandoffSessionTests
{
    [Fact]
    public async Task HandoffRunner_sends_only_explicit_projection()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var client = new RecordingChatClient();
        var (runner, adapters, context, execution) = Create(client, "child");
        await runner.TurnAsync("parent-secret", timeout.Token);

        var expectedParentId = ToTraceId(execution);
        var grant = Grant("child", expectedParentId);
        Assert.Equal(expectedParentId, grant.ParentExecutionId);
        Assert.Equal(context.Depth + 1, grant.ChildDepth);
        await adapters.HandoffRunner(execution, grant, "approved-projection-only", context).WaitAsync(timeout.Token);

        var childMessages = Assert.Single(client.Calls.Skip(1));
        Assert.Equal(new[] { "approved-projection-only" }, childMessages.Select(message => message.Text));
        Assert.DoesNotContain(childMessages, message => message.Text?.Contains("parent-secret", StringComparison.Ordinal) == true);
        var traces = runner.GetTrace();
        var child = Assert.Single(traces, trace => trace.AgentId == "child");
        Assert.NotNull(child.CompletedAt);
        // The lead TurnAsync trace uses a conversation-local Guid. Delegation lineage instead
        // uses ToTraceId(ExecutionId) in the grant and child context; those IDs are distinct spaces.
    }

    [Fact]
    public async Task HandoffRunner_rejects_grant_from_different_parent_before_invoking_agent()
    {
        var client = new RecordingChatClient();
        var (runner, adapters, context, execution) = Create(client, "child");
        var wrongParent = ToTraceId(new ExecutionId(Ulid.NewUlid()));
        var grant = Grant("child", wrongParent);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            adapters.HandoffRunner(execution, grant, "approved-projection-only", context));

        // Each child-agent run funnels through exactly one GetResponseAsync call, so this counter equals child-agent invocations.
        Assert.Equal(0, client.ChildAgentInvocations);
        Assert.Empty(client.Calls);
        Assert.Empty(runner.GetTrace());
    }

    [Fact]
    public async Task SequentialRunner_merges_authorized_results_in_order()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var client = new RecordingChatClient();
        var (runner, adapters, context, execution) = Create(client, "child-a", "child-b");
        const string prompt = "shared prompt";
        var expectedParentId = ToTraceId(execution);
        var grants = new[] { Grant("child-a", expectedParentId), Grant("child-b", expectedParentId) };

        var merged = await adapters.SequentialRunner(execution, grants, prompt, context).WaitAsync(timeout.Token);

        Assert.Equal(new[] { "child-a", "child-b" }, merged.Results.Select(result => result.AgentId.Value));
        Assert.Empty(merged.Quarantined);
        Assert.Equal(expectedParentId, merged.Audit.ParentExecutionId);
        Assert.Equal(2, client.Calls.Count);
        Assert.All(client.Calls, messages => Assert.Equal(new[] { prompt }, messages.Select(message => message.Text)));
        Assert.All(runner.GetTrace().Where(trace => trace.Kind == "agent"), trace =>
        {
            Assert.Equal(expectedParentId, trace.ParentId);
            Assert.NotNull(trace.CompletedAt);
        });
    }

    [Fact]
    public async Task ParallelRunner_children_get_isolated_instances()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var client = new RecordingChatClient();
        var (runner, adapters, context, execution) = Create(client, "child-a", "child-b");
        const string prompt = "isolated prompt";
        var grants = new[] { Grant("child-a", ToTraceId(execution)), Grant("child-b", ToTraceId(execution)) };

        await adapters.ParallelRunner(execution, grants, prompt, context, AgentResultMergeMode.OrderedTypedResults).WaitAsync(timeout.Token);

        Assert.Equal(2, client.Calls.Count);
        Assert.All(client.Calls, messages => Assert.Equal(new[] { prompt }, messages.Select(message => message.Text)));
        Assert.All(runner.GetTrace().Where(trace => trace.Kind == "agent"), trace => Assert.Equal(ToTraceId(execution), trace.ParentId));
    }

    private static (MafConversationRunner Runner, OrchestrationAdapters Adapters, AgentExecutionContext Context, ExecutionId Execution)
        Create(RecordingChatClient client, params string[] children)
    {
        var registry = new TestRegistry([new AgentDefinition(new AgentId("lead"), "lead"),
            .. children.Select(child => new AgentDefinition(new AgentId(child), child))]);
        var factory = new MafRealAgentFactory(registry, client);
        var execution = new ExecutionId(Ulid.NewUlid());
        var context = new AgentExecutionContext
        {
            TenantScope = "tenant", PrincipalOrWorkloadScope = "principal", IdentityKind = AgentIdentityMode.ActiveUser,
            SessionId = new SessionId(Ulid.NewUlid()), TaskId = new TaskId(Ulid.NewUlid()), AgentExecutionId = execution,
            Depth = 0, CatalogRevision = 1, PolicyRevision = 1, AuthRevision = 1,
            Deadline = DateTimeOffset.UtcNow.AddMinutes(1),
            RemainingBudgets = ImmutableDictionary<string, long>.Empty
                .Add("MaxToolCalls", 1).Add("MaxModelCalls", 1).Add("MaxTokens", 100)
                .Add("MaxConcurrent", 2).Add("MaxQueued", 2).Add("MaxTotal", 2).Add("MaxRuntimeTicks", long.MaxValue),
            CancellationToken = CancellationToken.None
        };
        var runner = new MafConversationRunner(factory, new AgentId("lead"));
        var adapters = new OrchestrationAdapters(factory, runner,
            (_, _, _) => ValueTask.FromResult<DelegationGrant?>(null), (_, _) => ValueTask.FromResult(false), 2);
        return (runner, adapters, context, execution);
    }

    private static DelegationGrant Grant(string child, Guid parent) => new(
        new AgentId("lead"), new AgentId(child), 1, new HashSet<ToolId>(), new HashSet<string> { "principal" },
        new AgentBudget(1, 1, 100, TimeSpan.FromMinutes(1), 2, 2, 2), parent, "policy");

    private static Guid ToTraceId(ExecutionId id)
    {
        var digest = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(id.Value.ToString()));
        return new Guid(digest.AsSpan(0, 16));
    }

    private sealed class TestRegistry(AgentDefinition[] definitions) : IAgentDefinitionRegistry
    {
        public AgentDefinition? Get(AgentId id) => definitions.FirstOrDefault(definition => definition.Id == id);
        public IReadOnlyList<AgentDefinition> GetAll() => definitions;
        public void Register(AgentDefinition value) => throw new NotSupportedException();
    }

    private sealed class RecordingChatClient : IChatClient
    {
        private readonly ConcurrentQueue<IReadOnlyList<ChatMessage>> _calls = new();
        private int _childAgentInvocations;
        public IReadOnlyList<IReadOnlyList<ChatMessage>> Calls => _calls.ToArray();
        public int ChildAgentInvocations => _childAgentInvocations;
        public ChatClientMetadata Metadata { get; } = new("handoff-session-test");

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _childAgentInvocations);
            var captured = messages.ToArray();
            _calls.Enqueue(captured);
            var prompt = captured.LastOrDefault()?.Text ?? string.Empty;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, $"echo:{prompt}")));
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
