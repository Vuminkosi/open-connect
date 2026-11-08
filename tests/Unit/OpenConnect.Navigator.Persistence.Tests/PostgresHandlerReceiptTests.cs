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
public sealed class PostgresHandlerReceiptTests(ITestOutputHelper output)
{
    [Fact]
    public async Task CompletedReceiptReplaysAcrossHandlerInstancesUsingPostgres()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:16-alpine").WithDatabase("handler_receipt_test")
            .WithUsername("test").WithPassword("test").Build();
        Exception? startupError = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try { await postgres.StartAsync(); startupError = null; break; }
            catch (Exception e) { startupError = e; }
        }
        if (startupError is not null)
        {
            output.WriteLine($"SKIP PostgresDocker: Docker/Testcontainers could not start postgres:16-alpine after 2 attempts: {startupError.Message}");
            return;
        }

        var connectionString = postgres.GetConnectionString();
        Assert.Contains("handler_receipt_test", connectionString, StringComparison.OrdinalIgnoreCase);
        await using (var migration = CreateDb(connectionString)) await migration.GetService<IMigrator>().MigrateAsync();
        var tenant = Guid.NewGuid().ToString("N");
        var job = new OperationalJob(tenant, "principal", "plan", new HashSet<string> { "app" },
            tenant + ":execution", DateTimeOffset.UtcNow.AddMinutes(1), "idempotency", "policy");
        var executor = new CountingExecutor();
        await using (var firstDb = CreateDb(connectionString))
            Assert.Equal(OperationalRunStatus.Completed, (await Create(firstDb, tenant, executor).HandleAsync(job)).Status);
        await using (var secondDb = CreateDb(connectionString))
            Assert.Equal(OperationalRunStatus.Completed, (await Create(secondDb, tenant, executor).HandleAsync(job)).Status);
        Assert.Equal(1, executor.Calls);
    }

    private static OpenConnectDbContext CreateDb(string connectionString) =>
        new(new DbContextOptionsBuilder<OpenConnectDbContext>().UseNpgsql(connectionString).Options);

    private static OperationalNavigatorHandler Create(OpenConnectDbContext db, string tenant, CountingExecutor executor)
    {
        var durable = new PostgresDurableNavigatorStore(db);
        return new(new DurableOperationalLeaseStore(durable, "handler-receipt-test"), new Identity(tenant), new Plan(), executor,
            new DurableIdempotencyStore(durable));
    }

    private sealed class Identity(string tenant) : IOperationalWorkloadIdentityProvider
    {
        public ValueTask<IOperationalWorkloadIdentity?> ResolveAsync(string servicePrincipalRef, string tenantScope, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IOperationalWorkloadIdentity?>(new WorkloadIdentity(tenant));
    }
    private sealed class WorkloadIdentity(string tenant) : IOperationalWorkloadIdentity
    {
        public string TenantScope { get; } = tenant;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Plan : IOperationalPlanProvider
    {
        public ValueTask<NavigatorTourPlan?> GetPlanAsync(string planId, string tenantScope, string policyRevision, CancellationToken cancellationToken) =>
            ValueTask.FromResult<NavigatorTourPlan?>(new("plan", 1, [new(NavigatorTourStepType.Explain)], "home"));
    }
    private sealed class CountingExecutor : IOperationalStepExecutor
    {
        public int Calls { get; private set; }
        public ValueTask<OperationalStepResult> ExecuteReadOnlyAsync(NavigatorTourStep step, IOperationalWorkloadIdentity identity,
            IReadOnlySet<string> applicationAllowlist, CancellationToken cancellationToken)
        { Calls++; return ValueTask.FromResult(new OperationalStepResult(true, false, "ok")); }
    }
}
