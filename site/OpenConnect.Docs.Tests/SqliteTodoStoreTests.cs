using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Abstractions;
using OpenConnect.Samples.Blazor;
using OpenConnect.Samples.Blazor.Models;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class SqliteTodoStoreTests
{
    [Fact]
    public async Task Migration_and_store_preserve_data_across_service_recreation_and_isolate_tenants()
    {
        var directory = Path.Combine(Path.GetTempPath(), "openconnect-todo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var connection = $"Data Source={Path.Combine(directory, "todo.db")};Pooling=False";
        try
        {
            var tenantA = new TenantId(Ulid.NewUlid());
            var tenantB = new TenantId(Ulid.NewUlid());
            Guid localId;
            Guid tenantId;
            var dueDate = new DateTime(2026, 12, 4, 10, 30, 0, DateTimeKind.Utc);
            await using (var services = CreateServices(connection))
            {
                await using (var scope = services.CreateAsyncScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<BlazorDbContext>();
                    await db.GetService<IMigrator>().MigrateAsync("20261118170000_PrivacyTokenScopeUniqueness");
                    await db.Database.ExecuteSqlRawAsync("CREATE TABLE preserved_marker (value TEXT NOT NULL)");
                    await db.Database.ExecuteSqlRawAsync("INSERT INTO preserved_marker (value) VALUES ('keep')");
                    await db.Database.MigrateAsync();
                    Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM preserved_marker WHERE value = 'keep'").SingleAsync());
                }
                var store = services.GetRequiredService<SqliteTodoStore>();
                await Assert.ThrowsAsync<ArgumentException>(() => store.AddAsync("Ambiguous date", dueDate: new DateTime(2026, 12, 4)));
                localId = (await store.AddAsync("Persistent local task", dueDate: dueDate)).Id;
                tenantId = (await store.AddAsync(tenantA, "Tenant A private task")).Id;
                var concurrentTenant = new TenantId(Ulid.NewUlid());
                var created = await Task.WhenAll(Enumerable.Range(0, 8)
                    .Select(index => store.AddAsync(concurrentTenant, $"Concurrent task {index}")));
                Assert.Equal(8, created.Select(item => item.Id).Distinct().Count());
                Assert.Equal(8, (await store.GetAllAsync(concurrentTenant)).Count(item => item.Title.StartsWith("Concurrent task ", StringComparison.Ordinal)));
                await using var otherServices = CreateServices(connection);
                var otherStore = otherServices.GetRequiredService<SqliteTodoStore>();
                var newTenant = new TenantId(Ulid.NewUlid());
                var crossStore = await Task.WhenAll(Enumerable.Range(0, 8).Select(index =>
                    (index % 2 == 0 ? store : otherStore).AddAsync(newTenant, $"Cross-store task {index}")));
                Assert.Equal(8, crossStore.Select(item => item.Id).Distinct().Count());
                Assert.Equal(8, (await store.GetAllAsync(newTenant)).Count(item => item.Title.StartsWith("Cross-store task ", StringComparison.Ordinal)));
                Assert.True(await store.UpdateStatusAsync(localId, true));
                Assert.False(await store.UpdateStatusAsync(tenantB, tenantId, true));
                Assert.False(await store.DeleteAsync(tenantB, tenantId));
                Assert.DoesNotContain(await store.GetAllAsync(tenantB), item => item.Id == tenantId || item.Id == localId);
            }
            await using (var services = CreateServices(connection))
            {
                var store = services.GetRequiredService<SqliteTodoStore>();
                var local = await store.GetByIdAsync(localId);
                Assert.NotNull(local);
                Assert.True(local.IsCompleted);
                Assert.Equal(dueDate, local.DueDateUtc);
                Assert.Equal(DateTimeKind.Utc, local.DueDateUtc!.Value.Kind);
                Assert.Contains(await store.GetAllAsync(tenantA), item => item.Id == tenantId);
                Assert.DoesNotContain(await store.GetAllAsync(tenantB), item => item.Id == tenantId || item.Id == localId);
                foreach (var item in await store.GetAllAsync())
                    Assert.True(await store.DeleteAsync(item.Id));
            }
            await using (var services = CreateServices(connection))
            {
                Assert.Empty(await services.GetRequiredService<SqliteTodoStore>().GetAllAsync());
                await using var scope = services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<BlazorDbContext>();
                await db.GetService<IMigrator>().MigrateAsync("20261118170000_PrivacyTokenScopeUniqueness");
                Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM preserved_marker WHERE value = 'keep'").SingleAsync());
            }
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(directory))
                File.Delete(file);
            Directory.Delete(directory);
        }
    }

    private static ServiceProvider CreateServices(string connection)
    {
        var services = new ServiceCollection();
        services.AddDbContext<BlazorDbContext>(options => BlazorDbContext.ConfigureSqlite(options, connection));
        services.AddSingleton(sp => new SqliteTodoStore(sp.GetRequiredService<IServiceScopeFactory>(), acceptanceData: true));
        return services.BuildServiceProvider();
    }
}
