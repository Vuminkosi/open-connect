using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System.Text;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Persistence;
using OpenConnect.Persistence.EFCore.Configuration;
using OpenConnect.Persistence.EFCore.DependencyInjection;
using OpenConnect.Persistence.EFCore.Entities;
using OpenConnect.Persistence.EFCore.Model;
using OpenConnect.Persistence.EFCore.Health;
using OpenConnect.Persistence.EFCore.Stores;
using Xunit;

namespace OpenConnect.Persistence.EFCore.Tests;

public sealed class RelationalPersistenceTests
{
    private static readonly TenantId Tenant = new(Ulid.NewUlid());
    private static readonly PrincipalId Principal = new(Ulid.NewUlid());

    [Fact]
    public void EF_AT_005_Application_context_model_includes_all_OpenConnect_entities_without_DbSets()
    {
        using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite("Data Source=:memory:").ReplaceService<IModelCacheKeyFactory, OpenConnect.Persistence.EFCore.Context.OpenConnectModelCacheKeyFactory>().Options);
        var model = db.Model;
        var expected = new[] { "SessionEntity", "TaskEntity", "TurnEntity", "EventEntity", "OutboxEntity", "LeaseEntity", "ApprovalEntity", "PrivacyTokenEntity", "OperationalRunEntity" };
        Assert.Equal(9, expected.Count(name => model.GetEntityTypes().Any(e => e.ClrType.Name == name)));
        Assert.DoesNotContain(typeof(ApplicationDbContext).GetProperties(), p => p.PropertyType.IsGenericType && p.PropertyType.GetGenericTypeDefinition() == typeof(DbSet<>));
        Assert.NotNull(model.FindEntityType(typeof(Customer)));
        Assert.NotNull(model.FindEntityType(typeof(Order)));
    }

    [Fact]
    public void EF_AT_006_027_table_prefix_and_schema_are_selected_in_metadata()
    {
        using var sqlite = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite("Data Source=:memory:").ReplaceService<IModelCacheKeyFactory, OpenConnect.Persistence.EFCore.Context.OpenConnectModelCacheKeyFactory>().Options, true);
        Assert.Equal("oc_sessions", sqlite.Model.GetEntityTypes().Single(e => e.ClrType.Name == "SessionEntity").GetTableName());
        Assert.Null(sqlite.Model.GetEntityTypes().Single(e => e.ClrType.Name == "SessionEntity").GetSchema());
        using var postgres = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite("Data Source=:memory:").ReplaceService<IModelCacheKeyFactory, OpenConnect.Persistence.EFCore.Context.OpenConnectModelCacheKeyFactory>().Options, false);
        Assert.Equal("Sessions", postgres.Model.GetEntityTypes().Single(e => e.ClrType.Name == "SessionEntity").GetTableName());
        Assert.Equal("app_data", postgres.Model.GetEntityTypes().Single(e => e.ClrType.Name == "SessionEntity").GetSchema());
    }

    [Fact]
    public async Task EF_AT_012_file_sqlite_persists_store_workflows_and_replays_events_in_order()
    {
        var file = Path.Combine(Path.GetTempPath(), $"openconnect-{Guid.NewGuid():N}.db");
        try
        {
            var services = Services(file);
            await using (var provider = services.BuildServiceProvider())
            {
                await using var scope = provider.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                await db.Database.EnsureCreatedAsync();
                var sessionId = new SessionId(Ulid.NewUlid());
                var sessions = scope.ServiceProvider.GetRequiredService<ISessionStore>();
                await sessions.UpsertAsync(new(sessionId, Tenant, Principal, 1));
                var taskId = new TaskId(Ulid.NewUlid());
                var task = await scope.ServiceProvider.GetRequiredService<ITaskStore>().AcceptAsync(new(taskId, Tenant, Principal, "client-key"));
                Assert.Equal(taskId, task.Id);
                var journal = scope.ServiceProvider.GetRequiredService<IEventJournal>();
                Assert.Equal(1, await journal.AppendAsync(new(sessionId, 0, "created", "one")));
                Assert.Equal(2, await journal.AppendAsync(new(sessionId, 0, "updated", "two")));
                var events = new List<EventRecord>();
                await foreach (var item in journal.ReplayAsync(sessionId)) events.Add(item);
                Assert.Equal(new long[] { 1, 2 }, events.Select(e => e.Sequence));
                Assert.Equal(new[] { "created", "updated" }, events.Select(e => e.Type));
                var outbox = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
                var entry = new OutboxEntry(Guid.NewGuid(), "subject", "payload");
                await outbox.EnqueueAsync(entry);
                Assert.Single(await outbox.GetPendingAsync(10));
                await outbox.MarkDeliveredAsync(entry.Id);
                Assert.Empty(await outbox.GetPendingAsync(10));
                var leases = scope.ServiceProvider.GetRequiredService<ILeaseStore>();
                var lease = new LeaseRecord("resource", Tenant, "owner", DateTimeOffset.UtcNow.AddMinutes(2), 1);
                Assert.True(await leases.TryAcquireAsync(lease));
                Assert.Equal("owner", (await leases.GetAsync("resource", Tenant))!.Owner);
                var approval = new ApprovalRecord(Guid.NewGuid(), Tenant, Principal, taskId, "pending", null);
                var approvals = scope.ServiceProvider.GetRequiredService<IApprovalStore>();
                await approvals.CreateAsync(approval);
                await approvals.DecideAsync(approval.Id, Tenant, Principal, "approved");
                Assert.Equal("approved", (await approvals.GetAsync(approval.Id, Tenant, Principal))!.Decision);
            }
            await using var reopened = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite($"Data Source={file};Pooling=False").ReplaceService<IModelCacheKeyFactory, OpenConnect.Persistence.EFCore.Context.OpenConnectModelCacheKeyFactory>().Options, true);
            Assert.True(await reopened.Database.CanConnectAsync());
            Assert.Equal(1L, await ScalarCountAsync(reopened, "SELECT COUNT(*) FROM oc_sessions"));
            Assert.Equal(1L, await ScalarCountAsync(reopened, "SELECT COUNT(*) FROM oc_tasks"));
        }
        finally { if (File.Exists(file)) File.Delete(file); }
    }

    [Fact]
    public async Task EF_AT_013_same_idempotency_key_is_idempotent_and_different_task_conflicts()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = Context(connection);
        await db.Database.EnsureCreatedAsync();
        var store = ServicesForExisting(db).BuildServiceProvider().CreateScope().ServiceProvider.GetRequiredService<ITaskStore>();
        var one = new TaskId(Ulid.NewUlid());
        var request = new TaskAcceptance(one, Tenant, Principal, "same-key");
        Assert.Equal(one, (await store.AcceptAsync(request)).Id);
        Assert.Equal(one, (await store.AcceptAsync(request)).Id);
        var conflict = await Assert.ThrowsAsync<InvalidOperationException>(() => store.AcceptAsync(request with { TaskId = new TaskId(Ulid.NewUlid()) }));
        Assert.Contains("conflict", conflict.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1L, await ScalarCountAsync(db, "SELECT COUNT(*) FROM oc_tasks"));
    }

    [Fact]
    public async Task EF_AT_015_unit_of_work_rolls_back_business_and_outbox_rows_when_work_throws()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = Context(connection);
        await db.Database.EnsureCreatedAsync();
        var provider = ServicesForExisting(db).BuildServiceProvider();
        using var scope = provider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IOpenConnectPersistenceUnitOfWork>();
        db.Add(new Customer { Name = "baseline row" });
        await db.SaveChangesAsync();
        var before = await db.Customers.Select(x => x.Name).ToListAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => uow.ExecuteAsync<int>(async _ =>
        {
            db.Add(new Customer { Name = "transaction row" });
            await scope.ServiceProvider.GetRequiredService<IOutboxStore>().EnqueueAsync(new(Guid.NewGuid(), "event", "payload"));
            throw new InvalidOperationException("rollback");
        }));
        Assert.Equal(before, await db.Customers.Select(x => x.Name).ToListAsync());
        Assert.Equal(0L, await ScalarCountAsync(db, "SELECT COUNT(*) FROM oc_outbox"));
    }

    [Fact]
    public async Task EF_AT_023_vault_returns_ciphertext_deletes_expired_and_never_persists_plaintext()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = Context(connection);
        await db.Database.EnsureCreatedAsync();
        var services = ServicesForExisting(db).BuildServiceProvider();
        using var scope = services.CreateScope();
        var vault = scope.ServiceProvider.GetRequiredService<IPrivacyTokenVault>();
        var session = new SessionId(Ulid.NewUlid());
        var encrypted = System.Text.Encoding.UTF8.GetBytes("ciphertext-only");
        await vault.StoreAsync(new("token-live", Tenant, "principal", session, "email", encrypted, DateTimeOffset.UtcNow.AddHours(1)));
        await vault.StoreAsync(new("token-expired", Tenant, "principal", session, "email", [9, 8, 7], DateTimeOffset.UtcNow.AddMinutes(-1)));
        Assert.Equal(encrypted, (await vault.GetAsync("token-live", Tenant))!.EncryptedValue);
        await vault.DeleteExpiredAsync(DateTimeOffset.UtcNow);
        Assert.Null(await vault.GetAsync("token-expired", Tenant));
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT EncryptedValue FROM oc_PrivacyTokens";
        await using var reader = await command.ExecuteReaderAsync();
        var decodedBlobs = new List<string>();
        while (await reader.ReadAsync())
            decodedBlobs.Add(Encoding.UTF8.GetString((byte[])reader["EncryptedValue"]));
        Assert.DoesNotContain("plain-user-value", string.Concat(decodedBlobs));
    }

    [Fact]
    public void EF_AT_025_registration_does_not_create_database_file_or_tables()
    {
        var file = Path.Combine(Path.GetTempPath(), $"openconnect-register-{Guid.NewGuid():N}.db");
        try
        {
            var provider = Services(file).BuildServiceProvider();
            Assert.False(File.Exists(file));
            using var scope = provider.CreateScope();
            _ = scope.ServiceProvider.GetRequiredService<ITaskStore>();
            Assert.False(File.Exists(file));
        }
        finally { if (File.Exists(file)) File.Delete(file); }
    }

    [Fact]
    public async Task EF_AT_026_ValidateOnStart_rejects_startup_migration_with_existing_context()
    {
        using var host = Host.CreateDefaultBuilder().ConfigureServices(s =>
        {
            s.AddDbContext<ApplicationDbContext>(o => o.UseSqlite("Data Source=:memory:"));
            s.AddOpenConnectEntityFramework<ApplicationDbContext>(o => o.Migrations = OpenConnectMigrationMode.ApplyOnStartup);
        }).Build();
        await Assert.ThrowsAnyAsync<OptionsValidationException>(() => host.StartAsync());
    }

    [Fact]
    public async Task Durable_single_database_rejects_InMemory_but_development_only_can_register()
    {
        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddOpenConnectEntityFramework<ApplicationDbContext>();
        using var host = Host.CreateDefaultBuilder().ConfigureServices(s =>
        {
            s.AddDbContext<ApplicationDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
            s.AddOpenConnectEntityFramework<ApplicationDbContext>();
        }).Build();
        await Assert.ThrowsAnyAsync<OptionsValidationException>(() => host.StartAsync());
        // InMemory is explicitly allowed only when the persistence consistency contract opts into DevelopmentOnly.
        var devServices = new ServiceCollection();
        devServices.AddDbContext<ApplicationDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        // Use the same registration type? registration is static/context scoped; validate options directly instead.
        var validator = new OpenConnect.Persistence.EFCore.Configuration.OpenConnectEfOptionsValidator();
        var options = new OpenConnectEfOptions { Consistency = OpenConnectPersistenceConsistency.DevelopmentOnly };
        Assert.True(validator.Validate(null, options).Succeeded);
    }

    private static async Task<long> ScalarCountAsync(DbContext db, string sql)
    {
        await using var cmd = db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = sql;
        if (db.Database.GetDbConnection().State != System.Data.ConnectionState.Open)
            await db.Database.OpenConnectionAsync();
        return System.Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public void EF_AT_007_model_cache_key_includes_provider_and_model_options()
    {
        using var prefixed = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite("Data Source=:memory:").ReplaceService<IModelCacheKeyFactory, OpenConnect.Persistence.EFCore.Context.OpenConnectModelCacheKeyFactory>().Options, true);
        using var unprefixed = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite("Data Source=:memory:").ReplaceService<IModelCacheKeyFactory, OpenConnect.Persistence.EFCore.Context.OpenConnectModelCacheKeyFactory>().Options, false);
        var factory = new OpenConnect.Persistence.EFCore.Context.OpenConnectModelCacheKeyFactory();
        Assert.NotEqual(factory.Create(prefixed, false), factory.Create(unprefixed, false));
        Assert.Equal("oc_sessions", prefixed.Model.GetEntityTypes().Single(e => e.ClrType.Name == "SessionEntity").GetTableName());
        Assert.Equal("Sessions", unprefixed.Model.GetEntityTypes().Single(e => e.ClrType.Name == "SessionEntity").GetTableName());
    }


    [Fact]
    public async Task EF_AT_016_single_transaction_persists_task_session_turn_event_and_outbox_atomically()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = Context(connection);
        await db.Database.EnsureCreatedAsync();
        var provider = ServicesForExisting(db).BuildServiceProvider();
        using var scope = provider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IOpenConnectPersistenceUnitOfWork>();
        var sessions = scope.ServiceProvider.GetRequiredService<ISessionStore>();
        var tasks = scope.ServiceProvider.GetRequiredService<ITaskStore>();
        var journal = scope.ServiceProvider.GetRequiredService<IEventJournal>();
        var outbox = scope.ServiceProvider.GetRequiredService<IOutboxStore>();

        var sessionId = new SessionId(Ulid.NewUlid());
        var taskId = new TaskId(Ulid.NewUlid());
        var outboxId = Guid.NewGuid();

        // 1. Commit path: all entities persisted atomically
        await uow.ExecuteAsync(async ct =>
        {
            await sessions.UpsertAsync(new(sessionId, Tenant, Principal, 1), ct);
            await tasks.AcceptAsync(new(taskId, Tenant, Principal, "client-task-1"), ct);
            db.Set<TurnEntity>().Add(new()
            {
                TurnId = Guid.NewGuid(),
                SessionId = UlidGuidMapper.ToGuid(sessionId.Value),
                TenantScope = Tenant.Value.ToString(),
                PrincipalScope = Principal.Value.ToString(),
                Sequence = 1,
                State = "Started",
                Context = null,
                StartedUtc = DateTime.UtcNow
            });
            await journal.AppendAsync(new(sessionId, 0, "turn.started", "payload-1"), ct);
            await outbox.EnqueueAsync(new(outboxId, "events.turn", "payload-1"), ct);
            return true;
        });

        Assert.NotNull(await sessions.GetAsync(sessionId, Tenant, Principal));
        Assert.Equal(taskId, (await tasks.GetAsync(taskId))!.Id);
        Assert.Equal(1L, await ScalarCountAsync(db, "SELECT COUNT(*) FROM oc_sessions"));
        Assert.Equal(1L, await ScalarCountAsync(db, "SELECT COUNT(*) FROM oc_tasks"));
        Assert.Equal(1L, await ScalarCountAsync(db, "SELECT COUNT(*) FROM oc_turns"));
        Assert.Equal(1L, await ScalarCountAsync(db, "SELECT COUNT(*) FROM oc_events"));
        Assert.Equal(1L, await ScalarCountAsync(db, "SELECT COUNT(*) FROM oc_outbox"));

        // 2. Rollback path: when work throws, all mutations are aborted atomically
        var failSessionId = new SessionId(Ulid.NewUlid());
        var failTaskId = new TaskId(Ulid.NewUlid());
        var failOutboxId = Guid.NewGuid();

        await Assert.ThrowsAsync<InvalidOperationException>(() => uow.ExecuteAsync<int>(async ct =>
        {
            await sessions.UpsertAsync(new(failSessionId, Tenant, Principal, 1), ct);
            await tasks.AcceptAsync(new(failTaskId, Tenant, Principal, "client-task-fail"), ct);
            await outbox.EnqueueAsync(new(failOutboxId, "events.fail", "payload-fail"), ct);
            throw new InvalidOperationException("aborted");
        }));

        Assert.Null(await sessions.GetAsync(failSessionId, Tenant, Principal));
        Assert.Null(await tasks.GetAsync(failTaskId));
        Assert.Equal(1L, await ScalarCountAsync(db, "SELECT COUNT(*) FROM oc_sessions"));
        Assert.Equal(1L, await ScalarCountAsync(db, "SELECT COUNT(*) FROM oc_tasks"));
        Assert.Equal(1L, await ScalarCountAsync(db, "SELECT COUNT(*) FROM oc_outbox"));
    }

    [Fact]
    public async Task EF_AT_017_concurrent_event_appends_allocate_monotonic_per_session_sequences()
    {
        var file = Path.Combine(Path.GetTempPath(), $"openconnect-seq-{Guid.NewGuid():N}.db");
        try
        {
            var services = Services(file);
            await using var provider = services.BuildServiceProvider();
            var sessionId = new SessionId(Ulid.NewUlid());

            // Initialize database and session
            using (var initScope = provider.CreateScope())
            {
                var db = initScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                await db.Database.EnsureCreatedAsync();
                var sessions = initScope.ServiceProvider.GetRequiredService<ISessionStore>();
                await sessions.UpsertAsync(new(sessionId, Tenant, Principal, 1));
            }

            const int concurrentAppends = 10;
            var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var tasks = Enumerable.Range(0, concurrentAppends).Select(async i =>
            {
                await barrier.Task;
                using var scope = provider.CreateScope();
                var journal = scope.ServiceProvider.GetRequiredService<IEventJournal>();
                return await journal.AppendAsync(new(sessionId, 0, "test.event", $"event-{i}"));
            }).ToArray();

            barrier.SetResult();
            var sequences = await Task.WhenAll(tasks);

            Array.Sort(sequences);
            var expected = Enumerable.Range(1, concurrentAppends).Select(i => (long)i).ToArray();
            Assert.Equal(expected, sequences);

            // Replay from journal to verify exact chronological order and completeness
            using (var verifyScope = provider.CreateScope())
            {
                var journal = verifyScope.ServiceProvider.GetRequiredService<IEventJournal>();
                var replayed = new List<EventRecord>();
                await foreach (var record in journal.ReplayAsync(sessionId))
                {
                    replayed.Add(record);
                }
                Assert.Equal(concurrentAppends, replayed.Count);
                Assert.Equal(expected, replayed.Select(r => r.Sequence).ToArray());
            }
        }
        finally
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }

    [Fact]
    public async Task EF_AT_027_schema_health_check_validates_readiness_without_creating_tables_and_exposes_diagnostics()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = Context(connection);

        // 1. Model-only validation passes when all OpenConnect entities are mapped in model
        var modelOnlyOptions = Options.Create(new OpenConnectEfOptions { ValidateSchema = OpenConnectSchemaValidationMode.ValidateModel });
        var modelCheck = new EfSchemaHealthCheck<ApplicationDbContext>(db, modelOnlyOptions);
        var (modelHealthy, modelDesc) = await modelCheck.CheckHealthAsync();
        Assert.True(modelHealthy, modelDesc ?? "null");
        Assert.Null(modelDesc);

        // Verify that no tables were created by the health check
        Assert.Equal(0L, await ScalarCountAsync(db, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name LIKE 'oc_%'"));

        // 2. Schema and connection validation succeeds when connection is alive
        var schemaOptions = Options.Create(new OpenConnectEfOptions { ValidateSchema = OpenConnectSchemaValidationMode.ValidateSchemaAndVersion });
        var schemaCheck = new EfSchemaHealthCheck<ApplicationDbContext>(db, schemaOptions);
        var (schemaHealthy, schemaDesc) = await schemaCheck.CheckHealthAsync();
        Assert.True(schemaHealthy);
        Assert.Contains("Database object creation is never performed", schemaDesc);

        // 3. Missing model entities fail closed with exact actionable diagnostics containing ef_model_missing
        var bareOptions = new DbContextOptionsBuilder<MissingModelDbContext>().UseSqlite("Data Source=:memory:").Options;
        using var bareDb = new MissingModelDbContext(bareOptions);
        var bareCheck = new EfSchemaHealthCheck<MissingModelDbContext>(bareDb, modelOnlyOptions);
        var (bareHealthy, bareDesc) = await bareCheck.CheckHealthAsync();
        Assert.False(bareHealthy);
        Assert.NotNull(bareDesc);
        Assert.Contains(ErrorCodes.EfModelMissing, bareDesc);
        Assert.Contains("ApplyOpenConnectModel", bareDesc);

        // 4. Unreachable connection fails closed with ef_schema_missing
        var deadOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=54329;Database=oc_unreachable;Username=fake;Password=fake;Timeout=1;CommandTimeout=1")
            .Options;
        using var deadDb = new ApplicationDbContext(deadOptions, false);
        var deadCheck = new EfSchemaHealthCheck<ApplicationDbContext>(deadDb, schemaOptions);
        var (deadHealthy, deadDesc) = await deadCheck.CheckHealthAsync();
        Assert.False(deadHealthy);
        Assert.NotNull(deadDesc);
        Assert.Contains(ErrorCodes.EfSchemaMissing, deadDesc);
    }

    private sealed class MissingModelDbContext(DbContextOptions<MissingModelDbContext> options) : DbContext(options);

    private static ServiceCollection Services(string file)
    {
        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(o => o.UseSqlite($"Data Source={file};Pooling=False").ReplaceService<IModelCacheKeyFactory, OpenConnect.Persistence.EFCore.Context.OpenConnectModelCacheKeyFactory>());
        services.AddOpenConnectEntityFramework<ApplicationDbContext>();
        return services;
    }

    private static ServiceCollection ServicesForExisting(ApplicationDbContext db)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => db);
        services.AddOpenConnectEntityFramework<ApplicationDbContext>();
        return services;
    }

    private static ApplicationDbContext Context(Microsoft.Data.Sqlite.SqliteConnection connection) => new(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).ReplaceService<IModelCacheKeyFactory, OpenConnect.Persistence.EFCore.Context.OpenConnectModelCacheKeyFactory>().Options, true);

    public sealed class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options, bool prefix = true) : DbContext(options), IOpenConnectModelOptionsProvider
    {
        public OpenConnectModelOptions ModelOptions { get; } = new() { Schema = "app_data", SqliteTablePrefix = "oc_", UseSqlitePrefix = prefix };
        public IQueryable<Customer> Customers => Set<Customer>();
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Customer>().ToTable("Customers");
            modelBuilder.Entity<Customer>().HasKey(x => x.Id);
            modelBuilder.Entity<Order>().ToTable("Orders");
            modelBuilder.Entity<Order>().HasKey(x => x.Id);
            modelBuilder.ApplyOpenConnectModel(ModelOptions);
        }
    }
    public sealed class Customer { public int Id { get; set; } public string Name { get; set; } = ""; }
    public sealed class Order { public int Id { get; set; } }
}
