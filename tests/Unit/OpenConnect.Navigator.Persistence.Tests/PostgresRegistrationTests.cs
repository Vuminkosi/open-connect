using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Navigator.Operational;
using OpenConnect.Navigator.Operational.Durable;
using OpenConnect.Navigator.Persistence.Postgres;
using OpenConnect.Persistence.Postgres;
using Xunit;

namespace OpenConnect.Navigator.Persistence.Tests;

public sealed class PostgresRegistrationTests
{
    [Fact]
    public async Task Postgres_registration_resolves_durable_stores_and_fails_closed_without_tenant_scope()
    {
        var services = new ServiceCollection();
        services.AddDbContext<OpenConnectDbContext>(options => options.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddScoped<PostgresDurableNavigatorStore>();
        services.AddNavigatorPostgresStore();

        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        Assert.IsType<PostgresDurableNavigatorStore>(scope.ServiceProvider.GetRequiredService<IDurableNavigatorStore>());
        var leaseStore = Assert.IsType<DurableOperationalLeaseStore>(scope.ServiceProvider.GetRequiredService<IOperationalLeaseStore>());
        Assert.True(leaseStore.IsDurable);
        Assert.Null(await leaseStore.TryAcquireAsync("execution-without-tenant", TimeSpan.FromMinutes(1), default));
    }

    [Fact]
    public void Postgres_registration_overrides_host_in_memory_lease_registration()
    {
        var services = new ServiceCollection();
        services.AddScoped<IOperationalLeaseStore, InMemoryOperationalLeaseStore>();
        services.AddDbContext<OpenConnectDbContext>(options => options.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddScoped<PostgresDurableNavigatorStore>();

        services.AddNavigatorPostgresStore();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        Assert.IsType<DurableOperationalLeaseStore>(scope.ServiceProvider.GetRequiredService<IOperationalLeaseStore>());
        Assert.True(scope.ServiceProvider.GetRequiredService<IOperationalLeaseStore>().IsDurable);
    }
}
