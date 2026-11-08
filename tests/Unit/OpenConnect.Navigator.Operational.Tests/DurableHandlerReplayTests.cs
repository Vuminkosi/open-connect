using OpenConnect.Navigator;
using OpenConnect.Navigator.Operational;
using OpenConnect.Navigator.Operational.Durable;
using Xunit;

namespace OpenConnect.Navigator.Operational.Tests;

public sealed class DurableHandlerReplayTests
{
    [Fact]
    public async Task CompletedReceiptIsReplayedByAnotherHandlerWithoutExecutingAgain()
    {
        var store = new InMemoryDurableOperationalStore();
        var executor = new Executor();
        var job = Job();
        var first = await Create(store, executor).HandleAsync(job);
        var replay = await Create(store, executor).HandleAsync(job);
        Assert.Equal(OperationalRunStatus.Completed, replay.Status);
        Assert.False(first.IsReplay);
        Assert.True(replay.IsReplay);
        Assert.Equal(first with { IsReplay = true }, replay);
        Assert.Equal(1, executor.Calls);
    }

    [Fact]
    public async Task UncertainResultIsNeverPersistedAndIdenticalJobExecutesAgain()
    {
        var store = new InMemoryDurableOperationalStore();
        var executor = new Executor { Result = new(false, true, null) };
        var job = Job();
        var result = await Create(store, executor).HandleAsync(job);
        Assert.Equal(OperationalRunStatus.Uncertain, result.Status);
        Assert.False(result.IsReplay);
        Assert.Null(await store.GetReceiptAsync(job.TenantScope, job.IdempotencyKey, default));
        var retryExecutor = new Executor();
        var retry = await Create(store, retryExecutor).HandleAsync(job);
        Assert.Equal(OperationalRunStatus.Completed, retry.Status);
        Assert.False(retry.IsReplay);
        Assert.Equal(1, retryExecutor.Calls);
    }

    [Fact]
    public async Task ConcurrentHandlersReturnSingleStoredWinner()
    {
        var store = new InMemoryDurableOperationalStore();
        var receiptStore = new DurableIdempotencyStore(store);
        var winner = new OperationalReceipt("winner", OperationalRunStatus.Completed, 1, "done", 5);
        Assert.True(await receiptStore.TryCompleteAsync("tenant", "key", winner, DateTimeOffset.UtcNow));
        Assert.False(await receiptStore.TryCompleteAsync("tenant", "key", winner with { ExecutionId = "loser" }, DateTimeOffset.UtcNow));
        Assert.Equal(winner, await receiptStore.GetAsync("tenant", "key"));
    }

    private static OperationalNavigatorHandler Create(InMemoryDurableOperationalStore store, Executor executor) =>
        new(new InMemoryOperationalLeaseStore(), new Identity(), new Plan(), executor, new DurableIdempotencyStore(store));

    private static OperationalJob Job() => new("tenant", "principal", "plan", new HashSet<string> { "app" },
        Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow.AddMinutes(1), Guid.NewGuid().ToString("N"), "policy");

    private sealed class Identity : IOperationalWorkloadIdentityProvider
    {
        public ValueTask<IOperationalWorkloadIdentity?> ResolveAsync(string servicePrincipalRef, string tenantScope, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IOperationalWorkloadIdentity?>(new WorkloadIdentity(tenantScope));
    }
    private sealed class WorkloadIdentity(string tenantScope) : IOperationalWorkloadIdentity
    {
        public string TenantScope { get; } = tenantScope;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Plan : IOperationalPlanProvider
    {
        public ValueTask<NavigatorTourPlan?> GetPlanAsync(string planId, string tenantScope, string policyRevision, CancellationToken cancellationToken) =>
            ValueTask.FromResult<NavigatorTourPlan?>(new("plan", 1, [new(NavigatorTourStepType.Explain)], "home"));
    }
    private sealed class Executor : IOperationalStepExecutor
    {
        public int Calls { get; private set; }
        public OperationalStepResult Result { get; init; } = new(true, false, "ok");
        public ValueTask<OperationalStepResult> ExecuteReadOnlyAsync(NavigatorTourStep step, IOperationalWorkloadIdentity identity,
            IReadOnlySet<string> applicationAllowlist, CancellationToken cancellationToken)
        { Calls++; return ValueTask.FromResult(Result); }
    }
}
