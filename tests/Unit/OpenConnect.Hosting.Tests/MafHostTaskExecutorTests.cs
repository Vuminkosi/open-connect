using System.Collections.Immutable;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Persistence;
using OpenConnect.Agents.Execution;
using OpenConnect.Hosting.AspNetCore.Execution;
using Xunit;

namespace OpenConnect.Hosting.Tests;

public sealed class MafHostTaskExecutorTests
{
    [Fact]
    public async Task Executes_real_turn_and_saves_native_state()
    {
        var fixture = new Fixture();
        await new MafHostTaskExecutor(fixture).ExecuteAsync(fixture.Input, default);
        Assert.Equal(1, fixture.Client.Calls);
        Assert.Equal(1, fixture.Store.Saves);
    }

    [Fact]
    public async Task Scope_mismatch_fails_before_model()
    {
        var fixture = new Fixture { WrongTenant = true };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new MafHostTaskExecutor(fixture).ExecuteAsync(fixture.Input, default));
        Assert.Equal(0, fixture.Client.Calls);
    }

    [Fact]
    public async Task Revocation_prevents_model_and_cancellation_propagates()
    {
        var revoked = new Fixture { Revoked = true };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new MafHostTaskExecutor(revoked).ExecuteAsync(revoked.Input, default));
        Assert.Equal(0, revoked.Client.Calls);

        using var cancellation = new CancellationTokenSource();
        var blocked = new Fixture { ClientCall = _ => cancellation.Cancel() };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new MafHostTaskExecutor(blocked).ExecuteAsync(blocked.Input, cancellation.Token));
    }

    [Fact]
    public void Registration_requires_factory_and_preserves_existing_executor()
    {
        var missing = new ServiceCollection();
        missing.AddMafHostTaskExecutor();
        using var provider = missing.BuildServiceProvider();
        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IHostTaskExecutor>());

        var existing = new ServiceCollection();
        existing.AddSingleton<IHostTaskExecutor, ExistingExecutor>();
        existing.AddMafHostTaskExecutor();
        using var existingProvider = existing.BuildServiceProvider();
        Assert.IsType<ExistingExecutor>(existingProvider.GetRequiredService<IHostTaskExecutor>());
    }

    private sealed class ExistingExecutor : IHostTaskExecutor
    { public Task ExecuteAsync(HostTaskExecution task, CancellationToken cancellationToken) => Task.CompletedTask; }

    private sealed class Fixture : IHostTaskAgentExecutionFactory
    {
        public readonly Client Client = new();
        public readonly Store Store = new();
        public bool WrongTenant { get; init; }
        public bool Revoked { get; init; }
        public Action<CancellationToken>? ClientCall { get => Client.OnCall; init => Client.OnCall = value; }
        public HostTaskExecution Input { get; } = new(new TaskId(Ulid.NewUlid()), new TenantId(Ulid.NewUlid()), new PrincipalId(Ulid.NewUlid()), new SessionId(Ulid.NewUlid()), "hello", "key");

        public Task<HostTaskAgentExecution> CreateAsync(HostTaskExecution task, CancellationToken cancellationToken)
        {
            var binding = new NativeAgentSessionBinding(task.SessionId, task.TenantId, task.PrincipalId,
                "lead", "r1", "maf-test", "s1", "test-provider");
            var context = new AgentExecutionContext
            {
                TenantScope = (WrongTenant ? new TenantId(Ulid.NewUlid()) : task.TenantId).Value.ToString(),
                PrincipalOrWorkloadScope = task.PrincipalId.Value.ToString(), IdentityKind = AgentIdentityMode.ActiveUser,
                SessionId = task.SessionId, TaskId = task.TaskId, AgentExecutionId = new ExecutionId(Ulid.NewUlid()),
                Depth = 0, CatalogRevision = 1, PolicyRevision = 1, AuthRevision = 1, Deadline = DateTimeOffset.UtcNow.AddMinutes(2),
                RemainingBudgets = ImmutableDictionary<string, long>.Empty, CancellationToken = cancellationToken
            };
            var factory = new MafRealAgentFactory(new Registry(), Client);
            var runner = new MafConversationRunner(factory, new AgentId("lead"));
            var persistence = new NativeAgentSessionPersistence(Store, new Protector());
            var contextForRevocation = new NativeSessionTurnPersistenceContext(persistence, binding, new Gate());
            return System.Threading.Tasks.Task.FromResult(new HostTaskAgentExecution(runner, context, (_, _) => ValueTask.FromResult(Revoked), contextForRevocation));
        }
    }

    private static NativeAgentSessionBinding Binding() => new(new SessionId(Ulid.NewUlid()), new TenantId(Ulid.NewUlid()),
        new PrincipalId(Ulid.NewUlid()), "lead", "r1", "maf-test", "s1", "test-provider");

    private sealed class Registry : IAgentDefinitionRegistry
    {
        private static readonly AgentDefinition Definition = new(new AgentId("lead"), "Lead");
        public AgentDefinition? Get(AgentId id) => Definition;
        public IReadOnlyList<AgentDefinition> GetAll() => [Definition];
        public void Register(AgentDefinition value) => throw new NotSupportedException();
    }

    private sealed class Store : INativeAgentSessionStore
    {
        private NativeAgentSessionRecord? _record;
        public int Saves { get; private set; }
        public Task<NativeAgentSessionRecord?> GetAsync(NativeAgentSessionBinding binding, CancellationToken ct = default) => Task.FromResult(_record);
        public Task<NativeAgentSessionSaveOutcome> SaveAsync(NativeAgentSessionRecord record, long expectedVersion, CancellationToken ct = default)
        { Saves++; _record = record; return Task.FromResult(NativeAgentSessionSaveOutcome.Saved); }
    }
    private sealed class Protector : INativeAgentSessionProtector
    {
        public Task<byte[]> ProtectAsync(NativeAgentSessionBinding binding, ReadOnlyMemory<byte> plaintext, CancellationToken ct = default) => Task.FromResult(plaintext.ToArray());
        public Task<byte[]> UnprotectAsync(NativeAgentSessionBinding binding, ReadOnlyMemory<byte> ciphertext, CancellationToken ct = default) => Task.FromResult(ciphertext.ToArray());
    }
    private sealed class Gate : INativeSessionTurnGate
    { public Task<INativeSessionTurnOwnership?> TryAcquireAsync(NativeAgentSessionBinding binding, AgentExecutionContext context, CancellationToken ct = default) => Task.FromResult<INativeSessionTurnOwnership?>(new Ownership()); }
    private sealed class Ownership : INativeSessionTurnOwnership
    {
        public CancellationToken OwnershipLost => default;
        public Task ValidateOwnershipAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Client : IChatClient
    {
        public int Calls { get; private set; }
        public Action<CancellationToken>? OnCall { get; set; }
        public ChatClientMetadata Metadata { get; } = new("host-executor-test");
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Calls++; OnCall?.Invoke(cancellationToken); cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "reply"))); }
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        { var result = await GetResponseAsync(messages, options, cancellationToken); yield return new ChatResponseUpdate(ChatRole.Assistant, result.Text); }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
