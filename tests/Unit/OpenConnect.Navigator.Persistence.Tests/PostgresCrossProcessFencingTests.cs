using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OpenConnect.Navigator;
using OpenConnect.Navigator.Operational;
using OpenConnect.Navigator.Operational.Durable;
using OpenConnect.Navigator.Persistence.Postgres;
using OpenConnect.Persistence.Postgres;
using Testcontainers.PostgreSql;
using Xunit;
using Xunit.Abstractions;

namespace OpenConnect.Navigator.Persistence.Tests;

[Trait("Category", "PostgresDocker")]
public sealed class PostgresCrossProcessFencingTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Lease_acquisition_fences_stale_host_after_expiry_on_independent_connections()
    {
        await using var postgres = await StartPostgresAsync("navigator_cross_fence_test");
        if (postgres is null) return;
        var connectionString = postgres.GetConnectionString();
        Assert.Contains("navigator_cross_fence_test", connectionString, StringComparison.OrdinalIgnoreCase);
        await using (var migrationDb = CreateDb(connectionString))
            await migrationDb.GetService<IMigrator>().MigrateAsync();

        var tenant = Guid.NewGuid().ToString("D");
        var executionId = $"{tenant}:cross-host-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        OperationalLease hostA;
        await using (var dbA = CreateDb(connectionString))
        {
            var storeA = new PostgresDurableNavigatorStore(dbA);
            hostA = Assert.IsType<OperationalLease>(await storeA.TryAcquireLeaseAsync(executionId, "host-a",
                TimeSpan.FromMilliseconds(100), now, CancellationToken.None));
        }

        await using (var dbB = CreateDb(connectionString))
        {
            var storeB = new PostgresDurableNavigatorStore(dbB);
            Assert.Null(await storeB.TryAcquireLeaseAsync(executionId, "host-b", TimeSpan.FromSeconds(5), now,
                CancellationToken.None));
        }

        await Task.Delay(500);
        OperationalLease hostB;
        await using (var dbB = CreateDb(connectionString))
        {
            var storeB = new PostgresDurableNavigatorStore(dbB);
            hostB = Assert.IsType<OperationalLease>(await storeB.TryAcquireLeaseAsync(executionId, "host-b",
                TimeSpan.FromSeconds(5), DateTimeOffset.UtcNow, CancellationToken.None));
            Assert.True(await storeB.IsLeaseCurrentAsync(hostB, DateTimeOffset.UtcNow, CancellationToken.None));
        }
        Assert.True(hostB.FencingToken > hostA.FencingToken);
        await using (var staleHostDb = CreateDb(connectionString))
        {
            var staleHost = new PostgresDurableNavigatorStore(staleHostDb);
            Assert.False(await staleHost.IsLeaseCurrentAsync(hostA, DateTimeOffset.UtcNow, CancellationToken.None));
        }
    }

    [Fact]
    public async Task Concurrent_same_key_handlers_on_independent_contexts_return_one_replay_receipt()
    {
        await using var postgres = await StartPostgresAsync("navigator_cross_receipt_test");
        if (postgres is null) return;
        var connectionString = postgres.GetConnectionString();
        Assert.Contains("navigator_cross_receipt_test", connectionString, StringComparison.OrdinalIgnoreCase);
        await using (var migrationDb = CreateDb(connectionString))
            await migrationDb.GetService<IMigrator>().MigrateAsync();

        var tenant = Guid.NewGuid().ToString("D");
        var key = "same-key-" + Guid.NewGuid().ToString("N");
        var firstJob = CreateJob(tenant, $"{tenant}:host-a-{Guid.NewGuid():N}", key);
        var secondJob = CreateJob(tenant, $"{tenant}:host-b-{Guid.NewGuid():N}", key);
        var executor = new RendezvousExecutor(2);
        await using var dbA = CreateDb(connectionString);
        await using var dbB = CreateDb(connectionString);
        var handlerA = CreateHandler(dbA, tenant, executor);
        var handlerB = CreateHandler(dbB, tenant, executor);

        var firstTask = handlerA.HandleAsync(firstJob);
        var secondTask = handlerB.HandleAsync(secondJob);
        var receipts = await Task.WhenAll(firstTask, secondTask).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.All(receipts, receipt => Assert.Equal(OperationalRunStatus.Completed, receipt.Status));
        Assert.Equal(1, receipts.Count(receipt => receipt.IsReplay));
        Assert.Equal(1, receipts.Count(receipt => !receipt.IsReplay));
        Assert.Equal(2, executor.Calls);
        Assert.Equal(receipts[0] with { IsReplay = receipts[1].IsReplay }, receipts[1]);
        Assert.Contains(receipts[0].ExecutionId, new[] { firstJob.ExecutionId, secondJob.ExecutionId });
    }

    [Fact]
    public async Task Concurrent_same_key_handlers_for_different_tenants_return_independent_receipts()
    {
        await using var postgres = await StartPostgresAsync("navigator_cross_tenant_receipt_test");
        if (postgres is null) return;
        var connectionString = postgres.GetConnectionString();
        Assert.Contains("navigator_cross_tenant_receipt_test", connectionString, StringComparison.OrdinalIgnoreCase);
        await using (var migrationDb = CreateDb(connectionString))
            await migrationDb.GetService<IMigrator>().MigrateAsync();

        var tenantA = Guid.NewGuid().ToString("D");
        var tenantB = Guid.NewGuid().ToString("D");
        var key = "same-key-" + Guid.NewGuid().ToString("N");
        var jobA = CreateJob(tenantA, $"{tenantA}:host-a-{Guid.NewGuid():N}", key);
        var jobB = CreateJob(tenantB, $"{tenantB}:host-b-{Guid.NewGuid():N}", key);
        var executor = new RendezvousExecutor(2);
        await using var dbA = CreateDb(connectionString);
        await using var dbB = CreateDb(connectionString);
        var handlerA = CreateHandler(dbA, tenantA, executor);
        var handlerB = CreateHandler(dbB, tenantB, executor);

        var receipts = await Task.WhenAll(handlerA.HandleAsync(jobA), handlerB.HandleAsync(jobB))
            .WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(2, executor.Calls);
        Assert.All(receipts, receipt => Assert.Equal(OperationalRunStatus.Completed, receipt.Status));
        Assert.All(receipts, receipt => Assert.False(receipt.IsReplay));
        Assert.Equal(jobA.ExecutionId, receipts[0].ExecutionId);
        Assert.Equal(jobB.ExecutionId, receipts[1].ExecutionId);
        Assert.NotEqual(receipts[0].ExecutionId, receipts[1].ExecutionId);
    }

    private async Task<PostgreSqlContainer?> StartPostgresAsync(string database)
    {
        var postgres = new PostgreSqlBuilder("postgres:16-alpine").WithDatabase(database)
            .WithUsername("test").WithPassword("test").Build();
        Exception? startupError = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try { await postgres.StartAsync(); startupError = null; break; }
            catch (Exception e) { startupError = e; }
        }
        if (startupError is null) return postgres;
        output.WriteLine($"SKIP PostgresDocker: Docker/Testcontainers could not start postgres:16-alpine after 2 attempts: {startupError.Message}");
        await postgres.DisposeAsync();
        return null;
    }

    private static OpenConnectDbContext CreateDb(string connectionString) =>
        new(new DbContextOptionsBuilder<OpenConnectDbContext>().UseNpgsql(connectionString).Options);

    private static OperationalJob CreateJob(string tenant, string executionId, string key) => new(tenant,
        "service-principal", "plan", new HashSet<string> { "app" }, executionId,
        DateTimeOffset.UtcNow.AddMinutes(2), key, "policy");

    private static OperationalNavigatorHandler CreateHandler(OpenConnectDbContext db, string tenant, IOperationalStepExecutor executor)
    {
        var store = new PostgresDurableNavigatorStore(db);
        return new(new DurableOperationalLeaseStore(store, "cross-process-test"), new IdentityProvider(tenant),
            new PlanProvider(), executor, new DurableIdempotencyStore(store));
    }

    private sealed class IdentityProvider(string tenant) : IOperationalWorkloadIdentityProvider
    {
        public ValueTask<IOperationalWorkloadIdentity?> ResolveAsync(string servicePrincipalRef, string tenantScope, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IOperationalWorkloadIdentity?>(new Identity(tenant));
    }

    private sealed class Identity(string tenant) : IOperationalWorkloadIdentity
    {
        public string TenantScope { get; } = tenant;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class PlanProvider : IOperationalPlanProvider
    {
        public ValueTask<NavigatorTourPlan?> GetPlanAsync(string planId, string tenantScope, string policyRevision, CancellationToken cancellationToken) =>
            ValueTask.FromResult<NavigatorTourPlan?>(new("plan", 1,
                [new NavigatorTourStep(NavigatorTourStepType.Explain)], "home"));
    }

    private sealed class RendezvousExecutor(int participants) : IOperationalStepExecutor
    {
        private readonly TaskCompletionSource _bothEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        public async ValueTask<OperationalStepResult> ExecuteReadOnlyAsync(NavigatorTourStep step,
            IOperationalWorkloadIdentity identity, IReadOnlySet<string> applicationAllowlist,
            CancellationToken cancellationToken)
        {
            Assert.Equal(NavigatorTourStepType.Explain, step.Type);
            Assert.Contains("app", applicationAllowlist);
            if (Interlocked.Increment(ref _calls) == participants) _bothEntered.TrySetResult();
            await _bothEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            return new OperationalStepResult(true, false, "ok");
        }
    }
}
