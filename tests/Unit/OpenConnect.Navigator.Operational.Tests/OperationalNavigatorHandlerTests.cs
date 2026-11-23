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
    public async Task HandleAsync_reports_confirmed_progress_when_an_inflight_step_throws()
    {
        var executor = new TestExecutor
        {
            OnExecute = (call, _) => call == 3
                ? throw new InvalidOperationException("Synthetic executor failure.")
                : ValueTask.FromResult(new OperationalStepResult(true, false, null))
        };
        var handler = CreateHandler(new TestIdentity("tenant-a"), executor, FourStepPlan());

        var result = await handler.HandleAsync(Job());

        Assert.Equal(3, executor.Calls);
        Assert.Equal(OperationalRunStatus.Uncertain, result.Status);
        Assert.Equal(2, result.CompletedSteps);
    }

    [Fact]
    public async Task HandleAsync_reports_uncertain_for_deadline_inside_a_step()
    {
        var executor = new TestExecutor
        {
            OnExecute = async (call, token) =>
            {
                if (call == 3)
                    await Task.Delay(TimeSpan.FromSeconds(6), token);
                return new(true, false, null);
            }
        };
        var handler = CreateHandler(new TestIdentity("tenant-a"), executor, FourStepPlan());
        var job = Job() with { Deadline = DateTimeOffset.UtcNow.AddSeconds(2) };

        var result = await handler.HandleAsync(job).WaitAsync(TimeSpan.FromSeconds(8));

        Assert.Equal(3, executor.Calls);
        Assert.Equal(OperationalRunStatus.Uncertain, result.Status);
        Assert.Equal(2, result.CompletedSteps);
    }

    [Fact]
    public async Task HandleAsync_preserves_caller_cancellation_and_reports_uncertain_to_another_waiter()
    {
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var permitExit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new TestExecutor
        {
            OnExecute = async (call, token) =>
            {
                if (call == 3)
                {
                    started.TrySetResult();
                    await permitExit.Task.WaitAsync(TimeSpan.FromSeconds(8));
                    token.ThrowIfCancellationRequested();
                }
                return new(true, false, null);
            }
        };
        var handler = CreateHandler(new TestIdentity("tenant-a"), executor, FourStepPlan());
        var job = Job();
        var caller = handler.HandleAsync(job, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var observer = handler.HandleAsync(job);

        try
        {
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => caller.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(observer.IsCompleted);
        }
        finally
        {
            permitExit.TrySetResult();
        }

        var result = await observer.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3, executor.Calls);
        Assert.Equal(OperationalRunStatus.Uncertain, result.Status);
        Assert.Equal(2, result.CompletedSteps);
    }

    [Theory]
    [InlineData(true, OperationalRunStatus.Cancelled)]
    [InlineData(false, OperationalRunStatus.Failed)]
    public async Task HandleAsync_preserves_confirmed_progress_on_interruption_before_dispatch(
        bool cancelled, OperationalRunStatus expectedStatus)
    {
        var executor = new TestExecutor();
        var leases = new InterruptBeforeThirdRenewalLeaseStore(cancelled);
        var handler = new OperationalNavigatorHandler(leases, new IdentityProvider(new TestIdentity("tenant-a")),
            new PlanProvider(FourStepPlan()), executor);

        var result = await handler.HandleAsync(Job());

        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(2, result.CompletedSteps);
        Assert.Equal(2, executor.Calls);
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

    private static NavigatorTourPlan FourStepPlan() =>
        new("tour", 1, Enumerable.Range(1, 4).Select(_ => new NavigatorTourStep(NavigatorTourStepType.Explain)).ToArray(), "home");

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
        public Func<int, CancellationToken, ValueTask<OperationalStepResult>>? OnExecute { get; init; }
        public ValueTask<OperationalStepResult> ExecuteReadOnlyAsync(NavigatorTourStep step, IOperationalWorkloadIdentity identity, IReadOnlySet<string> applicationAllowlist, CancellationToken cancellationToken)
        {
            Calls++;
            return OnExecute is null ? ValueTask.FromResult(Result) : OnExecute(Calls, cancellationToken);
        }
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

    private sealed class InterruptBeforeThirdRenewalLeaseStore(bool cancelled) : IOperationalLeaseStore
    {
        private readonly InMemoryOperationalLeaseStore _inner = new();
        private int _renewals;
        public bool IsDurable => false;
        public ValueTask<OperationalLease?> TryAcquireAsync(string executionId, TimeSpan duration, CancellationToken cancellationToken) => _inner.TryAcquireAsync(executionId, duration, cancellationToken);
        public ValueTask<OperationalLease?> RenewAsync(OperationalLease lease, TimeSpan duration, CancellationToken cancellationToken)
        {
            if (++_renewals == 3)
                throw cancelled ? new OperationCanceledException() : new InvalidOperationException("Synthetic lease failure.");
            return _inner.RenewAsync(lease, duration, cancellationToken);
        }
        public ValueTask<bool> IsCurrentAsync(OperationalLease lease, CancellationToken cancellationToken) => _inner.IsCurrentAsync(lease, cancellationToken);
        public ValueTask ReleaseAsync(OperationalLease lease, CancellationToken cancellationToken) => _inner.ReleaseAsync(lease, cancellationToken);
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
