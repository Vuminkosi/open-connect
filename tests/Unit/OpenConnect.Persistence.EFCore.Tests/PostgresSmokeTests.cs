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

public sealed class PostgresSmokeTests(ITestOutputHelper output)
{
    private const string DefaultConnectionString = "Host=localhost;Port=55432;Database=oc_smoke;Username=postgres;Password=postgres;Timeout=2;";
    private static readonly bool RequireProviderTests = string.Equals(Environment.GetEnvironmentVariable("OPENCONNECT_REQUIRE_PROVIDER_TESTS"), "true", StringComparison.OrdinalIgnoreCase);
    private static readonly string? ConfiguredConnectionString = Environment.GetEnvironmentVariable("OPENCONNECT_TEST_POSTGRES_CONNECTION_STRING");
    private static readonly string ConnectionString = ConfiguredConnectionString ?? DefaultConnectionString;

    private static OpenConnectDbContext CreateContext(string? connectionString = null) => new(new DbContextOptionsBuilder<OpenConnectDbContext>().UseNpgsql(connectionString ?? ConnectionString).Options);

    [Fact]
    public async Task PostgresSmoke_MigrationContent_has_expected_openconnect_tables()
    {
        if (!await ServiceAvailableAsync()) return;
        await using var db = CreateContext();
        var script = db.Database.GenerateCreateScript();
        var lines = script.Split('\n');
        var scriptPath = Path.Combine(Path.GetTempPath(), $"efsmoke-{Guid.NewGuid():N}.sql");
        try
        {
            await File.WriteAllLinesAsync(scriptPath, lines.Take(60));
        }
        finally
        {
            if (File.Exists(scriptPath)) File.Delete(scriptPath);
        }
        Assert.All(db.Model.GetEntityTypes(), e => Assert.Equal("openconnect", e.GetSchema()));
        Assert.Equal(10, db.Model.GetEntityTypes().Count(e => e.GetSchema() == "openconnect"));
        // PgContext defines no business entities; 10 = all OpenConnect tables, zero coupling
        Assert.Equal(10, System.Text.RegularExpressions.Regex.Matches(script, @"CREATE TABLE").Count);
        Assert.Equal(10, db.Model.GetEntityTypes().Count());
        Assert.Contains("NativeAgentSessions", script);
    }

    [Fact]
    public async Task PostgresSmoke_IdempotentScriptHasOnlyOpenConnectTables()
    {
        if (!await ServiceAvailableAsync()) return;
        using var pgContext = CreateContext();
        var sql = pgContext.Database.GenerateCreateScript();
        var expectedTables = new[] { "Sessions", "Tasks", "Turns", "Events", "Outbox", "Leases", "Approvals", "PrivacyTokens", "OperationalRuns", "NativeAgentSessions" };
        Assert.All(expectedTables, table => Assert.Contains($"openconnect.\"{table}\"", sql));
        Assert.Equal(10, System.Text.RegularExpressions.Regex.Matches(sql, "CREATE TABLE").Count);
        Assert.DoesNotContain("Customers", sql);
        Assert.DoesNotContain("Orders", sql);
    }
    [Fact]
    public async Task PostgresSmoke_RoundTrip_persists_openconnect_workflows()
    {
        if (!await ServiceAvailableAsync()) return;
        await using var db = CreateContext();
        await db.Database.EnsureCreatedAsync();
        var scope = new TenantId(Ulid.NewUlid());
        var principal = new PrincipalId(Ulid.NewUlid());
        var sessionId = new SessionId(Ulid.NewUlid());
        var sessions = new EfSessionStore<OpenConnectDbContext>(db);
        await sessions.UpsertAsync(new(sessionId, scope, principal, 1));
        var taskId = new TaskId(Ulid.NewUlid());
        var tasks = new EfTaskStore<OpenConnectDbContext>(db);
        Assert.Equal(taskId, (await tasks.AcceptAsync(new(taskId, scope, principal, "pg-smoke-key"))).Id);
        var journal = new EfEventJournal<OpenConnectDbContext>(db);
        Assert.Equal(1, await journal.AppendAsync(new(sessionId, 0, "first", "one")));
        Assert.Equal(2, await journal.AppendAsync(new(sessionId, 0, "second", "two")));
        var events = new List<EventRecord>();
        await foreach (var item in journal.ReplayAsync(sessionId)) events.Add(item);
        Assert.Equal(new long[] { 1, 2 }, events.Select(e => e.Sequence));
        var outbox = new EfOutboxStore<OpenConnectDbContext>(db);
        var entry = new OutboxEntry(Guid.NewGuid(), "pg-smoke", "payload");
        await outbox.EnqueueAsync(entry);
        Assert.Single(await outbox.GetPendingAsync(10));
        var leases = new EfLeaseStore<OpenConnectDbContext>(db);
        Assert.True(await leases.TryAcquireAsync(new("pg-smoke-resource", scope, "lease-owner", DateTimeOffset.UtcNow.AddMinutes(2), 1)));
        var approvals = new EfApprovalStore<OpenConnectDbContext>(db);
        var approval = new ApprovalRecord(Guid.NewGuid(), scope, principal, taskId, "pending", null);
        await approvals.CreateAsync(approval);
        await approvals.DecideAsync(approval.Id, scope, principal, "approved");
        Assert.Equal("approved", (await approvals.GetAsync(approval.Id, scope, principal))!.Decision);
        var vault = new EfPrivacyTokenVault<OpenConnectDbContext>(db);
        var encrypted = new byte[] { 1, 2, 3 };
        await vault.StoreAsync(new("pg-token", scope, "principal", sessionId, "email", encrypted, DateTimeOffset.UtcNow.AddHours(1)));
        Assert.Equal(encrypted, (await vault.GetAsync("pg-token", scope))!.EncryptedValue);
    }

    /// <summary>
    /// Sequential replay suppression: first batch delivers 3 claimed rows and marks them
    /// delivered; an immediate sequential second batch delivers 0 with persisted
    /// status/PublishedUtc stable. The publish-success/completion-failure window is NOT
    /// covered here, so this test makes no exactly-once claim.
    /// </summary>
    [Fact]
    public async Task PostgresSmoke_DispatcherSuppressesSequentialReplay_marks_claimed_rows_delivered()
    {
        if (!await ServiceAvailableAsync()) return;
        if (string.IsNullOrWhiteSpace(ConfiguredConnectionString))
        {
            output.WriteLine("SKIP Postgres dispatcher smoke test: an explicitly configured disposable PostgreSQL fixture is required for isolated database creation.");
            return;
        }

        var database = $"oc_smoke_dispatch_{Guid.NewGuid():N}";
        var connectionString = new NpgsqlConnectionStringBuilder(ConfiguredConnectionString)
        {
            Database = database,
            Pooling = false,
            Timeout = 15
        }.ConnectionString;
        var databaseCreated = false;
        Exception? testFailure = null;
        try
        {
            await CreateIsolatedDatabaseAsync(database);
            databaseCreated = true;
            await using var db = CreateContext(connectionString);
            await db.Database.EnsureCreatedAsync();
            var entries = Enumerable.Range(0, 3).Select(i => new OutboxEntry(Guid.NewGuid(), "dispatch", $"payload-{i}")).ToArray();
            foreach (var entry in entries) await new EfOutboxStore<OpenConnectDbContext>(db).EnqueueAsync(entry);
            var publisher = new CountingPublisher();
            var dispatcher = new EfOutboxDispatcher();
            var request = new OutboxClaimRequest(10, DateTime.UtcNow.AddMinutes(1), "smoke-worker", TimeSpan.FromMinutes(1));
            Assert.Equal(3, await dispatcher.ExecuteBatchAsync(db, OpenConnectOutboxClaimStrategy.OptimisticConcurrency, request, publisher, 3, CancellationToken.None));
            Assert.Equal(3, publisher.Count);
            var entryIds = entries.Select(entry => entry.Id).ToArray();
            var expectedEventIds = entries.Select(entry => entry.Id.ToString("D")).OrderBy(id => id, StringComparer.Ordinal).ToArray();
            Assert.Equal(expectedEventIds, publisher.EventIds.OrderBy(id => id, StringComparer.Ordinal));
            Assert.Equal(publisher.EventIds.Count, publisher.EventIds.Distinct(StringComparer.Ordinal).Count());

            List<(Guid Id, string EventId, string Status, DateTime? PublishedUtc, int Attempts)> persistedAfterFirstBatch;
            await using (var readDb = CreateContext(connectionString))
            {
                var persistedRows = await readDb.Set<OutboxEntity>().AsNoTracking()
                    .Where(row => entryIds.Contains(row.Id))
                    .OrderBy(row => row.Id)
                    .Select(row => new { row.Id, row.EventId, row.Status, row.PublishedUtc, row.Attempts })
                    .ToListAsync();
                persistedAfterFirstBatch = persistedRows.Select(row => (row.Id, row.EventId, row.Status, row.PublishedUtc, row.Attempts)).ToList();
            }
            Assert.Equal(entries.Select(entry => entry.Id).OrderBy(id => id), persistedAfterFirstBatch.Select(row => row.Id));
            Assert.All(persistedAfterFirstBatch, row =>
            {
                Assert.Equal("delivered", row.Status);
                Assert.NotNull(row.PublishedUtc);
                Assert.Equal(row.Id.ToString("D"), row.EventId);
            });

            Assert.Equal(0, await dispatcher.ExecuteBatchAsync(db, OpenConnectOutboxClaimStrategy.OptimisticConcurrency, request, publisher, 3, CancellationToken.None));
            Assert.Equal(3, publisher.Count);
            Assert.Equal(expectedEventIds, publisher.EventIds.OrderBy(id => id, StringComparer.Ordinal));
            await using (var readDb = CreateContext(connectionString))
            {
                var persistedAfterSecondBatch = await readDb.Set<OutboxEntity>().AsNoTracking()
                    .Where(row => entryIds.Contains(row.Id))
                    .OrderBy(row => row.Id)
                    .Select(row => new { row.Id, row.EventId, row.Status, row.PublishedUtc, row.Attempts })
                    .ToListAsync();
                Assert.Equal(persistedAfterFirstBatch, persistedAfterSecondBatch.Select(row => (row.Id, row.EventId, row.Status, row.PublishedUtc, row.Attempts)));
            }
        }
        catch (Exception exception)
        {
            testFailure = exception;
            throw;
        }
        finally
        {
            if (databaseCreated)
            {
                try { await DropIsolatedDatabaseAsync(database); }
                catch (Exception exception) when (testFailure is not null)
                {
                    output.WriteLine($"Postgres isolated smoke database cleanup failed ({exception.GetType().Name}); the original test failure is preserved.");
                }
            }
        }
    }

    private static async Task CreateIsolatedDatabaseAsync(string database)
    {
        ValidateIsolatedDatabaseName(database);
        var adminConnectionString = new NpgsqlConnectionStringBuilder(ConfiguredConnectionString) { Pooling = false, Timeout = 15 }.ConnectionString;
        await using var connection = new NpgsqlConnection(adminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", connection) { CommandTimeout = 30 };
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DropIsolatedDatabaseAsync(string database)
    {
        ValidateIsolatedDatabaseName(database);

        var adminConnectionString = new NpgsqlConnectionStringBuilder(ConfiguredConnectionString) { Pooling = false, Timeout = 15 }.ConnectionString;
        await using var connection = new NpgsqlConnection(adminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE \"{database}\"", connection) { CommandTimeout = 30 };
        await command.ExecuteNonQueryAsync();
    }

    private static void ValidateIsolatedDatabaseName(string database)
    {
        const string prefix = "oc_smoke_dispatch_";
        if (!database.StartsWith(prefix, StringComparison.Ordinal) ||
            database.Length != prefix.Length + 32 ||
            !Guid.TryParseExact(database[prefix.Length..], "N", out _))
            throw new InvalidOperationException("Refusing to operate on a database outside the generated smoke-test name scope.");
    }

    private sealed class CountingPublisher : ICommittedOutboxPublisher
    {
        public List<string> EventIds { get; } = [];
        public int Count => EventIds.Count;
        public Task PublishAsync(string subject, string eventId, string payload, CancellationToken ct = default) { EventIds.Add(eventId); return Task.CompletedTask; }
    }

    private async Task<bool> ServiceAvailableAsync()
    {
        if (RequireProviderTests && string.IsNullOrWhiteSpace(ConfiguredConnectionString))
            throw new Xunit.Sdk.XunitException("Postgres provider is required but OPENCONNECT_TEST_POSTGRES_CONNECTION_STRING is not configured.");

        try
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            return true;
        }
        catch (Exception exception) when (exception is NpgsqlException or System.Net.Sockets.SocketException or OperationCanceledException)
        {
            if (RequireProviderTests)
                throw new Xunit.Sdk.XunitException($"Postgres provider is required but unavailable ({exception.GetType().Name}).");
            output.WriteLine($"SKIP Postgres: unavailable ({exception.GetType().Name}).");
            return false;
        }
    }
}
