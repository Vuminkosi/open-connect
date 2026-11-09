using System.Collections.Immutable;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Abstractions;
using OpenConnect.Agents.Execution;
using OpenConnect.Navigator;
using OpenConnect.Navigator.Operational;
using OpenConnect.Navigator.Operational.Durable;
using Xunit;

namespace OpenConnect.Agents.Tests;

/// <summary>Rate-limit + signed-audit slice (AG-IMPL-021): per-tenant + per-application limits with HMAC-signed denial audits.</summary>
public sealed class TenantApplicationRateLimitTests
{
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";
    private const string App = "https://app.example";
    private const string OtherApp = "https://other.example";

    [Fact]
    public async Task InteractiveDeny_BeforeMafInvocation_HasZeroSideEffects()
    {
        var client = new TestChatClient();
        var registry = new TestRegistry(new AgentDefinition(new AgentId("lead"), "lead"));
        var factory = new MafRealAgentFactory(registry, client);
        var runner = new MafConversationRunner(factory, new AgentId("lead"));
        var ledger = new RunBudgetLedger(new AgentBudget(20, 20, 1000, TimeSpan.FromMinutes(1), 3, 2, 10));
        var limiter = new TenantApplicationRateLimiter(new TestAuditKeyProvider(),
            null, new RateLimitOptions { BurstPerMinute = 1, SustainedPerDay = 1 });
        var context = MakeContext(TenantA);

        // Pre-consume the single allowed acquisition so the adapter attempt denies.
        // The adapter maps application to the executed agent id.
        Assert.True(limiter.TryAcquire(new RateLimitKey(TenantA, "lead", RateLimitModes.Interactive), "setup").Allowed);
        var before = ledger.Remaining;

        var adapters = new OrchestrationAdapters(factory, runner,
            (_, _, _) => ValueTask.FromResult<DelegationGrant?>(null),
            (_, _) => ValueTask.FromResult(false), 2, ledger, limiter);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => adapters.DirectRunner(new AgentId("lead"), "hello", context));
        Assert.Contains("rate limit exceeded", exception.Message, StringComparison.Ordinal);

        Assert.Equal(0, client.Calls);
        Assert.Empty(runner.GetTrace());
        Assert.Empty(factory.GetTrace());
        Assert.Equal(before, ledger.Remaining);
    }

    [Fact]
    public async Task TurnAsync_GatedAllow_InvokesMafOnce()
    {
        var client = new TestChatClient();
        var registry = new TestRegistry(new AgentDefinition(new AgentId("lead"), "lead"));
        var factory = new MafRealAgentFactory(registry, client);
        var ledger = new RunBudgetLedger(new AgentBudget(20, 20, 1000, TimeSpan.FromMinutes(1), 3, 2, 10));
        var limiter = new TenantApplicationRateLimiter(new TestAuditKeyProvider(),
            null, new RateLimitOptions { BurstPerMinute = 5, SustainedPerDay = 10 });
        var runner = new MafConversationRunner(factory, new AgentId("lead"),
            runLedger: ledger, rateLimitGate: limiter);

        var response = await runner.TurnAsync("hello", tenant: TenantA);

        Assert.NotNull(response);
        Assert.Equal(1, client.Calls);
        Assert.Single(runner.GetTrace());
    }

    [Fact]
    public async Task TurnAsync_Unattributed_DeniesWithoutSideEffects()
    {
        var client = new TestChatClient();
        var registry = new TestRegistry(new AgentDefinition(new AgentId("lead"), "lead"));
        var factory = new MafRealAgentFactory(registry, client);
        var ledger = new RunBudgetLedger(new AgentBudget(20, 20, 1000, TimeSpan.FromMinutes(1), 3, 2, 10));
        var limiter = new TenantApplicationRateLimiter(new TestAuditKeyProvider(),
            null, new RateLimitOptions { BurstPerMinute = 5, SustainedPerDay = 10 });
        var runner = new MafConversationRunner(factory, new AgentId("lead"),
            runLedger: ledger, rateLimitGate: limiter);
        var before = ledger.Remaining;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.TurnAsync("hello"));

        Assert.Contains("rate limit exceeded", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, client.Calls);
        Assert.Empty(runner.GetTrace());
        Assert.Empty(factory.GetTrace());
        Assert.Equal(before, ledger.Remaining);
    }

    [Fact]
    public async Task DelegateAsync_GatedAllow_InvokesMafOnce()
    {
        var client = new TestChatClient();
        var registry = new TestRegistry(new AgentDefinition(new AgentId("lead"), "lead"));
        var factory = new MafRealAgentFactory(registry, client);
        var ledger = new RunBudgetLedger(new AgentBudget(20, 20, 1000, TimeSpan.FromMinutes(1), 3, 2, 10));
        var limiter = new TenantApplicationRateLimiter(new TestAuditKeyProvider(),
            null, new RateLimitOptions { BurstPerMinute = 5, SustainedPerDay = 10 });
        var runner = new MafConversationRunner(factory, new AgentId("lead"),
            runLedger: ledger, rateLimitGate: limiter);

        var response = await runner.DelegateAsync(Guid.NewGuid(), new AgentId("lead"), "child work", tenant: TenantA);

        Assert.NotNull(response);
        Assert.Equal(1, client.Calls);
        Assert.Single(runner.GetTrace());
    }

    [Fact]
    public async Task DelegateAsync_Unattributed_DeniesWithoutSideEffects()
    {
        var client = new TestChatClient();
        var registry = new TestRegistry(new AgentDefinition(new AgentId("lead"), "lead"));
        var factory = new MafRealAgentFactory(registry, client);
        var ledger = new RunBudgetLedger(new AgentBudget(20, 20, 1000, TimeSpan.FromMinutes(1), 3, 2, 10));
        var limiter = new TenantApplicationRateLimiter(new TestAuditKeyProvider(),
            null, new RateLimitOptions { BurstPerMinute = 5, SustainedPerDay = 10 });
        var runner = new MafConversationRunner(factory, new AgentId("lead"),
            runLedger: ledger, rateLimitGate: limiter);
        var before = ledger.Remaining;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.DelegateAsync(Guid.NewGuid(), new AgentId("lead"), "child work"));

        Assert.Contains("rate limit exceeded", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, client.Calls);
        Assert.Empty(runner.GetTrace());
        Assert.Empty(factory.GetTrace());
        Assert.Equal(before, ledger.Remaining);
    }

    [Fact]
    public async Task AdapterChild_Deny_LeavesLedgerUnchangedAndMakesZeroMafCalls()
    {
        var client = new TestChatClient();
        var registry = new TestRegistry(new AgentDefinition(new AgentId("lead"), "lead"),
            new AgentDefinition(new AgentId("child"), "child"));
        var factory = new MafRealAgentFactory(registry, client);
        var ledger = new RunBudgetLedger(new AgentBudget(20, 20, 1000, TimeSpan.FromMinutes(1), 3, 2, 10));
        var limiter = new TenantApplicationRateLimiter(new TestAuditKeyProvider(),
            null, new RateLimitOptions { BurstPerMinute = 1, SustainedPerDay = 1 });
        var runner = new MafConversationRunner(factory, new AgentId("lead"),
            runLedger: ledger, rateLimitGate: limiter);
        var adapters = new OrchestrationAdapters(factory, runner,
            (_, _, _) => ValueTask.FromResult<DelegationGrant?>(null),
            (_, _) => ValueTask.FromResult(false), 2, ledger, limiter);
        var context = MakeContext(TenantA);

        // Pre-consume the single allowed acquisition for the attributed child bucket.
        Assert.True(limiter.TryAcquire(new RateLimitKey(TenantA, "child", RateLimitModes.Interactive), "setup").Allowed);
        var before = ledger.Remaining;
        var grant = MakeGrant("child", ToTraceGuid(context.AgentExecutionId));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => adapters.HandoffRunner(new ExecutionId(Ulid.NewUlid()), grant, "delegated work", context));

        Assert.Contains("rate limit exceeded", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, client.Calls);
        Assert.Empty(runner.GetTrace());
        Assert.Empty(factory.GetTrace());
        Assert.Equal(before, ledger.Remaining);
    }

    [Fact]
    public async Task AdapterChild_Allow_ConsumesExactlyOneGateAcquisition()
    {
        var client = new TestChatClient();
        var registry = new TestRegistry(new AgentDefinition(new AgentId("lead"), "lead"),
            new AgentDefinition(new AgentId("child"), "child"));
        var factory = new MafRealAgentFactory(registry, client);
        var ledger = new RunBudgetLedger(new AgentBudget(20, 20, 1000, TimeSpan.FromMinutes(1), 3, 2, 10));
        var limiter = new TenantApplicationRateLimiter(new TestAuditKeyProvider(),
            null, new RateLimitOptions { BurstPerMinute = 2, SustainedPerDay = 10 });
        var runner = new MafConversationRunner(factory, new AgentId("lead"),
            runLedger: ledger, rateLimitGate: limiter);
        var adapters = new OrchestrationAdapters(factory, runner,
            (_, _, _) => ValueTask.FromResult<DelegationGrant?>(null),
            (_, _) => ValueTask.FromResult(false), 2, ledger, limiter);
        var context = MakeContext(TenantA);
        var grant = MakeGrant("child", ToTraceGuid(context.AgentExecutionId));

        await adapters.HandoffRunner(new ExecutionId(Ulid.NewUlid()), grant, "delegated work", context);

        Assert.Equal(1, client.Calls);
        // Exactly one of the two burst acquisitions was consumed by the single gate evaluation.
        Assert.True(limiter.TryAcquire(new RateLimitKey(TenantA, "child", RateLimitModes.Interactive), "probe-1").Allowed);
        Assert.False(limiter.TryAcquire(new RateLimitKey(TenantA, "child", RateLimitModes.Interactive), "probe-2").Allowed);
    }

    [Fact]
    public async Task RateGate_Cancellation_PropagatesInsteadOfDenying()
    {
        var registry = new TestRegistry(new AgentDefinition(new AgentId("lead"), "lead"));
        var factory = new MafRealAgentFactory(registry, new TestChatClient());
        var runner = new MafConversationRunner(factory, new AgentId("lead"),
            rateLimitGate: new ThrowingGate(new OperationCanceledException()));
        var adapters = new OrchestrationAdapters(factory, runner,
            (_, _, _) => ValueTask.FromResult<DelegationGrant?>(null),
            (_, _) => ValueTask.FromResult(false), 2,
            rateLimitGate: new ThrowingGate(new OperationCanceledException()));
        var context = MakeContext(TenantA);

        await Assert.ThrowsAsync<OperationCanceledException>(() => runner.TurnAsync("hello", tenant: TenantA));
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => runner.DelegateAsync(Guid.NewGuid(), new AgentId("lead"), "work", tenant: TenantA));
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => adapters.DirectRunner(new AgentId("lead"), "hello", context));
    }

    [Fact]
    public void OptionGuards_RejectInvalidConfiguration()
    {
        Assert.Throws<ArgumentNullException>(() => new TenantApplicationRateLimiter(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TenantApplicationRateLimiter(new TestAuditKeyProvider(),
            null, new RateLimitOptions { BurstPerMinute = -1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TenantApplicationRateLimiter(new TestAuditKeyProvider(),
            null, new RateLimitOptions { SustainedPerDay = -1 }));
        Assert.Throws<ArgumentNullException>(() => RateLimitServiceCollectionExtensions.AddTenantApplicationRateLimiting(null!));

        var limiter = new TenantApplicationRateLimiter(new TestAuditKeyProvider());
        Assert.Throws<ArgumentNullException>(() => limiter.TryAcquire(null!, "exec-1"));
        Assert.Throws<ArgumentException>(() => limiter.TryAcquire(Key(TenantA, App), string.Empty));
        Assert.Throws<OperationCanceledException>(
            () => limiter.TryAcquire(Key(TenantA, App), "exec-1", new CancellationToken(canceled: true)));
        Assert.False(limiter.Verify(null!));
    }

    [Fact]
    public void TenantIsolation_DifferentTenants_HaveIndependentBudgets()
    {
        var limiter = new TenantApplicationRateLimiter(new TestAuditKeyProvider(),
            null, new RateLimitOptions { BurstPerMinute = 1, SustainedPerDay = 10 });

        Assert.True(limiter.TryAcquire(Key(TenantA, App), "exec-1").Allowed);
        var denied = limiter.TryAcquire(Key(TenantA, App), "exec-2");
        Assert.False(denied.Allowed);
        Assert.Equal("burst", denied.LimitName);
        Assert.NotNull(denied.Audit);

        Assert.True(limiter.TryAcquire(Key(TenantB, App), "exec-3").Allowed);
    }

    [Fact]
    public void ApplicationIsolation_DifferentApplications_HaveIndependentBudgets()
    {
        var limiter = new TenantApplicationRateLimiter(new TestAuditKeyProvider(),
            null, new RateLimitOptions { BurstPerMinute = 1, SustainedPerDay = 10 });

        Assert.True(limiter.TryAcquire(Key(TenantA, App), "exec-1").Allowed);
        Assert.False(limiter.TryAcquire(Key(TenantA, App), "exec-2").Allowed);
        Assert.True(limiter.TryAcquire(Key(TenantA, OtherApp), "exec-3").Allowed);
    }

    [Fact]
    public void WindowsAdvance_BurstAndSustainedReset_AfterWindowElapses()
    {
        var clock = new ManualTimeProvider();
        var limiter = new TenantApplicationRateLimiter(new TestAuditKeyProvider(), clock,
            new RateLimitOptions { BurstPerMinute = 2, SustainedPerDay = 3 });
        var key = Key(TenantA, App);

        Assert.True(limiter.TryAcquire(key, "exec-1").Allowed);
        Assert.True(limiter.TryAcquire(key, "exec-2").Allowed);
        var burstDenied = limiter.TryAcquire(key, "exec-3");
        Assert.False(burstDenied.Allowed);
        Assert.Equal("burst", burstDenied.LimitName);
        Assert.Equal(3, burstDenied.Audit!.ObservedCount);
        Assert.Equal(2, burstDenied.Audit.Limit);

        clock.Advance(TimeSpan.FromSeconds(61));
        var admitted = limiter.TryAcquire(key, "exec-4");
        Assert.True(admitted.Allowed);
        Assert.Null(admitted.Audit);
        var sustainedDenied = limiter.TryAcquire(key, "exec-5");
        Assert.False(sustainedDenied.Allowed);
        Assert.Equal("sustained", sustainedDenied.LimitName);
        Assert.Equal(4, sustainedDenied.Audit!.ObservedCount);
        Assert.Equal(3, sustainedDenied.Audit.Limit);

        clock.Advance(TimeSpan.FromDays(1).Add(TimeSpan.FromSeconds(1)));
        Assert.True(limiter.TryAcquire(key, "exec-6").Allowed);
    }

    [Fact]
    public void AuditSignature_Verifies_AndTamperedRecord_Fails()
    {
        var limiter = new TenantApplicationRateLimiter(new TestAuditKeyProvider(),
            null, new RateLimitOptions { BurstPerMinute = 1, SustainedPerDay = 1 });
        var key = Key(TenantA, App);

        Assert.True(limiter.TryAcquire(key, "exec-1").Allowed);
        var denied = limiter.TryAcquire(key, "exec-2");
        Assert.False(denied.Allowed);
        Assert.NotNull(denied.Audit);
        Assert.True(limiter.Verify(denied.Audit));

        Assert.False(limiter.Verify(denied.Audit with { ObservedCount = denied.Audit.ObservedCount + 1 }));
        Assert.False(limiter.Verify(denied.Audit with { Signature = "bogus" }));
        Assert.False(limiter.Verify(denied.Audit with { Tenant = TenantB }));
    }

    [Fact]
    public async Task OperationalDeny_ReturnsRejected_WithoutLeaseExecutionOrDurableWrite()
    {
        var leases = new CountingLeaseStore();
        var identities = new CountingIdentityProvider(new TestIdentity(TenantA));
        var plans = new PlanProvider(new NavigatorTourPlan("tour", 1,
            [new(NavigatorTourStepType.Explain, Text: "welcome")], "home"));
        var executor = new CountingExecutor();
        var durable = new CountingDurableStore();
        var receipts = new DurableIdempotencyStore(durable);
        var gate = new TenantApplicationRateLimiter(new TestAuditKeyProvider(),
            null, new RateLimitOptions { BurstPerMinute = 0, SustainedPerDay = 0 });
        var handler = new OperationalNavigatorHandler(leases, identities, plans, executor, receipts, gate);

        var job = new OperationalJob(TenantA, "service-principal-ref", "tour",
            new HashSet<string>([App], StringComparer.Ordinal), "execution-1",
            DateTimeOffset.UtcNow.AddMinutes(1), "idempotency-1", "policy-1");

        var receipt = await handler.HandleAsync(job);

        Assert.Equal(OperationalRunStatus.Rejected, receipt.Status);
        Assert.Equal("execution-1", receipt.ExecutionId);
        Assert.Equal(0, receipt.CompletedSteps);
        Assert.Equal(0, leases.Acquires);
        Assert.Equal(0, identities.Resolves);
        Assert.Equal(0, executor.Calls);
        Assert.Equal(0, durable.Saves);
        Assert.Null(await durable.GetReceiptAsync(TenantA, "idempotency-1", CancellationToken.None));
    }

    [Fact]
    public async Task KeyedDelegate_CancelledMidRun_SurfacesCancellationWithoutHistoryMutation()
    {
        var client = new BlockingChatClient();
        var registry = new TestRegistry(new AgentDefinition(new AgentId("lead"), "lead"),
            new AgentDefinition(new AgentId("child"), "child"));
        var factory = new MafRealAgentFactory(registry, client);
        var runner = new MafConversationRunner(factory, new AgentId("lead"));

        using var cts = new CancellationTokenSource();
        var task = runner.DelegateAsync(Guid.NewGuid(), new AgentId("child"), "child work",
            cts.Token, childSessionKey: "key-1");
        await client.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);

        // The in-flight model call started once; no further model call ran after cancel.
        Assert.Equal(1, client.Calls);
        // No child-session history was committed for the cancelled turn.
        Assert.Empty(runner.ExportCheckpoint("s").ChildSessions);
        // The trace record opened before invocation is completed by its owning finally.
        var trace = Assert.Single(runner.GetTrace());
        Assert.NotNull(trace.CompletedAt);
    }

    [Fact]
    public async Task OperationalHandle_Cancelled_GateSeesCallerTokenAndNeverReportsRateLimited()
    {
        var leases = new CountingLeaseStore();
        var identities = new CountingIdentityProvider(new TestIdentity(TenantA));
        var plans = new PlanProvider(new NavigatorTourPlan("tour", 1,
            [new(NavigatorTourStepType.Explain, Text: "welcome")], "home"));
        var executor = new CountingExecutor();
        var durable = new CountingDurableStore();
        var receipts = new DurableIdempotencyStore(durable);
        var gate = new CancellationObservingGate();
        var handler = new OperationalNavigatorHandler(leases, identities, plans, executor, receipts, gate);

        var job = new OperationalJob(TenantA, "service-principal-ref", "tour",
            new HashSet<string>([App], StringComparer.Ordinal), "execution-1",
            DateTimeOffset.UtcNow.AddMinutes(1), "idempotency-1", "policy-1");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Cancellation must propagate, never be misclassified as a rate-limit Rejected.
        await Assert.ThrowsAsync<OperationCanceledException>(() => handler.HandleAsync(job, cts.Token));

        Assert.True(gate.ObservedCancellableToken);
        Assert.Equal(0, leases.Acquires);
        Assert.Equal(0, identities.Resolves);
        Assert.Equal(0, executor.Calls);
        Assert.Equal(0, durable.Saves);
        Assert.Null(await durable.GetReceiptAsync(TenantA, "idempotency-1", CancellationToken.None));
    }

    private static DelegationGrant MakeGrant(string child, Guid parentTraceId) => new(
        new AgentId("lead"), new AgentId(child), 1, new HashSet<ToolId>(), new HashSet<string> { "principal" },
        new AgentBudget(1, 1, 100, TimeSpan.FromMinutes(1), 2, 2, 2), parentTraceId, "policy");

    private static Guid ToTraceGuid(ExecutionId id)
    {
        var digest = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(id.Value.ToString()));
        return new Guid(digest.AsSpan(0, 16));
    }

    private static RateLimitKey Key(string tenant, string application) =>
        new(tenant, application, RateLimitModes.Interactive);

    private static AgentExecutionContext MakeContext(string tenant) => new()
    {
        TenantScope = tenant,
        PrincipalOrWorkloadScope = "principal",
        IdentityKind = AgentIdentityMode.ActiveUser,
        SessionId = new SessionId(Ulid.NewUlid()),
        TaskId = new TaskId(Ulid.NewUlid()),
        AgentExecutionId = new ExecutionId(Ulid.NewUlid()),
        Depth = 0,
        CatalogRevision = 1,
        PolicyRevision = 1,
        AuthRevision = 1,
        Deadline = DateTimeOffset.UtcNow.AddMinutes(1),
        RemainingBudgets = ImmutableDictionary<string, long>.Empty
            .Add("MaxToolCalls", 1).Add("MaxModelCalls", 1).Add("MaxTokens", 100)
            .Add("MaxConcurrent", 1).Add("MaxQueued", 2).Add("MaxTotal", 10)
            .Add("MaxRuntimeTicks", long.MaxValue),
        CancellationToken = CancellationToken.None
    };

    /// <summary>Test-only deterministic audit key provider. Lives in the test assembly only; no production key sourcing.</summary>
    private sealed class TestAuditKeyProvider : IRateLimitAuditKeyProvider
    {
        private static readonly byte[] KeyBytes =
            [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32];

        public string KeyId => "test-key-1";

        public ReadOnlyMemory<byte> GetKey(string tenant) => KeyBytes;
    }

    /// <summary>Hand-rolled controllable clock; no additional packages required.</summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }

    private sealed class TestChatClient : IChatClient
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public ChatClientMetadata Metadata { get; } = new("test");

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                messages.Last(message => message.Role == ChatRole.User).Text)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed class TestRegistry(params AgentDefinition[] definitions) : IAgentDefinitionRegistry
    {
        public AgentDefinition? Get(AgentId id) => definitions.FirstOrDefault(definition => definition.Id == id);
        public IReadOnlyList<AgentDefinition> GetAll() => definitions;
        public void Register(AgentDefinition value) => throw new NotSupportedException();
    }

    /// <summary>Test-only gate that fails with a caller-supplied exception to prove fail-closed mapping.</summary>
    private sealed class ThrowingGate(Exception error) : IRateLimitGate
    {
        public RateLimitDecision TryAcquire(RateLimitKey key, string executionId, CancellationToken cancellationToken = default) =>
            throw error;

        public bool Verify(RateLimitAuditRecord record) => false;
    }

    private sealed class CountingLeaseStore : IOperationalLeaseStore
    {
        private readonly InMemoryOperationalLeaseStore _inner = new();
        private int _acquires;
        public int Acquires => Volatile.Read(ref _acquires);
        public bool IsDurable => false;

        public ValueTask<OperationalLease?> TryAcquireAsync(string executionId, TimeSpan duration, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _acquires);
            return _inner.TryAcquireAsync(executionId, duration, cancellationToken);
        }

        public ValueTask<OperationalLease?> RenewAsync(OperationalLease lease, TimeSpan duration, CancellationToken cancellationToken) =>
            _inner.RenewAsync(lease, duration, cancellationToken);

        public ValueTask<bool> IsCurrentAsync(OperationalLease lease, CancellationToken cancellationToken) =>
            _inner.IsCurrentAsync(lease, cancellationToken);

        public ValueTask ReleaseAsync(OperationalLease lease, CancellationToken cancellationToken) =>
            _inner.ReleaseAsync(lease, cancellationToken);
    }

    private sealed class TestIdentity(string tenantScope) : IOperationalWorkloadIdentity
    {
        public string TenantScope { get; } = tenantScope;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CountingIdentityProvider(TestIdentity identity) : IOperationalWorkloadIdentityProvider
    {
        private int _resolves;
        public int Resolves => Volatile.Read(ref _resolves);

        public ValueTask<IOperationalWorkloadIdentity?> ResolveAsync(string servicePrincipalRef, string tenantScope,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _resolves);
            return ValueTask.FromResult<IOperationalWorkloadIdentity?>(identity);
        }
    }

    private sealed class PlanProvider(NavigatorTourPlan plan) : IOperationalPlanProvider
    {
        public ValueTask<NavigatorTourPlan?> GetPlanAsync(string planId, string tenantScope, string policyRevision,
            CancellationToken cancellationToken) => ValueTask.FromResult<NavigatorTourPlan?>(plan);
    }

    private sealed class CountingExecutor : IOperationalStepExecutor
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        public ValueTask<OperationalStepResult> ExecuteReadOnlyAsync(NavigatorTourStep step, IOperationalWorkloadIdentity identity,
            IReadOnlySet<string> applicationAllowlist, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return ValueTask.FromResult(new OperationalStepResult(true, false, "ok"));
        }
    }

    private sealed class CountingDurableStore : IDurableOperationalStore
    {
        private int _saves;
        public int Saves => Volatile.Read(ref _saves);
        public bool IsDurable => true;

        public ValueTask<OperationalLease?> TryAcquireLeaseAsync(string executionId, string owner, TimeSpan duration,
            DateTimeOffset now, CancellationToken cancellationToken) => ValueTask.FromResult<OperationalLease?>(null);

        public ValueTask<OperationalLease?> RenewLeaseAsync(OperationalLease lease, TimeSpan duration, DateTimeOffset now,
            CancellationToken cancellationToken) => ValueTask.FromResult<OperationalLease?>(null);

        public ValueTask<bool> IsLeaseCurrentAsync(OperationalLease lease, DateTimeOffset now, CancellationToken cancellationToken) =>
            ValueTask.FromResult(false);

        public ValueTask ReleaseLeaseAsync(OperationalLease lease, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask<OperationalReceipt?> GetReceiptAsync(string tenantScope, string idempotencyKey,
            CancellationToken cancellationToken) => ValueTask.FromResult<OperationalReceipt?>(null);

        public ValueTask<bool> TrySaveReceiptAsync(string tenantScope, string idempotencyKey, OperationalReceipt receipt,
            DateTimeOffset completedAt, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _saves);
            return ValueTask.FromResult(true);
        }
    }

    /// <summary>Test-only chat client that blocks inside the model call until the caller cancels.</summary>
    private sealed class BlockingChatClient : IChatClient
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ChatClientMetadata Metadata { get; } = new("test");

        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            Entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "unreached"));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    /// <summary>Test-only gate that observes the token it receives and honors cancellation instead of denying.</summary>
    private sealed class CancellationObservingGate : IRateLimitGate
    {
        public bool ObservedCancellableToken { get; private set; }

        public RateLimitDecision TryAcquire(RateLimitKey key, string executionId, CancellationToken cancellationToken = default)
        {
            ObservedCancellableToken = cancellationToken.CanBeCanceled;
            cancellationToken.ThrowIfCancellationRequested();
            return new RateLimitDecision(true, null, null);
        }

        public bool Verify(RateLimitAuditRecord record) => false;
    }
}
