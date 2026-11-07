using System.Collections.Immutable;
using Microsoft.Extensions.AI;
using OpenConnect.Abstractions;
using OpenConnect.Agents.Execution;
using Xunit;

namespace OpenConnect.Agents.Tests;

public sealed class ParallelCancellationTests
{
    [Fact]
    public async Task ParallelRunner_cancels_and_settles_siblings_when_one_child_fails()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var siblingCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var siblingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callCount = 0;
        var registry = new TestRegistry(
            new AgentDefinition(new AgentId("fail"), "fail"),
            new AgentDefinition(new AgentId("wait"), "wait"));
        var client = new TestChatClient(async (_, cancellationToken) =>
        {
            if (Interlocked.Increment(ref callCount) == 1)
            {
                failed.TrySetResult();
                await siblingStarted.Task.WaitAsync(cancellationToken);
                throw new InvalidOperationException("expected child failure");
            }

            siblingStarted.TrySetResult();
            await failed.Task.WaitAsync(cancellationToken);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return "unreachable";
            }
            catch (OperationCanceledException)
            {
                siblingCancelled.TrySetResult();
                throw;
            }
        });
        var factory = new MafRealAgentFactory(registry, client);
        var lead = new AgentId("lead");
        var runner = new MafConversationRunner(factory, lead);
        var executionId = new ExecutionId(Ulid.NewUlid());
        var context = new AgentExecutionContext
        {
            TenantScope = "tenant", PrincipalOrWorkloadScope = "principal", IdentityKind = AgentIdentityMode.ActiveUser,
            SessionId = new SessionId(Ulid.NewUlid()), TaskId = new TaskId(Ulid.NewUlid()), AgentExecutionId = executionId,
            Depth = 0, CatalogRevision = 1, PolicyRevision = 1, AuthRevision = 1,
            Deadline = DateTimeOffset.UtcNow.AddMinutes(1),
            RemainingBudgets = ImmutableDictionary<string, long>.Empty
                .Add("MaxToolCalls", 1).Add("MaxModelCalls", 1).Add("MaxTokens", 100)
                .Add("MaxConcurrent", 2).Add("MaxQueued", 2).Add("MaxTotal", 2).Add("MaxRuntimeTicks", long.MaxValue),
            CancellationToken = timeout.Token
        };
        var adapters = new OrchestrationAdapters(factory, runner,
            (_, _, _) => ValueTask.FromResult<DelegationGrant?>(null), (_, _) => ValueTask.FromResult(false), 2);
        var parentTraceId = ToTraceId(executionId);
        var grants = new[]
        {
            Grant("fail", parentTraceId), Grant("wait", parentTraceId)
        };

        var execution = adapters.ParallelRunner(executionId, grants, "run", context, AgentResultMergeMode.OrderedTypedResults);
        var completed = await Task.WhenAny(execution, Task.Delay(Timeout.InfiniteTimeSpan, timeout.Token));
        Assert.Same(execution, completed);
        await Assert.ThrowsAnyAsync<Exception>(() => execution);
        Assert.True(siblingCancelled.Task.IsCompletedSuccessfully, "The sibling should observe scope cancellation.");
        Assert.All(runner.GetTrace(), trace => Assert.NotNull(trace.CompletedAt));
    }

    private static DelegationGrant Grant(string child, Guid parent) => new(
        new AgentId("lead"), new AgentId(child), 1, new HashSet<ToolId>(), new HashSet<string> { "scope" },
        new AgentBudget(1, 1, 100, TimeSpan.FromMinutes(1), 2, 2, 2), parent, "policy");

    private static Guid ToTraceId(ExecutionId id)
    {
        var digest = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(id.Value.ToString()));
        return new Guid(digest.AsSpan(0, 16));
    }

    private sealed class TestRegistry(params AgentDefinition[] definitions) : IAgentDefinitionRegistry
    {
        public AgentDefinition? Get(AgentId id) => definitions.FirstOrDefault(definition => definition.Id == id);
        public IReadOnlyList<AgentDefinition> GetAll() => definitions;
        public void Register(AgentDefinition value) => throw new NotSupportedException();
    }

    private sealed class TestChatClient(Func<IEnumerable<ChatMessage>, CancellationToken, Task<string>> respond) : IChatClient
    {
        public ChatClientMetadata Metadata { get; } = new("parallel-cancellation-test");

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
