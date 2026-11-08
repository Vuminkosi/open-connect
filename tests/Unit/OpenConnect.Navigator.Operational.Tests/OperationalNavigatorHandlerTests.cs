using OpenConnect.Navigator;
using OpenConnect.Navigator.Operational;
using Xunit;

namespace OpenConnect.Navigator.Operational.Tests;

public sealed class OperationalNavigatorHandlerTests
{
    [Fact]
    public async Task HandleAsync_usesScopedWorkloadIdentityAndReadOnlyPlan()
    {
        var identity = new TestIdentity("tenant-a");
        var executor = new TestExecutor();
        var handler = CreateHandler(identity, executor, new NavigatorTourPlan("tour", 1,
            [new(NavigatorTourStepType.Explain, Text: "welcome")], "home"));

        var result = await handler.HandleAsync(Job());

        Assert.Equal(OperationalRunStatus.Completed, result.Status);
        Assert.Equal(1, result.CompletedSteps);
        Assert.True(identity.Disposed);
        Assert.Equal(1, executor.Calls);
    }

    [Fact]
    public async Task HandleAsync_rejectsMutationBeforeExecution()
    {
        var executor = new TestExecutor();
        var handler = CreateHandler(new TestIdentity("tenant-a"), executor,
            new NavigatorTourPlan("tour", 1, [new(NavigatorTourStepType.Execute, "button", Action: NavigatorActionType.Click)], "home"));

        var result = await handler.HandleAsync(Job());

        Assert.Equal(OperationalRunStatus.Rejected, result.Status);
        Assert.Equal(0, executor.Calls);
    }

    [Fact]
    public async Task HandleAsync_marksUncertainAndDoesNotRunFollowingSteps()
    {
        var executor = new TestExecutor { Result = new(false, true, null) };
        var handler = CreateHandler(new TestIdentity("tenant-a"), executor,
            new NavigatorTourPlan("tour", 1, [new(NavigatorTourStepType.Explain), new(NavigatorTourStepType.Explain)], "home"));

        var result = await handler.HandleAsync(Job());

        Assert.Equal(OperationalRunStatus.Uncertain, result.Status);
        Assert.Equal(1, executor.Calls);
    }

    [Fact]
    public async Task HandleAsync_replayReturnsPriorReceiptWithoutReexecution()
    {
        var executor = new TestExecutor();
        var handler = CreateHandler(new TestIdentity("tenant-a"), executor,
            new NavigatorTourPlan("tour", 1, [new(NavigatorTourStepType.Explain)], "home"));

        var first = await handler.HandleAsync(Job());
        var replay = await handler.HandleAsync(Job());

        Assert.Equal(first, replay);
        Assert.Equal(1, executor.Calls);
    }

    [Fact]
    public async Task HandleAsync_concurrentSameKeyDifferentExecutionsRunsOnce()
    {
        var executor = new BlockingExecutor();
        var handler = CreateHandler(new TestIdentity("tenant-a"), executor,
            new NavigatorTourPlan("tour", 1, [new(NavigatorTourStepType.Explain)], "home"));
        var first = handler.HandleAsync(Job());
        await executor.Started.Task;
        var second = handler.HandleAsync(Job() with { ExecutionId = "execution-2" });
        executor.Release.TrySetResult(new(true, false, "ok"));

        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, executor.Calls);
        Assert.Equal(results[0], results[1]);
        Assert.Equal(OperationalRunStatus.Completed, results[1].Status);
    }

    [Fact]
    public async Task HandleAsync_staleLeaseAfterStepAbortsWithoutCountingStep()
    {
        var leases = new StaleAfterFirstCheckLeaseStore();
        var executor = new TestExecutor();
        var handler = new OperationalNavigatorHandler(leases, new IdentityProvider(new TestIdentity("tenant-a")),
            new PlanProvider(new NavigatorTourPlan("tour", 1, [new(NavigatorTourStepType.Explain)], "home")), executor);

        var result = await handler.HandleAsync(Job());

        Assert.Equal(OperationalRunStatus.LeaseLost, result.Status);
        Assert.Equal(0, result.CompletedSteps);
        Assert.Equal(1, executor.Calls);
    }

    [Fact]
    public async Task InMemoryLeaseStore_usesMonotonicFencingAndReportsNonDurable()
    {
        var store = new InMemoryOperationalLeaseStore();
        var first = await store.TryAcquireAsync("execution", TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.NotNull(first);
        Assert.Null(await store.TryAcquireAsync("execution", TimeSpan.FromSeconds(10), CancellationToken.None));
        await store.ReleaseAsync(first!, CancellationToken.None);
        var second = await store.TryAcquireAsync("execution", TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.True(second!.FencingToken > first!.FencingToken);
        Assert.False(store.IsDurable);
    }

    private static OperationalNavigatorHandler CreateHandler(TestIdentity identity, IOperationalStepExecutor executor, NavigatorTourPlan plan) =>
        new(new InMemoryOperationalLeaseStore(), new IdentityProvider(identity), new PlanProvider(plan), executor);

    private static OperationalJob Job() => new("tenant-a", "service-principal-ref", "tour",
        new HashSet<string>(["https://app.example"], StringComparer.Ordinal), "execution-1",
        DateTimeOffset.UtcNow.AddMinutes(1), "idempotency-1", "policy-1");

    private sealed class TestIdentity(string tenantScope) : IOperationalWorkloadIdentity
    {
        public string TenantScope { get; } = tenantScope;
        public bool Disposed { get; private set; }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class IdentityProvider(TestIdentity identity) : IOperationalWorkloadIdentityProvider
    {
        public ValueTask<IOperationalWorkloadIdentity?> ResolveAsync(string servicePrincipalRef, string tenantScope, CancellationToken cancellationToken) => ValueTask.FromResult<IOperationalWorkloadIdentity?>(identity);
    }

    private sealed class PlanProvider(NavigatorTourPlan plan) : IOperationalPlanProvider
    {
        public ValueTask<NavigatorTourPlan?> GetPlanAsync(string planId, string tenantScope, string policyRevision, CancellationToken cancellationToken) => ValueTask.FromResult<NavigatorTourPlan?>(plan);
    }

    private sealed class TestExecutor : IOperationalStepExecutor
    {
        public int Calls { get; private set; }
        public OperationalStepResult Result { get; set; } = new(true, false, "ok");
        public ValueTask<OperationalStepResult> ExecuteReadOnlyAsync(NavigatorTourStep step, IOperationalWorkloadIdentity identity, IReadOnlySet<string> applicationAllowlist, CancellationToken cancellationToken)
        { Calls++; return ValueTask.FromResult(Result); }
    }

    private sealed class BlockingExecutor : IOperationalStepExecutor
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<OperationalStepResult> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<OperationalStepResult> ExecuteReadOnlyAsync(NavigatorTourStep step, IOperationalWorkloadIdentity identity, IReadOnlySet<string> applicationAllowlist, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            Started.TrySetResult();
            return await Release.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class StaleAfterFirstCheckLeaseStore : IOperationalLeaseStore
    {
        private readonly InMemoryOperationalLeaseStore _inner = new();
        private int _checks;
        public bool IsDurable => false;
        public ValueTask<OperationalLease?> TryAcquireAsync(string executionId, TimeSpan duration, CancellationToken cancellationToken) => _inner.TryAcquireAsync(executionId, duration, cancellationToken);
        public ValueTask<OperationalLease?> RenewAsync(OperationalLease lease, TimeSpan duration, CancellationToken cancellationToken) => _inner.RenewAsync(lease, duration, cancellationToken);
        public ValueTask<bool> IsCurrentAsync(OperationalLease lease, CancellationToken cancellationToken) =>
            Interlocked.Increment(ref _checks) == 1 ? _inner.IsCurrentAsync(lease, cancellationToken) : ValueTask.FromResult(false);
        public ValueTask ReleaseAsync(OperationalLease lease, CancellationToken cancellationToken) => _inner.ReleaseAsync(lease, cancellationToken);
    }
}
