using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.Extensions.AI;
using OpenConnect.Abstractions;
using OpenConnect.Agents.Execution;
using Xunit;

namespace OpenConnect.Agents.Tests;

public sealed class SupervisorDirectTests
{
    [Fact]
    public async Task SupervisorRunner_runs_gate_authorized_child_with_prompt()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var client = new RecordingChatClient();
        var (factory, runner, context, execution) = Create(client);
        var expectedParentId = ToTraceId(execution);
        string? requestedCapability = null;
        var adapters = new OrchestrationAdapters(factory, runner,
            (_, capability, _) =>
            {
                requestedCapability = capability;
                return ValueTask.FromResult<DelegationGrant?>(Grant(expectedParentId));
            }, (_, _) => ValueTask.FromResult(false), 2);

        const string prompt = "find the documentation";
        var result = await adapters.SupervisorRunner(execution, "docs.lookup", prompt, context, 7)
            .WaitAsync(timeout.Token);

        Assert.Equal("echo:" + prompt, result.Response.Text);
        Assert.Equal("docs.lookup", requestedCapability);
        Assert.Equal("researcher", result.AgentId.Value);
        Assert.Equal(7, result.Order);
        var childMessages = Assert.Single(client.Calls);
        Assert.Equal(new[] { prompt }, childMessages.Select(message => message.Text));
        var childTrace = Assert.Single(runner.GetTrace());
        Assert.Equal("researcher", childTrace.AgentId);
        Assert.NotNull(childTrace.CompletedAt);
    }

    [Fact]
    public async Task SupervisorRunner_denies_unknown_capability_before_invoking_agent()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var client = new RecordingChatClient();
        var (factory, runner, context, execution) = Create(client);
        var adapters = new OrchestrationAdapters(factory, runner,
            (_, _, _) => ValueTask.FromResult<DelegationGrant?>(null),
            (_, _) => ValueTask.FromResult(false), 2);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            adapters.SupervisorRunner(execution, "unknown.capability", "do not run", context).WaitAsync(timeout.Token));

        Assert.Equal(0, client.CallCount);
        Assert.Empty(runner.GetTrace());
    }

    [Fact]
    public async Task DirectRunner_returns_agent_text_on_success()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var client = new RecordingChatClient();
        var (factory, runner, context, _) = Create(client);

        var response = await new OrchestrationAdapters(factory, runner,
            (_, _, _) => ValueTask.FromResult<DelegationGrant?>(null),
            (_, _) => ValueTask.FromResult(false), 2)
            .DirectRunner(new AgentId("lead"), "direct prompt", context).WaitAsync(timeout.Token);

        Assert.Equal("echo:direct prompt", response.Text);
        var messages = Assert.Single(client.Calls);
        Assert.Equal(new[] { "direct prompt" }, messages.Select(message => message.Text));
        Assert.Empty(runner.GetTrace());
    }

    private static (MafRealAgentFactory Factory, MafConversationRunner Runner, AgentExecutionContext Context, ExecutionId Execution)
        Create(RecordingChatClient client)
    {
        var registry = new TestRegistry(
            new AgentDefinition(new AgentId("lead"), "lead"),
            new AgentDefinition(new AgentId("researcher"), "researcher"));
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
        return (factory, new MafConversationRunner(factory, new AgentId("lead")), context, execution);
    }

    private static DelegationGrant Grant(Guid parent) => new(
        new AgentId("lead"), new AgentId("researcher"), 1, new HashSet<ToolId>(), new HashSet<string> { "principal" },
        new AgentBudget(1, 1, 100, TimeSpan.FromMinutes(1), 2, 2, 2), parent, "policy-revision");

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

    private sealed class RecordingChatClient : IChatClient
    {
        private readonly ConcurrentQueue<IReadOnlyList<ChatMessage>> _calls = new();
        private int _callCount;
        public IReadOnlyList<IReadOnlyList<ChatMessage>> Calls => _calls.ToArray();
        public int CallCount => Volatile.Read(ref _callCount);
        public ChatClientMetadata Metadata { get; } = new("supervisor-direct-test");

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _callCount);
            var captured = messages.ToArray();
            _calls.Enqueue(captured);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                $"echo:{captured.LastOrDefault()?.Text ?? string.Empty}")));
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
