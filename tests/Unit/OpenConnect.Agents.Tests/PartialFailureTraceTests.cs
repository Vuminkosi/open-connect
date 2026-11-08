using System.Collections.Immutable;
using Microsoft.Extensions.AI;
using OpenConnect.Abstractions;
using OpenConnect.Agents.Execution;
using Xunit;

namespace OpenConnect.Agents.Tests;

public sealed class PartialFailureTraceTests
{
    [Fact]
    public void Parallel_partial_failure_exposes_original_error_and_settled_child_provenance()
    {
        var originalError = new InvalidOperationException("child failed");
        var failure = new ParallelPartialFailureException("child-failed", originalError,
            ["child-completed"], ["child-cancelled"]);

        Assert.Same(originalError, failure.Error);
        Assert.Same(originalError, failure.InnerException);
        Assert.Equal("child-failed", failure.FailedChildId);
        Assert.Equal(new[] { "child-completed" }, failure.CompletedIds);
        Assert.Equal(new[] { "child-cancelled" }, failure.CancelledIds);
    }

    [Fact]
    public async Task Parallel_partial_failure_preserves_both_child_traces()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var successCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callCount = 0;
        var client = new TestChatClient(async (messages, cancellationToken) =>
        {
            if (Interlocked.Increment(ref callCount) == 1)
            {
                successCompleted.TrySetResult();
                return "ok";
            }

            await successCompleted.Task.WaitAsync(cancellationToken);
            throw new InvalidOperationException("expected child failure");
        });
        var (runner, adapters, context, execution) = Create(client, "child-a", "child-b");

        var failure = await Assert.ThrowsAsync<ParallelPartialFailureException>(() => adapters.ParallelRunner(execution,
            Grants(execution, "child-a", "child-b"), "run", context, AgentResultMergeMode.OrderedTypedResults)
            .WaitAsync(timeout.Token));
        Assert.IsType<InvalidOperationException>(failure.Error);
        Assert.Same(failure.Error, failure.InnerException);
        Assert.Equal("child-b", failure.FailedChildId);
        Assert.Equal(new[] { "child-a" }, failure.CompletedIds);
        Assert.Empty(failure.CancelledIds);

        var children = runner.GetTrace().Where(trace => trace.Kind == "agent").ToArray();
        Assert.Equal(new[] { "child-a", "child-b" }, children.Select(trace => trace.AgentId).Order().ToArray());
        Assert.All(children, trace =>
        {
            Assert.NotNull(trace.CompletedAt);
            Assert.Equal(ToTraceId(execution), trace.ParentId);
        });
        Assert.Equal(2, children.Select(trace => trace.Id).Distinct().Count());
        Assert.Empty(runner.History);
        Assert.DoesNotContain(runner.GetTrace(), trace => trace.Kind == "success");
    }

    [Fact]
    public async Task Sequential_partial_failure_keeps_completed_prefix()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var callCount = 0;
        var client = new TestChatClient((_, _) =>
        {
            return Interlocked.Increment(ref callCount) == 2
                ? Task.FromException<string>(new InvalidOperationException("expected child failure"))
                : Task.FromResult("ok");
        });
        var (runner, adapters, context, execution) = Create(client, "child-ok", "child-boom");

        await Assert.ThrowsAsync<InvalidOperationException>(() => adapters.SequentialRunner(execution,
            Grants(execution, "child-ok", "child-boom"), "run", context).WaitAsync(timeout.Token));

        var children = runner.GetTrace().Where(trace => trace.Kind == "agent").ToArray();
        Assert.Contains(children, trace => trace.AgentId == "child-ok" && trace.CompletedAt is not null);
        Assert.Contains(children, trace => trace.AgentId == "child-boom" && trace.CompletedAt is not null);
        Assert.All(children, trace =>
        {
            Assert.NotNull(trace.CompletedAt);
            Assert.Equal(ToTraceId(execution), trace.ParentId);
        });
    }

    private static (MafConversationRunner Runner, OrchestrationAdapters Adapters, AgentExecutionContext Context, ExecutionId Execution)
        Create(TestChatClient client, params string[] children)
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

    private static DelegationGrant[] Grants(ExecutionId execution, params string[] children) => children
        .Select(child => new DelegationGrant(new AgentId("lead"), new AgentId(child), 1, new HashSet<ToolId>(),
            new HashSet<string> { "principal" }, new AgentBudget(1, 1, 100, TimeSpan.FromMinutes(1), 2, 2, 2),
            ToTraceId(execution), "policy")).ToArray();

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

    private sealed class TestChatClient(Func<IEnumerable<ChatMessage>, CancellationToken, Task<string>> respond) : IChatClient
    {
        public ChatClientMetadata Metadata { get; } = new("partial-failure-trace-test");

        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            new(new ChatMessage(ChatRole.Assistant, await respond(messages, cancellationToken)));

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
