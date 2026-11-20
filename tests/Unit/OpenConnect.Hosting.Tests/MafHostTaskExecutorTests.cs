using System.Collections.Immutable;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Persistence;
using OpenConnect.Agents.Execution;
using OpenConnect.Hosting.AspNetCore.Execution;
using OpenConnect.Hosting.AspNetCore;
using OpenConnect.LiveUI.Agents;
using OpenConnect.LiveUI.Hosting;
using Xunit;

namespace OpenConnect.Hosting.Tests;

public sealed class MafHostTaskExecutorTests
{
    [Fact]
    public async Task Generic_liveui_factory_executes_and_publishes_for_accepted_connection()
    {
        var fixture = new Fixture { UseLiveUI = true };
        var sessions = new InMemorySessionStore();
        var session = sessions.Create(fixture.Input.TenantId, fixture.Input.PrincipalId);
        fixture.Input = fixture.Input with { SessionId = session.Id };
        var services = new ServiceCollection();
        services.AddSingleton<ILiveUIHostExecutionInputsFactory>(fixture);
        services.AddSingleton<IClientInteractionBroker>(fixture.Broker);
        services.AddSingleton(sessions);
        services.AddOpenConnectLiveUIHostExecution();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        await scope.ServiceProvider.GetRequiredService<IHostTaskExecutor>().ExecuteAsync(fixture.Input, default);

        Assert.Equal("accepted-connection", fixture.Broker.Request!.ConnectionId);
        Assert.Equal(session.Id, fixture.Broker.Session);
        Assert.Equal("reply", Assert.Single(sessions.GetEvents(session.Id.Value, 0), item => item.Type == "message").Data);
    }

    [Fact]
    public async Task Generic_liveui_factory_rejects_missing_connection_before_creating_host_context()
    {
        var fixture = new Fixture { UseLiveUI = true };
        var factory = new LiveUIHostTaskAgentExecutionFactory(fixture, fixture.Broker);
        var task = fixture.Input with { ClientConnectionId = null };

        await Assert.ThrowsAsync<InvalidOperationException>(() => factory.CreateAsync(task, default));

        Assert.Equal(0, fixture.Client.Calls);
        Assert.Null(fixture.Broker.Request);
    }

    [Fact]
    public async Task Generic_liveui_factory_does_not_expose_tools_for_noninteractive_tasks()
    {
        var fixture = new Fixture();
        fixture.Input = fixture.Input with { Origin = HostTaskOrigin.NonInteractive, ClientConnectionId = null };
        var factory = new LiveUIHostTaskAgentExecutionFactory(fixture, fixture.Broker);

        await new MafHostTaskExecutor(factory).ExecuteAsync(fixture.Input, default);

        Assert.True(fixture.Client.LastOptions?.Tools is null || fixture.Client.LastOptions.Tools.Count == 0);
        Assert.Null(fixture.Broker.Request);
    }

    [Fact]
    public async Task Generic_liveui_factory_rejects_mismatched_host_scope_before_tool_dispatch()
    {
        var fixture = new Fixture { UseLiveUI = true, WrongTenant = true };
        var factory = new LiveUIHostTaskAgentExecutionFactory(fixture, fixture.Broker);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new MafHostTaskExecutor(factory).ExecuteAsync(fixture.Input, default));

        Assert.Equal(0, fixture.Client.Calls);
        Assert.Null(fixture.Broker.Request);
    }

    [Fact]
    public async Task Publishes_completed_response_to_owning_task_session()
    {
        var fixture = new Fixture { UseLiveUI = true };
        var sessions = new InMemorySessionStore();
        var session = sessions.Create(fixture.Input.TenantId, fixture.Input.PrincipalId);
        fixture.Input = fixture.Input with { SessionId = session.Id };

        await new MafHostTaskExecutor(fixture, sessions).ExecuteAsync(fixture.Input, default);

        var message = Assert.Single(sessions.GetEvents(session.Id.Value, 0), item => item.Type == "message");
        Assert.Equal("reply", message.Data);
        Assert.Equal(fixture.Input.TaskId.Value.ToString(), message.TaskId);
        Assert.Equal(session.Id, fixture.Broker.Session);
    }

    [Fact]
    public async Task Session_owned_by_another_principal_rejects_before_model_or_publication()
    {
        var fixture = new Fixture { UseLiveUI = true };
        var sessions = new InMemorySessionStore();
        var session = sessions.Create(fixture.Input.TenantId, new PrincipalId(Ulid.NewUlid()));
        fixture.Input = fixture.Input with { SessionId = session.Id };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            new MafHostTaskExecutor(fixture, sessions).ExecuteAsync(fixture.Input, default));

        Assert.Equal(0, fixture.Client.Calls);
        Assert.Null(fixture.Broker.Request);
        Assert.Empty(sessions.GetEvents(session.Id.Value, 0));
    }

    [Fact]
    public async Task Registered_executor_publishes_to_registered_session_store()
    {
        var fixture = new Fixture();
        var sessions = new InMemorySessionStore();
        var session = sessions.Create(fixture.Input.TenantId, fixture.Input.PrincipalId);
        fixture.Input = fixture.Input with { SessionId = session.Id };
        var services = new ServiceCollection();
        services.AddSingleton<IHostTaskAgentExecutionFactory>(fixture);
        services.AddSingleton(sessions);
        services.AddMafHostTaskExecutor();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        await scope.ServiceProvider.GetRequiredService<IHostTaskExecutor>().ExecuteAsync(fixture.Input, default);

        Assert.Equal("reply", Assert.Single(sessions.GetEvents(session.Id.Value, 0), item => item.Type == "message").Data);
    }

    [Fact]
    public async Task Revocation_after_persistence_prevents_response_publication()
    {
        var fixture = new Fixture();
        fixture.Revocation = () => fixture.Store.Saves > 0;
        var sessions = new InMemorySessionStore();
        var session = sessions.Create(fixture.Input.TenantId, fixture.Input.PrincipalId);
        fixture.Input = fixture.Input with { SessionId = session.Id };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            new MafHostTaskExecutor(fixture, sessions).ExecuteAsync(fixture.Input, default));

        Assert.Equal(1, fixture.Store.Saves);
        Assert.Empty(sessions.GetEvents(session.Id.Value, 0));
    }

    [Fact]
    public async Task Executes_durable_liveui_tool_against_accepted_client_connection()
    {
        var fixture = new Fixture { UseLiveUI = true };
        await new MafHostTaskExecutor(fixture).ExecuteAsync(fixture.Input, default);

        Assert.Equal(2, fixture.Client.Calls);
        Assert.Equal(1, fixture.Store.Saves);
        Assert.Equal(fixture.Input.SessionId, fixture.Broker.Session);
        Assert.Equal("accepted-connection", fixture.Broker.Request!.ConnectionId);
        Assert.Equal("get_current_page", fixture.Broker.Request.Operation);
    }

    [Fact]
    public async Task Scope_mismatch_prevents_liveui_tool_dispatch()
    {
        var fixture = new Fixture { UseLiveUI = true, WrongTenant = true };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            new MafHostTaskExecutor(fixture).ExecuteAsync(fixture.Input, default));

        Assert.Equal(0, fixture.Client.Calls);
        Assert.Null(fixture.Broker.Request);
    }

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

    private sealed class Fixture : IHostTaskAgentExecutionFactory, ILiveUIHostExecutionInputsFactory
    {
        public readonly Client Client = new();
        public readonly Store Store = new();
        public readonly Broker Broker = new();
        public bool UseLiveUI { get; init; }
        public bool WrongTenant { get; init; }
        public bool Revoked { get; init; }
        public Func<bool>? Revocation { get; set; }
        public Action<CancellationToken>? ClientCall { get => Client.OnCall; init => Client.OnCall = value; }
        public HostTaskExecution Input { get; set; } = new(new TaskId(Ulid.NewUlid()), new TenantId(Ulid.NewUlid()), new PrincipalId(Ulid.NewUlid()), new SessionId(Ulid.NewUlid()), "hello", "key")
        { Origin = HostTaskOrigin.MiniComposer, ClientConnectionId = "accepted-connection" };

        public Task<HostTaskAgentExecution> CreateAsync(HostTaskExecution task, CancellationToken cancellationToken)
        {
            var inputs = CreateInputs(task, cancellationToken);
            var runner = UseLiveUI
                ? MafConversationRunner.CreateWithTools(inputs.AgentFactory, inputs.AgentId,
                    LiveUIClientAgentToolFactory.CreateToolsForConnection(Broker, task.SessionId, task.ClientConnectionId!),
                    LiveUIAgentGuidance.Instructions)
                : new MafConversationRunner(inputs.AgentFactory, inputs.AgentId);
            return System.Threading.Tasks.Task.FromResult(new HostTaskAgentExecution(runner, inputs.Context,
                inputs.IsExecutionRevoked, inputs.PersistenceContext));
        }

        Task<LiveUIHostExecutionInputs> ILiveUIHostExecutionInputsFactory.CreateAsync(HostTaskExecution task, CancellationToken cancellationToken) =>
            Task.FromResult(CreateInputs(task, cancellationToken));

        private LiveUIHostExecutionInputs CreateInputs(HostTaskExecution task, CancellationToken cancellationToken)
        {
            var binding = new NativeAgentSessionBinding(task.SessionId, task.TenantId, task.PrincipalId,
                "lead", "r1", "maf-test", "s1", "test-provider");
            var context = new AgentExecutionContext
            {
                TenantScope = (WrongTenant ? new TenantId(Ulid.NewUlid()) : task.TenantId).Value.ToString(),
                PrincipalOrWorkloadScope = task.PrincipalId.Value.ToString(),
                IdentityKind = AgentIdentityMode.ActiveUser,
                SessionId = task.SessionId,
                TaskId = task.TaskId,
                AgentExecutionId = new ExecutionId(Ulid.NewUlid()),
                Depth = 0,
                CatalogRevision = 1,
                PolicyRevision = 1,
                AuthRevision = 1,
                Deadline = DateTimeOffset.UtcNow.AddMinutes(2),
                RemainingBudgets = ImmutableDictionary<string, long>.Empty,
                CancellationToken = cancellationToken
            };
            var factory = new MafRealAgentFactory(new Registry(), Client);
            Client.UseLiveUI = UseLiveUI;
            var persistence = new NativeAgentSessionPersistence(Store, new Protector());
            var contextForRevocation = new NativeSessionTurnPersistenceContext(persistence, binding, new Gate());
            return new LiveUIHostExecutionInputs(factory, new AgentId("lead"), context,
                (_, _) => ValueTask.FromResult(Revocation?.Invoke() ?? Revoked), contextForRevocation);
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
        public bool UseLiveUI { get; set; }
        public ChatOptions? LastOptions { get; private set; }
        public Action<CancellationToken>? OnCall { get; set; }
        public ChatClientMetadata Metadata { get; } = new("host-executor-test");
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Calls++; LastOptions = options; OnCall?.Invoke(cancellationToken); cancellationToken.ThrowIfCancellationRequested();
            if (UseLiveUI && Calls == 1)
            {
                var function = Assert.IsAssignableFrom<AIFunction>(options!.Tools!.Single(tool => tool.Name == "liveui_get_current_page"));
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent("page-call", function.Name, new Dictionary<string, object?>())])));
            }
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "reply")));
        }
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        { var result = await GetResponseAsync(messages, options, cancellationToken); yield return new ChatResponseUpdate(ChatRole.Assistant, result.Text); }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class Broker : IClientInteractionBroker
    {
        public SessionId Session { get; private set; }
        public ClientInteractionRequest? Request { get; private set; }
        public Task<ClientInteractionResponse?> RequestAsync(SessionId sessionId, ClientInteractionRequest request,
            string expectedResultKind, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Session = sessionId;
            Request = request;
            return Task.FromResult<ClientInteractionResponse?>(new(request.CorrelationId, expectedResultKind, "{\"targets\":[]}"));
        }
    }
}
