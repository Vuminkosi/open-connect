using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Abstractions;
using OpenConnect.Persistence.EFCore.Context;
using OpenConnect.Persistence.EFCore.DependencyInjection;
using OpenConnect.Persistence.EFCore.Entities;
using OpenConnect.Persistence.EFCore.Model;
using Xunit;

namespace OpenConnect.Persistence.EFCore.Tests;

public sealed class AcceptedTaskStateTests
{
    [Fact]
    public async Task Accepting_a_task_persists_and_returns_Accepted_across_idempotent_and_read_paths()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<RelationalPersistenceTests.ApplicationDbContext>()
            .UseSqlite(connection)
            .ReplaceService<IModelCacheKeyFactory, OpenConnectModelCacheKeyFactory>()
            .Options;
        await using var db = new RelationalPersistenceTests.ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var services = new ServiceCollection();
        services.AddScoped(_ => db);
        services.AddOpenConnectEntityFramework<RelationalPersistenceTests.ApplicationDbContext>();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ITaskStore>();
        var taskId = new TaskId(Ulid.NewUlid());
        var tenant = new TenantId(Ulid.NewUlid());
        var principal = new PrincipalId(Ulid.NewUlid());
        var acceptance = new TaskAcceptance(taskId, tenant, principal, "accepted-state-key");

        Assert.Equal(AgentRunState.Accepted, (await store.AcceptAsync(acceptance)).State);
        Assert.Equal(nameof(AgentRunState.Accepted), (await db.Set<TaskEntity>().SingleAsync()).State);
        Assert.Equal(AgentRunState.Accepted, (await store.GetAsync(taskId))!.State);
        Assert.Equal(AgentRunState.Accepted, (await store.GetByIdempotencyKeyAsync("accepted-state-key", tenant, principal))!.State);
        Assert.Equal(AgentRunState.Accepted, (await store.AcceptAsync(acceptance)).State);
    }
}
