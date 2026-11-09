using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Persistence;
using OpenConnect.Persistence.EFCore.Context;
using OpenConnect.Persistence.EFCore.DependencyInjection;
using OpenConnect.Persistence.EFCore.Entities;
using OpenConnect.Persistence.EFCore.Stores;
using Xunit;

namespace OpenConnect.Persistence.EFCore.Tests;

public sealed class TaskLifecycleStoreTests
{
    [Fact]
    public async Task Transition_requires_scoped_expected_state_and_only_allows_contract_transitions()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<RelationalPersistenceTests.ApplicationDbContext>()
            .UseSqlite(connection)
            .ReplaceService<IModelCacheKeyFactory, OpenConnectModelCacheKeyFactory>()
            .Options;
        await using var db = new RelationalPersistenceTests.ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var tasks = new EfTaskStore<RelationalPersistenceTests.ApplicationDbContext>(db);
        var id = new TaskId(Ulid.NewUlid());
        var tenant = new TenantId(Ulid.NewUlid());
        var principal = new PrincipalId(Ulid.NewUlid());
        await tasks.AcceptAsync(new(id, tenant, principal, "lifecycle-key"));

        Assert.Null(await tasks.TransitionAsync(id, tenant, principal, AgentRunState.Running, AgentRunState.Completed));
        Assert.Null(await tasks.TransitionAsync(id, new(Ulid.NewUlid()), principal, AgentRunState.Accepted, AgentRunState.Running));
        Assert.Null(await tasks.TransitionAsync(id, tenant, new(Ulid.NewUlid()), AgentRunState.Accepted, AgentRunState.Running));
        await Assert.ThrowsAsync<ArgumentException>(() => tasks.TransitionAsync(id, tenant, principal, AgentRunState.Accepted, AgentRunState.Completed));

        var trackedTask = await db.Set<TaskEntity>().SingleAsync();
        trackedTask.AgentId = "locally-modified";
        Assert.Equal(EntityState.Modified, db.Entry(trackedTask).State);

        var running = await tasks.TransitionAsync(id, tenant, principal, AgentRunState.Accepted, AgentRunState.Running);
        Assert.Equal(AgentRunState.Running, running!.State);
        Assert.Equal(id, running.Id);
        Assert.Equal(tenant, running.TenantId);
        Assert.Equal(principal, running.PrincipalId);
        Assert.Null(await tasks.TransitionAsync(id, tenant, principal, AgentRunState.Accepted, AgentRunState.Running));
        var completed = await tasks.TransitionAsync(id, tenant, principal, AgentRunState.Running, AgentRunState.Completed);
        Assert.Equal(AgentRunState.Completed, completed!.State);
        Assert.Null(await tasks.TransitionAsync(id, tenant, principal, AgentRunState.Running, AgentRunState.Cancelled));
        Assert.Equal(AgentRunState.Completed, (await tasks.GetAsync(id))!.State);
        Assert.Equal(AgentRunState.Completed, (await tasks.GetByIdempotencyKeyAsync("lifecycle-key", tenant, principal))!.State);
        Assert.Equal(nameof(AgentRunState.Completed), (await db.Set<TaskEntity>().AsNoTracking().SingleAsync()).State);
        Assert.Equal(AgentRunState.Completed, (await tasks.AcceptAsync(new(id, tenant, principal, "lifecycle-key"))).State);
        Assert.Equal("locally-modified", trackedTask.AgentId);
        Assert.Equal(EntityState.Modified, db.Entry(trackedTask).State);
        await using var freshDb = new RelationalPersistenceTests.ApplicationDbContext(options);
        var freshStore = new EfTaskStore<RelationalPersistenceTests.ApplicationDbContext>(freshDb);
        Assert.Equal(AgentRunState.Completed, (await freshStore.GetAsync(id))!.State);
        Assert.Equal(AgentRunState.Completed, (await freshStore.GetByIdempotencyKeyAsync("lifecycle-key", tenant, principal))!.State);
    }

    [Fact]
    public async Task Transition_honors_cancellation_before_database_write()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<RelationalPersistenceTests.ApplicationDbContext>()
            .UseSqlite(connection)
            .ReplaceService<IModelCacheKeyFactory, OpenConnectModelCacheKeyFactory>()
            .Options;
        await using var db = new RelationalPersistenceTests.ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var id = new TaskId(Ulid.NewUlid());
        var tenant = new TenantId(Ulid.NewUlid());
        var principal = new PrincipalId(Ulid.NewUlid());
        var store = new EfTaskStore<RelationalPersistenceTests.ApplicationDbContext>(db);
        await store.AcceptAsync(new(id, tenant, principal, "cancel-key"));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.TransitionAsync(id, tenant, principal,
            AgentRunState.Accepted, AgentRunState.Running, cancellation.Token));
        Assert.Equal(AgentRunState.Accepted, (await store.GetAsync(id))!.State);
    }

    [Fact]
    public async Task Competing_terminal_compare_and_set_transitions_have_one_winner()
    {
        var file = Path.Combine(Path.GetTempPath(), $"openconnect-lifecycle-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<RelationalPersistenceTests.ApplicationDbContext>()
                .UseSqlite($"Data Source={file};Pooling=False;Default Timeout=10")
                .ReplaceService<IModelCacheKeyFactory, OpenConnectModelCacheKeyFactory>()
                .Options;
            var id = new TaskId(Ulid.NewUlid());
            var tenant = new TenantId(Ulid.NewUlid());
            var principal = new PrincipalId(Ulid.NewUlid());
            await using (var setup = new RelationalPersistenceTests.ApplicationDbContext(options))
            {
                await setup.Database.EnsureCreatedAsync();
                var store = new EfTaskStore<RelationalPersistenceTests.ApplicationDbContext>(setup);
                await store.AcceptAsync(new(id, tenant, principal, "competing-key"));
                Assert.NotNull(await store.TransitionAsync(id, tenant, principal, AgentRunState.Accepted, AgentRunState.Running));
            }

            using var start = new Barrier(2);
            async Task<TaskRecord?> TryTerminalAsync(AgentRunState destination)
            {
                await using var context = new RelationalPersistenceTests.ApplicationDbContext(options);
                await Task.Run(start.SignalAndWait);
                return await new EfTaskStore<RelationalPersistenceTests.ApplicationDbContext>(context)
                    .TransitionAsync(id, tenant, principal, AgentRunState.Running, destination);
            }

            var results = await Task.WhenAll(TryTerminalAsync(AgentRunState.Completed), TryTerminalAsync(AgentRunState.Cancelled));
            var winner = Assert.Single(results, result => result is not null)!;
            Assert.Single(results, result => result is null);
            Assert.Contains(winner.State, new[] { AgentRunState.Completed, AgentRunState.Cancelled });
            await using var verify = new RelationalPersistenceTests.ApplicationDbContext(options);
            Assert.Equal(winner.State, (await new EfTaskStore<RelationalPersistenceTests.ApplicationDbContext>(verify).GetAsync(id))!.State);
        }
        finally
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }

    [Fact]
    public void Registration_shares_default_task_store_and_preserves_custom_registrations()
    {
        var options = new DbContextOptionsBuilder<RelationalPersistenceTests.ApplicationDbContext>()
            .UseSqlite("Data Source=:memory:")
            .ReplaceService<IModelCacheKeyFactory, OpenConnectModelCacheKeyFactory>()
            .Options;
        var defaultServices = new ServiceCollection();
        defaultServices.AddScoped(_ => new RelationalPersistenceTests.ApplicationDbContext(options));
        defaultServices.AddOpenConnectEntityFramework<RelationalPersistenceTests.ApplicationDbContext>();
        using (var provider = defaultServices.BuildServiceProvider())
        using (var scope = provider.CreateScope())
        {
            Assert.Same(scope.ServiceProvider.GetRequiredService<ITaskStore>(), scope.ServiceProvider.GetRequiredService<ITaskLifecycleStore>());
        }

        var customStore = new CustomTaskStore();
        var customLifecycle = new CustomLifecycleStore();
        var customServices = new ServiceCollection();
        customServices.AddScoped(_ => new RelationalPersistenceTests.ApplicationDbContext(options));
        customServices.AddScoped<ITaskStore>(_ => customStore);
        customServices.AddScoped<ITaskLifecycleStore>(_ => customLifecycle);
        customServices.AddOpenConnectEntityFramework<RelationalPersistenceTests.ApplicationDbContext>();
        using var customProvider = customServices.BuildServiceProvider();
        using var customScope = customProvider.CreateScope();
        Assert.Same(customStore, customScope.ServiceProvider.GetRequiredService<ITaskStore>());
        Assert.Same(customLifecycle, customScope.ServiceProvider.GetRequiredService<ITaskLifecycleStore>());
    }

    private sealed class CustomTaskStore : ITaskStore
    {
        public Task<TaskRecord> AcceptAsync(TaskAcceptance acceptance, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TaskRecord?> GetAsync(TaskId taskId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TaskRecord?> GetByIdempotencyKeyAsync(string clientTaskId, TenantId tenant, PrincipalId principal, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class CustomLifecycleStore : ITaskLifecycleStore
    {
        public Task<TaskRecord?> TransitionAsync(TaskId taskId, TenantId tenantId, PrincipalId principalId, AgentRunState expectedState, AgentRunState newState, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
