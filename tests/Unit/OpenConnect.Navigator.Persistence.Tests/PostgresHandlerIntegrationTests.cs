using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
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
public sealed class PostgresHandlerIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task DI_resolved_handler_replays_completed_receipt_across_scopes()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("openconnect_handler_di_test").WithUsername("test").WithPassword("test").Build();
        try { await postgres.StartAsync(); }
        catch (Exception e)
        {
            output.WriteLine($"SKIP PostgresDocker: Docker/Testcontainers could not start postgres:16-alpine: {e.Message}");
            return;
        }

        var connectionString = postgres.GetConnectionString();
        Assert.Contains("openconnect_handler_di_test", connectionString, StringComparison.OrdinalIgnoreCase);
        await using (var migrationDb = CreateDb(connectionString))
            await migrationDb.GetService<IMigrator>().MigrateAsync();

        var tenant = Guid.NewGuid().ToString("D");
        var job = new OperationalJob(tenant, "service-principal", "plan-1", new HashSet<string> { "app" },
            $"{tenant}:handler-di-{Guid.NewGuid():N}", DateTimeOffset.UtcNow.AddMinutes(2), "idem", "policy-1");
        var identities = new FakeIdentityProvider(tenant);
        var plans = new FakePlanProvider();
        var executor = new ReadOnlyExecutor();
        var services = new ServiceCollection();
        services.AddDbContext<OpenConnectDbContext>(options => options.UseNpgsql(connectionString));
        services.AddNavigatorPostgresStore();
        services.AddNavigatorOperationalHandler(identities, plans, executor);
        await using var provider = services.BuildServiceProvider();

        OperationalReceipt first;
        await using (var scope = provider.CreateAsyncScope())
            first = await scope.ServiceProvider.GetRequiredService<IOperationalHandler>().HandleAsync(job);
        Assert.Equal(OperationalRunStatus.Completed, first.Status);
        Assert.Equal(1, first.CompletedSteps);
        Assert.True(first.FencingToken >= 1);
        Assert.Equal(1, executor.Reads);

        OperationalReceipt replay;
        await using (var scope = provider.CreateAsyncScope())
            replay = await scope.ServiceProvider.GetRequiredService<IOperationalHandler>().HandleAsync(job);
        Assert.Equal(first, replay);
        Assert.Equal(1, executor.Reads);
        Assert.Equal(1, identities.Reads);
        Assert.Equal(1, plans.Reads);
    }

    [Fact]
    public async Task Operational_handler_completes_and_fencing_advances_between_instances()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("openconnect_handler_test").WithUsername("test").WithPassword("test").Build();
        try { await postgres.StartAsync(); }
        catch (Exception e)
        {
            output.WriteLine($"SKIP PostgresDocker: Docker/Testcontainers could not start postgres:16-alpine: {e.Message}");
            return;
        }

        var connectionString = postgres.GetConnectionString();
        Assert.Contains("openconnect_handler_test", connectionString, StringComparison.OrdinalIgnoreCase);
        await using (var migrationDb = CreateDb(connectionString))
            await migrationDb.GetService<IMigrator>().MigrateAsync();

        var tenant = Guid.NewGuid().ToString("D");
        var job = new OperationalJob(tenant, "service-principal", "plan-1", new HashSet<string> { "app" },
            $"{tenant}:handler-{Guid.NewGuid():N}", DateTimeOffset.UtcNow.AddMinutes(2), "idem", "policy-1");

        await using var firstDb = CreateDb(connectionString);
        var firstExecutor = new ReadOnlyExecutor();
        var firstIdentity = new FakeIdentityProvider(tenant);
        var firstPlan = new FakePlanProvider();
        var first = await CreateHandler(firstDb, firstIdentity, firstPlan, firstExecutor).HandleAsync(job);
        Assert.Equal(OperationalRunStatus.Completed, first.Status);
        Assert.Equal(1, first.CompletedSteps);
        Assert.True(first.FencingToken >= 1);
        Assert.Equal(1, firstIdentity.Reads);
        Assert.Equal(1, firstPlan.Reads);
        Assert.Equal(1, firstExecutor.Reads);

        await using var secondDb = CreateDb(connectionString);
        var secondExecutor = new ReadOnlyExecutor();
        var second = await CreateHandler(secondDb, new FakeIdentityProvider(tenant), new FakePlanProvider(), secondExecutor).HandleAsync(job);
        Assert.Equal(OperationalRunStatus.Completed, second.Status);
        Assert.True(second.FencingToken > first.FencingToken);
        Assert.Equal(1, secondExecutor.Reads);
    }

    private static OperationalNavigatorHandler CreateHandler(OpenConnectDbContext db, IOperationalWorkloadIdentityProvider identities,
        IOperationalPlanProvider plans, IOperationalStepExecutor executor)
    {
        var store = new PostgresDurableNavigatorStore(db);
        var leases = new DurableOperationalLeaseStore(store, "navigator-operational");
        return new OperationalNavigatorHandler(leases, identities, plans, executor);
    }

    private static OpenConnectDbContext CreateDb(string connectionString) =>
        new(new DbContextOptionsBuilder<OpenConnectDbContext>().UseNpgsql(connectionString).Options);

    private sealed class FakeIdentityProvider(string tenant) : IOperationalWorkloadIdentityProvider
    {
        public int Reads { get; private set; }
        public ValueTask<IOperationalWorkloadIdentity?> ResolveAsync(string servicePrincipalRef, string tenantScope, CancellationToken cancellationToken)
        {
            Reads++;
            Assert.Equal("service-principal", servicePrincipalRef);
            Assert.Equal(tenant, tenantScope);
            return ValueTask.FromResult<IOperationalWorkloadIdentity?>(new FakeIdentity(tenant));
        }
    }

    private sealed class FakeIdentity(string tenant) : IOperationalWorkloadIdentity
    {
        public string TenantScope => tenant;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakePlanProvider : IOperationalPlanProvider
    {
        public int Reads { get; private set; }
        public ValueTask<NavigatorTourPlan?> GetPlanAsync(string planId, string tenantScope, string policyRevision, CancellationToken cancellationToken)
        {
            Reads++;
            Assert.Equal("plan-1", planId);
            return ValueTask.FromResult<NavigatorTourPlan?>(new NavigatorTourPlan("plan-1", 1,
                new[] { new NavigatorTourStep(NavigatorTourStepType.Explain, Text: "Read-only") }, "route-1"));
        }
    }

    private sealed class ReadOnlyExecutor : IOperationalStepExecutor
    {
        public int Reads { get; private set; }
        public ValueTask<OperationalStepResult> ExecuteReadOnlyAsync(NavigatorTourStep step, IOperationalWorkloadIdentity identity,
            IReadOnlySet<string> applicationAllowlist, CancellationToken cancellationToken)
        {
            Reads++;
            Assert.Equal(NavigatorTourStepType.Explain, step.Type);
            Assert.Contains("app", applicationAllowlist);
            return ValueTask.FromResult(new OperationalStepResult(true, false, "completed"));
        }
    }
}
