using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
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

public sealed class PostgresSmokeFixtureFactAttribute : FactAttribute
{
    public PostgresSmokeFixtureFactAttribute()
    {
        if (!PostgresSmokeTests.RequireProviderTests && string.IsNullOrWhiteSpace(PostgresSmokeTests.ConfiguredConnectionString))
            Skip = "NOT VERIFIED: an explicitly configured disposable PostgreSQL fixture is required.";
    }
}

public sealed class PostgresSmokeTests(ITestOutputHelper output)
{
    internal static readonly bool RequireProviderTests = string.Equals(Environment.GetEnvironmentVariable("OPENCONNECT_REQUIRE_PROVIDER_TESTS"), "true", StringComparison.OrdinalIgnoreCase);
    internal static readonly string? ConfiguredConnectionString = Environment.GetEnvironmentVariable("OPENCONNECT_TEST_POSTGRES_CONNECTION_STRING");

    private static OpenConnectDbContext CreateScriptContext() =>
        new(new DbContextOptionsBuilder<OpenConnectDbContext>().UseNpgsql().Options);

    private static OpenConnectDbContext CreateContext(string connectionString) =>
        new(new DbContextOptionsBuilder<OpenConnectDbContext>().UseNpgsql(connectionString).Options);

    private static readonly string[] ExpectedTables =
    [
        "ApprovalNonces", "Approvals", "Events", "Leases",
        "MessagingAttachments", "MessagingCalls", "MessagingContinuations", "MessagingConversations",
        "MessagingDevices", "MessagingEnrollments", "MessagingFollowUps", "MessagingKeyPackages",
        "MessagingMessages", "MessagingParticipants", "MessagingPushRegistrations", "MessagingReceipts",
        "MessagingRefreshGrants", "NativeAgentSessions", "OperationalRuns", "Outbox",
        "PrivacyTokens", "PublicSessionMappings", "Sessions", "Tasks",
        "Turns",
    ];

    [Fact]
    public void PostgresSmoke_ModelHasExpectedOpenConnectTables()
    {
        using var db = CreateScriptContext();
        var tables = db.Model.GetEntityTypes().ToArray();
        Assert.All(tables, table => Assert.Equal("openconnect", table.GetSchema()));
        var names = tables.Select(table => table.GetTableName()).OrderBy(name => name, StringComparer.Ordinal);
        Assert.Equal(ExpectedTables, names);
    }

    [Fact]
    public void PostgresSmoke_CreateScriptHasOnlyOpenConnectTables()
    {
        using var db = CreateScriptContext();
        var sql = db.Database.GenerateCreateScript();
        var generationHelper = db.GetService<ISqlGenerationHelper>();
        var expected = ExpectedTables.Select(table => generationHelper.DelimitIdentifier(table, "openconnect"));
        const string prefix = "CREATE TABLE ";
        const string suffix = " (";
        var createStatements = sql.ReplaceLineEndings("\n").Split('\n')
            .Where(line => line.StartsWith(prefix, StringComparison.Ordinal))
            .ToArray();
        Assert.All(createStatements, line => Assert.EndsWith(suffix, line));
        var actual = createStatements.Select(line => line[prefix.Length..^suffix.Length]);
        Assert.Equal(expected.Order(StringComparer.Ordinal), actual.Order(StringComparer.Ordinal));
        Assert.DoesNotContain("Customers", sql);
        Assert.DoesNotContain("Orders", sql);
    }

    [PostgresSmokeFixtureFact]
    public async Task PostgresSmoke_RoundTrip_persists_openconnect_workflows()
    {
        var configuredConnectionString = GetConfiguredFixture();

        var database = $"oc_smoke_roundtrip_{Guid.NewGuid():N}";
        var connectionString = new NpgsqlConnectionStringBuilder(configuredConnectionString)
        {
            Database = database,
            Pooling = false,
            Timeout = 15
        }.ConnectionString;
        var databaseCreated = false;
        Exception? testFailure = null;
        try
        {
            await CreateIsolatedDatabaseAsync(configuredConnectionString, database);
            databaseCreated = true;
            await using var db = CreateContext(connectionString);
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
            await foreach (var item in journal.ReplayAsync(sessionId))
                events.Add(item);
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
            Assert.Equal(encrypted, (await vault.GetAsync("pg-token", scope, "principal", sessionId))!.EncryptedValue);
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
                try
                { await DropIsolatedDatabaseAsync(configuredConnectionString, database); }
                catch (Exception exception) when (testFailure is not null)
                {
                    output.WriteLine($"Postgres isolated smoke database cleanup failed ({exception.GetType().Name}); the original test failure is preserved.");
                }
            }
        }
    }

    /// <summary>
    /// Sequential replay suppression: first batch delivers 3 claimed rows and marks them
    /// delivered; an immediate sequential second batch delivers 0 with persisted
    /// status/PublishedUtc stable. The publish-success/completion-failure window is NOT
    /// covered here, so this test makes no exactly-once claim.
    /// </summary>
    [PostgresSmokeFixtureFact]
    public async Task PostgresSmoke_DispatcherSuppressesSequentialReplay_marks_claimed_rows_delivered()
    {
        var configuredConnectionString = GetConfiguredFixture();

        var database = $"oc_smoke_dispatch_{Guid.NewGuid():N}";
        var connectionString = new NpgsqlConnectionStringBuilder(configuredConnectionString)
        {
            Database = database,
            Pooling = false,
            Timeout = 15
        }.ConnectionString;
        var databaseCreated = false;
        Exception? testFailure = null;
        try
        {
            await CreateIsolatedDatabaseAsync(configuredConnectionString, database);
            databaseCreated = true;
            await using var db = CreateContext(connectionString);
            await db.Database.EnsureCreatedAsync();
            var entries = Enumerable.Range(0, 3).Select(i => new OutboxEntry(Guid.NewGuid(), "dispatch", $"payload-{i}")).ToArray();
            foreach (var entry in entries)
                await new EfOutboxStore<OpenConnectDbContext>(db).EnqueueAsync(entry);
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
                try
                { await DropIsolatedDatabaseAsync(configuredConnectionString, database); }
                catch (Exception exception) when (testFailure is not null)
                {
                    output.WriteLine($"Postgres isolated smoke database cleanup failed ({exception.GetType().Name}); the original test failure is preserved.");
                }
            }
        }
    }

    private static async Task CreateIsolatedDatabaseAsync(string configuredConnectionString, string database)
    {
        ValidateIsolatedDatabaseName(database);
        var adminConnectionString = new NpgsqlConnectionStringBuilder(configuredConnectionString) { Pooling = false, Timeout = 15 }.ConnectionString;
        await using var connection = new NpgsqlConnection(adminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", connection) { CommandTimeout = 30 };
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DropIsolatedDatabaseAsync(string configuredConnectionString, string database)
    {
        ValidateIsolatedDatabaseName(database);

        var adminConnectionString = new NpgsqlConnectionStringBuilder(configuredConnectionString) { Pooling = false, Timeout = 15 }.ConnectionString;
        await using var connection = new NpgsqlConnection(adminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE \"{database}\"", connection) { CommandTimeout = 30 };
        await command.ExecuteNonQueryAsync();
    }

    private static void ValidateIsolatedDatabaseName(string database)
    {
        const string dispatchPrefix = "oc_smoke_dispatch_";
        const string roundTripPrefix = "oc_smoke_roundtrip_";
        var prefix = database.StartsWith(dispatchPrefix, StringComparison.Ordinal)
            ? dispatchPrefix
            : database.StartsWith(roundTripPrefix, StringComparison.Ordinal)
                ? roundTripPrefix
                : null;
        if (prefix is null ||
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

    private static string GetConfiguredFixture()
    {
        if (string.IsNullOrWhiteSpace(ConfiguredConnectionString))
            throw new Xunit.Sdk.XunitException("Postgres provider is required but OPENCONNECT_TEST_POSTGRES_CONNECTION_STRING is not configured.");

        return ConfiguredConnectionString;
    }
}
