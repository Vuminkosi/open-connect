using System.Collections.Immutable;
using Microsoft.Extensions.AI;
using OpenConnect.Abstractions;
using OpenConnect.Agents.Execution;
using Xunit;

namespace OpenConnect.Agents.Tests;

public sealed class RevocationTests
{
    [Fact]
    public async Task RevokedCallbackDeniesDirectRunner()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var client = new CountingChatClient();
        var (adapters, _) = CreateAdapters(client, (_, _) => ValueTask.FromResult(true));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => adapters.DirectRunner(new AgentId("lead"), "run", Context(timeout.Token)));
        Assert.Equal(0, client.CallCount);
    }

    [Fact]
    public async Task RevokedCallbackDeniesChildRunnerAndSettlesWithoutInvokingMaf()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var client = new CountingChatClient();
        var (adapters, conversation) = CreateAdapters(client, (_, _) => ValueTask.FromResult(true));
        var context = Context(timeout.Token);
        var execution = new ExecutionId(Ulid.NewUlid());

        var run = adapters.ParallelRunner(execution, [Grant(execution, "child")], "run", context,
            AgentResultMergeMode.OrderedTypedResults);
        var completed = await Task.WhenAny(run, Task.Delay(Timeout.InfiniteTimeSpan, timeout.Token));

        Assert.Same(run, completed);
        var failure = await Assert.ThrowsAsync<ParallelPartialFailureException>(() => run);
        Assert.IsType<UnauthorizedAccessException>(failure.Error);
        Assert.Same(failure.Error, failure.InnerException);
        Assert.Empty(failure.CompletedIds);
        Assert.Empty(failure.CancelledIds);
        Assert.Equal(0, client.CallCount);
        Assert.Empty(conversation.GetTrace());
    }

    [Fact]
    public async Task ExpiredDeadlineDeniesEffect()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var client = new CountingChatClient();
        var (adapters, _) = CreateAdapters(client, (_, _) => ValueTask.FromResult(false));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => adapters.DirectRunner(new AgentId("lead"), "run",
            Context(timeout.Token, DateTimeOffset.UtcNow.AddSeconds(-1))));
        Assert.Equal(0, client.CallCount);
    }

    [Theory]
    [InlineData("other-tenant", "principal")]
    [InlineData("tenant", "other-principal")]
    public void ScopeNarrowingRefusesWidening(string tenant, string principal)
    {
        using var context = Context(CancellationToken.None);
        Assert.Throws<UnauthorizedAccessException>(() => context.CreateChild(tenant, principal,
            ImmutableDictionary<string, long>.Empty, CancellationToken.None));
    }

    private static (OrchestrationAdapters Adapters, MafConversationRunner Conversation) CreateAdapters(
        CountingChatClient client, IsExecutionRevoked revoked)
    {
        var registry = new TestRegistry(new AgentDefinition(new AgentId("lead"), "lead"),
            new AgentDefinition(new AgentId("child"), "child"));
        var factory = new MafRealAgentFactory(registry, client);
        var conversation = new MafConversationRunner(factory, new AgentId("lead"));
        return (new OrchestrationAdapters(factory, conversation,
            (_, _, _) => ValueTask.FromResult<DelegationGrant?>(null), revoked, 2), conversation);
    }

    private static AgentExecutionContext Context(CancellationToken cancellationToken, DateTimeOffset? deadline = null) => new()
    {
        TenantScope = "tenant", PrincipalOrWorkloadScope = "principal", IdentityKind = AgentIdentityMode.ActiveUser,
        SessionId = new SessionId(Ulid.NewUlid()), TaskId = new TaskId(Ulid.NewUlid()),
        AgentExecutionId = new ExecutionId(Ulid.NewUlid()), Depth = 0, CatalogRevision = 1,
        PolicyRevision = 1, AuthRevision = 1, Deadline = deadline ?? DateTimeOffset.UtcNow.AddMinutes(1),
        RemainingBudgets = ImmutableDictionary<string, long>.Empty
            .Add("MaxToolCalls", 1).Add("MaxModelCalls", 1).Add("MaxTokens", 100)
            .Add("MaxConcurrent", 2).Add("MaxQueued", 2).Add("MaxTotal", 2).Add("MaxRuntimeTicks", long.MaxValue),
        CancellationToken = cancellationToken
    };

    private static DelegationGrant Grant(ExecutionId execution, string child) => new(
        new AgentId("lead"), new AgentId(child), 1, new HashSet<ToolId>(), new HashSet<string> { "principal" },
        new AgentBudget(1, 1, 100, TimeSpan.FromMinutes(1), 2, 2, 2), ToTraceId(execution), "p1");

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

    private sealed class CountingChatClient : IChatClient
    {
        public int CallCount { get; private set; }
        public ChatClientMetadata Metadata { get; } = new("revocation-test");
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            throw new InvalidOperationException("MAF should not be invoked after authorization denial.");
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
