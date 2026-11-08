using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Persistence;
using OpenConnect.Persistence.EFCore.Configuration;
using OpenConnect.Persistence.EFCore.Context;
using OpenConnect.Persistence.EFCore.Entities;
using OpenConnect.Persistence.EFCore.Outbox;
using OpenConnect.Persistence.EFCore.Providers;
using OpenConnect.Persistence.EFCore.Stores;
using Xunit;
using Xunit.Abstractions;

namespace OpenConnect.Persistence.EFCore.Tests;

public sealed class MigratedTopologyTests(ITestOutputHelper output)
{
    private static readonly string Run = Guid.NewGuid().ToString("N");
    private static readonly string PgBase = Environment.GetEnvironmentVariable("OPENCONNECT_TEST_POSTGRES_CONNECTION_STRING") ?? "Host=localhost;Port=55432;Database=postgres;Username=postgres;Password=postgres;Timeout=2;";
    private static readonly string MsBase = Environment.GetEnvironmentVariable("OPENCONNECT_TEST_SQLSERVER_CONNECTION_STRING") ?? "Server=127.0.0.1,51434;Database=master;User Id=sa;Password=Str0ng!Passw0rd!;TrustServerCertificate=True;Connect Timeout=2";
    private static readonly bool RequireProviders = string.Equals(Environment.GetEnvironmentVariable("OPENCONNECT_REQUIRE_PROVIDER_TESTS"), "true", StringComparison.OrdinalIgnoreCase);
    private static string PgConnection(string database) { var builder = new NpgsqlConnectionStringBuilder(PgBase) { Database = database }; return builder.ConnectionString; }
    private static string MsConnection(string database) { var builder = new SqlConnectionStringBuilder(MsBase) { InitialCatalog = database }; return builder.ConnectionString; }
    private static string PgMigrated => PgConnection($"oc_migrated_pg_{Run}");
    private static string PgRoundTrip => PgConnection($"oc_rt_pg_{Run}");
    private static string MsMigrated => MsConnection($"oc_migrated_ms_{Run}");

    private static DbContextOptions<HostPgContext> PgOptions(string connectionString) =>
        new DbContextOptionsBuilder<HostPgContext>().UseNpgsql(connectionString).Options;
    private static DbContextOptions<HostMsContext> MsOptions(string connectionString) =>
        new DbContextOptionsBuilder<HostMsContext>().UseSqlServer(connectionString).Options;
    private static OpenConnectDbContext PgStoreContext(string connectionString) =>
        new(new DbContextOptionsBuilder<OpenConnectDbContext>().UseNpgsql(connectionString).Options);
    private static OpenConnectDbContext MsStoreContext(string connectionString) =>
        new(new DbContextOptionsBuilder<OpenConnectDbContext>().UseSqlServer(connectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "openconnect")).Options);

    [Fact]
    public async Task Postgres_MigrateAppliesOnlyOwnedPlusBusinessTables()
    {
        if (!await ServiceAvailableAsync(PgConnection("postgres"), "Postgres")) return;
        await CreatePostgresDatabaseAsync($"oc_migrated_pg_{Run}");
        await using var db = new HostPgContext(PgOptions(PgMigrated));
        await db.GetService<IMigrator>().MigrateAsync();

        var historyCount = await ScalarAsync<long>(PgMigrated, "SELECT COUNT(*) FROM \"__EFMigrationsHistory\"");
        Assert.True(historyCount > 0, $"Expected migration history row; found {historyCount}.");
        var owned = await ReadPostgresTablesAsync(PgMigrated, "openconnect");
        Assert.Equal(11, owned.Count);
        Assert.Contains("NativeAgentSessions", owned);
        Assert.Contains("PublicSessionMappings", owned);
        var publicTables = await ReadPostgresTablesAsync(PgMigrated, "public");
        Assert.Contains("Customers", publicTables);
        Assert.Contains("Orders", publicTables);
        Assert.Contains("__EFMigrationsHistory", publicTables);
        Assert.Equal(new[] { "Customers", "Orders", "__EFMigrationsHistory" }.OrderBy(x => x), publicTables.OrderBy(x => x));
    }

    [Fact]
    public async Task Postgres_MigratedRoundTrip()
    {
        if (!await ServiceAvailableAsync(PgConnection("postgres"), "Postgres")) return;
        await CreatePostgresDatabaseAsync($"oc_rt_pg_{Run}");
        await using (var migrationDb = new HostPgContext(PgOptions(PgRoundTrip)))
            await migrationDb.GetService<IMigrator>().MigrateAsync();
        await using var db = PgStoreContext(PgRoundTrip);
        await ExerciseStoresAsync(db, "migrated-pg");
    }

    [Fact]
    public async Task Postgres_ConcurrentClaimBatchesDeliverExactlyOnce()
    {
        if (!await ServiceAvailableAsync(PgConnection("postgres"), "Postgres")) return;
        var database = $"oc_cc_pg_{Run}";
        await CreatePostgresDatabaseAsync(database);
        var connectionString = PgConnection(database);
        await using (var migrationDb = new HostPgContext(PgOptions(connectionString)))
            await migrationDb.GetService<IMigrator>().MigrateAsync();
        await using var db = PgStoreContext(connectionString);
        var store = new EfOutboxStore<OpenConnectDbContext>(db);
        for (var i = 0; i < 20; i++) await store.EnqueueAsync(new(Guid.NewGuid(), "batch", $"payload-{i}"));
        var publisher = new RecordingPublisher();
        var calls = Enumerable.Range(0, 4).Select(i => new EfOutboxDispatcher().ExecuteBatchAsync(
            PgStoreContext(connectionString), OpenConnectOutboxClaimStrategy.OptimisticConcurrency,
            new OutboxClaimRequest(25, DateTime.UtcNow.AddMinutes(1), $"worker-{i}", TimeSpan.FromMinutes(5)), publisher, 5, CancellationToken.None)).ToArray();
        var delivered = await Task.WhenAll(calls);
        Assert.Equal(20, delivered.Sum());
        Assert.Equal(20, publisher.EventIds.Count);
        Assert.Equal(20, publisher.EventIds.Distinct().Count());
        await using var rerun = PgStoreContext(connectionString);
        Assert.Equal(0, await new EfOutboxDispatcher().ExecuteBatchAsync(rerun, OpenConnectOutboxClaimStrategy.OptimisticConcurrency,
            new OutboxClaimRequest(25, DateTime.UtcNow.AddMinutes(1), "rerun", TimeSpan.FromMinutes(5)), publisher, 5, CancellationToken.None));
    }

    [Fact]
    public async Task Postgres_PublishFailureRetriesThenQuarantines()
    {
        if (!await ServiceAvailableAsync(PgConnection("postgres"), "Postgres")) return;
        var database = $"oc_q_pg_{Run}";
        await CreatePostgresDatabaseAsync(database);
        var connectionString = PgConnection(database);
        await using (var migrationDb = new HostPgContext(PgOptions(connectionString)))
            await migrationDb.GetService<IMigrator>().MigrateAsync();
        await using var db = PgStoreContext(connectionString);
        var store = new EfOutboxStore<OpenConnectDbContext>(db);
        var entry = new OutboxEntry(Guid.NewGuid(), "failure", "payload");
        await store.EnqueueAsync(entry);
        var dispatcher = new EfOutboxDispatcher();
        var publisher = new ThrowingPublisher();
        for (var i = 0; i < 3; i++)
        {
            await using var attemptDb = PgStoreContext(connectionString);
            await dispatcher.ExecuteBatchAsync(attemptDb, OpenConnectOutboxClaimStrategy.OptimisticConcurrency,
                new OutboxClaimRequest(5, DateTime.UtcNow.AddMinutes(10), "failure-dispatcher", TimeSpan.FromMinutes(5)), publisher, 2, CancellationToken.None);
            if (i < 2) await Task.Delay(TimeSpan.FromSeconds(5));
        }
        Assert.DoesNotContain(await store.GetPendingAsync(10), x => x.Id == entry.Id);
        var status = await db.Set<OutboxEntity>().AsNoTracking().Where(x => x.Id == entry.Id).Select(x => x.Status).SingleAsync();
        var attempts = await db.Set<OutboxEntity>().AsNoTracking().Where(x => x.Id == entry.Id).Select(x => x.Attempts).SingleAsync();
        Assert.Equal("quarantined", status);
        Assert.True(attempts >= 2, $"Expected at least two attempts, got {attempts}.");
    }

    [Fact]
    public async Task SqlServer_MigrateAndRoundTrip()
    {
        if (!await ServiceAvailableAsync(MsConnection("master"), "SQL Server")) return;
        await CreateSqlServerDatabaseAsync($"oc_migrated_ms_{Run}");
        await using (var migrationDb = new HostMsContext(MsOptions(MsMigrated)))
            await migrationDb.GetService<IMigrator>().MigrateAsync();
        // HostMsContext uses EF Core default dbo history placement; custom openconnect history remains deployment guidance.
        var historyCount = await ScalarSqlServerAsync<long>(MsMigrated, "SELECT COUNT(*) FROM [dbo].[__EFMigrationsHistory]");
        Assert.True(historyCount > 0, $"Expected migration history row; found {historyCount}.");
        var ownedCount = await ScalarSqlServerAsync<int>(MsMigrated, "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = 'openconnect'");
        Assert.Equal(11, ownedCount);

        await using var db = MsStoreContext(MsMigrated);
        await ExerciseSessionTaskOutboxAsync(db, "migrated-ms");
        await ExerciseNativeAgentSessionStoreAsync(db);
        var publisher = new RecordingPublisher();
        var delivered = await new EfOutboxDispatcher().ExecuteBatchAsync(db, OpenConnectOutboxClaimStrategy.OptimisticConcurrency,
            new OutboxClaimRequest(25, DateTime.UtcNow.AddMinutes(1), "sql-worker", TimeSpan.FromMinutes(5)), publisher, 5, CancellationToken.None);
        Assert.Equal(1, delivered);
        Assert.Single(publisher.EventIds);
    }

    private static async Task ExerciseStoresAsync(OpenConnectDbContext db, string suffix)
    {
        await ExerciseSessionTaskOutboxAsync(db, suffix);
        var session = await new EfSessionStore<OpenConnectDbContext>(db).GetAsync(_sessionId!.Value, _tenant!.Value, _principal!.Value);
        Assert.NotNull(session);
        var journal = new EfEventJournal<OpenConnectDbContext>(db);
        Assert.Equal(1, await journal.AppendAsync(new(_sessionId!.Value, 0, "first", "one")));
        Assert.Equal(2, await journal.AppendAsync(new(_sessionId!.Value, 0, "second", "two")));
        var events = new List<EventRecord>();
        await foreach (var item in journal.ReplayAsync(_sessionId!.Value)) events.Add(item);
        Assert.Equal(new long[] { 1, 2 }, events.Select(x => x.Sequence));
        var leases = new EfLeaseStore<OpenConnectDbContext>(db);
        Assert.True(await leases.TryAcquireAsync(new($"resource-{suffix}", _tenant!.Value, "owner", DateTimeOffset.UtcNow.AddMinutes(2), 1)));
        var task = await new EfTaskStore<OpenConnectDbContext>(db).GetByIdempotencyKeyAsync($"task-{suffix}", _tenant!.Value, _principal!.Value);
        Assert.NotNull(task);
        var approvals = new EfApprovalStore<OpenConnectDbContext>(db);
        var approvalId = Guid.NewGuid();
        await approvals.CreateAsync(new(approvalId, _tenant!.Value, _principal!.Value, _taskId!.Value, "pending", null));
        await approvals.DecideAsync(approvalId, _tenant!.Value, _principal!.Value, "approved");
        Assert.Equal("approved", (await approvals.GetAsync(approvalId, _tenant!.Value, _principal!.Value))!.Decision);
        var vault = new EfPrivacyTokenVault<OpenConnectDbContext>(db);
        var ciphertext = new byte[] { 3, 1, 4 };
        await vault.StoreAsync(new($"token-{suffix}", _tenant!.Value, "principal", _sessionId!.Value, "email", ciphertext, DateTimeOffset.UtcNow.AddHours(1)));
        Assert.Equal(ciphertext, (await vault.GetAsync($"token-{suffix}", _tenant!.Value))!.EncryptedValue);
        await ExerciseNativeAgentSessionStoreAsync(db);
    }

    private static async Task ExerciseNativeAgentSessionStoreAsync(OpenConnectDbContext db)
    {
        var binding = new NativeAgentSessionBinding(_sessionId!.Value, _tenant!.Value, _principal!.Value,
            "fixture-agent", "revision-1", "maf-1", "schema-1", "provider-1");
        var store = new EfNativeAgentSessionStore<OpenConnectDbContext>(db);
        var record = new NativeAgentSessionRecord(binding, 1, [9, 8, 7]);
        Assert.Equal(NativeAgentSessionSaveOutcome.Saved, await store.SaveAsync(record, 0));
        var restored = await store.GetAsync(binding);
        Assert.NotNull(restored);
        Assert.Equal(1, restored.Version);
        Assert.Equal(new byte[] { 9, 8, 7 }, restored.GetCiphertextCopy());
    }

    private static TenantId? _tenant;
    private static PrincipalId? _principal;
    private static SessionId? _sessionId;
    private static TaskId? _taskId;

    private static async Task ExerciseSessionTaskOutboxAsync(OpenConnectDbContext db, string suffix)
    {
        _tenant = new TenantId(Ulid.NewUlid()); _principal = new PrincipalId(Ulid.NewUlid());
        _sessionId = new SessionId(Ulid.NewUlid()); _taskId = new TaskId(Ulid.NewUlid());
        var sessions = new EfSessionStore<OpenConnectDbContext>(db);
        await sessions.UpsertAsync(new(_sessionId!.Value, _tenant!.Value, _principal!.Value, 1));
        var tasks = new EfTaskStore<OpenConnectDbContext>(db);
        Assert.Equal(_taskId, (await tasks.AcceptAsync(new(_taskId!.Value, _tenant!.Value, _principal!.Value, $"task-{suffix}"))).Id);
        Assert.Equal(_taskId, (await tasks.AcceptAsync(new(_taskId!.Value, _tenant!.Value, _principal!.Value, $"task-{suffix}"))).Id);
        await new EfOutboxStore<OpenConnectDbContext>(db).EnqueueAsync(new(Guid.NewGuid(), suffix, "payload"));
    }

    private static async Task CreatePostgresDatabaseAsync(string database)
    {
        await using var connection = new NpgsqlConnection(PgConnection("postgres")); await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", connection); await command.ExecuteNonQueryAsync();
    }

    private async Task<bool> ServiceAvailableAsync(string connectionString, string service)
    {
        try
        {
            if (service == "Postgres")
            { await using var connection = new NpgsqlConnection(connectionString); await connection.OpenAsync(); }
            else
            { await using var connection = new SqlConnection(connectionString); await connection.OpenAsync(); }
            return true;
        }
        catch (Exception exception) when (exception is NpgsqlException or SqlException or System.Net.Sockets.SocketException or OperationCanceledException)
        {
            if (RequireProviders) throw new Xunit.Sdk.XunitException($"{service} provider is required but unavailable ({exception.GetType().Name}).");
            output.WriteLine($"SKIP {service}: unavailable ({exception.GetType().Name}).");
            return false;
        }
    }
    private static async Task CreateSqlServerDatabaseAsync(string database)
    {
        await using var connection = new SqlConnection(MsConnection("master")); await connection.OpenAsync();
        await using var command = new SqlCommand($"CREATE DATABASE [{database}]", connection); await command.ExecuteNonQueryAsync();
    }
    private static async Task<List<string>> ReadPostgresTablesAsync(string connectionString, string schema)
    {
        await using var connection = new NpgsqlConnection(connectionString); await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT table_name FROM information_schema.tables WHERE table_schema = @schema AND table_type = 'BASE TABLE' ORDER BY table_name", connection);
        command.Parameters.AddWithValue("schema", schema);
        await using var reader = await command.ExecuteReaderAsync(); var result = new List<string>();
        while (await reader.ReadAsync()) result.Add(reader.GetString(0)); return result;
    }
    private static async Task<T> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString); await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection); return (T)Convert.ChangeType(await command.ExecuteScalarAsync(), typeof(T))!;
    }
    private static async Task<T> ScalarSqlServerAsync<T>(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString); await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection); return (T)Convert.ChangeType(await command.ExecuteScalarAsync(), typeof(T))!;
    }
    private sealed class RecordingPublisher : ICommittedOutboxPublisher
    {
        private readonly System.Collections.Concurrent.ConcurrentBag<string> _ids = [];
        public IReadOnlyCollection<string> EventIds => _ids.ToArray();
        public Task PublishAsync(string subject, string eventId, string payload, CancellationToken ct = default) { _ids.Add(eventId); return Task.CompletedTask; }
    }
    private sealed class ThrowingPublisher : ICommittedOutboxPublisher
    {
        public Task PublishAsync(string subject, string eventId, string payload, CancellationToken ct = default) => Task.FromException(new InvalidOperationException("expected test failure"));
    }
}
