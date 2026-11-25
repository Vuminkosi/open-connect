using Docker.DotNet.Models;
using DotNet.Testcontainers.Images;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Persistence;
using OpenConnect.Persistence.EFCore.Context;
using OpenConnect.Persistence.EFCore.Entities;
using OpenConnect.Persistence.EFCore.Stores;
using Testcontainers.PostgreSql;
using Xunit;

namespace OpenConnect.Persistence.EFCore.Tests;

public sealed class EventJournalReplayProviderTests
{
    private const string PostgresOptIn = "OPENCONNECT_REQUIRE_EVENT_REPLAY_POSTGRES";
    private const string SqlServerOptIn = "OPENCONNECT_REQUIRE_EVENT_REPLAY_SQLSERVER";
    private const int EventCount = 457;
    private const int Cursor = 19;

    [DisposableEventReplayFact(PostgresOptIn)]
    [Trait("Category", "EventJournalReplayPostgres")]
    public async Task PostgreSql_disposable_fixture_replays_every_ordered_event_after_the_cursor()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = timeout.Token;
        var database = "oc_event_replay_" + Guid.NewGuid().ToString("N");
        await using var postgres = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDockerEndpoint("unix:///var/run/docker.sock")
            .WithDatabase(database).WithUsername("test").WithPassword("test")
            .WithImagePullPolicy(PullPolicy.Never)
            .WithCreateParameterModifier(RestrictPostgresHostBinding)
            .Build();
        await postgres.StartAsync(ct);

        // Only the loopback port owned by the new fixture; no configured provider URL.
        var connection = new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1",
            Port = postgres.GetMappedPublicPort(5432),
            Database = database,
            Username = "test",
            Password = "test",
            Pooling = false,
        };
        await using var db = new OpenConnectDbContext(
            new DbContextOptionsBuilder<OpenConnectDbContext>().UseNpgsql(connection.ConnectionString).Options);
        Assert.True(await db.Database.EnsureCreatedAsync(ct));
        await AssertReplayWindowAsync(db, ct);
    }

    [DisposableEventReplayFact(SqlServerOptIn)]
    [Trait("Category", "EventJournalReplaySqlServer")]
    public async Task SqlServer_disposable_fixture_replays_every_ordered_event_after_the_cursor()
    {
        Assert.True(OperatingSystem.IsWindows(), "The explicitly required disposable LocalDB fixture needs Windows.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = timeout.Token;
        var database = "OpenConnectReplay_" + Guid.NewGuid().ToString("N");
        var connection = new SqlConnectionStringBuilder
        {
            DataSource = @"(localdb)\OpenConnectPrivacyMigration",
            InitialCatalog = "master",
            IntegratedSecurity = true,
            TrustServerCertificate = true,
            ConnectTimeout = 15,
            Pooling = false,
        };

        await using (var master = new SqlConnection(connection.ConnectionString))
        {
            await master.OpenAsync(ct);
            await using var create = master.CreateCommand();
            // The database identifier is only a fixed prefix and freshly generated hex GUID.
            create.CommandText = $"CREATE DATABASE [{database}]";
            await create.ExecuteNonQueryAsync(ct);
        }

        connection.InitialCatalog = database;
        var options = new DbContextOptionsBuilder<OpenConnectDbContext>().UseSqlServer(connection.ConnectionString).Options;
        try
        {
            await using var db = new OpenConnectDbContext(options);
            Assert.True(await db.Database.EnsureCreatedAsync(ct));
            await AssertReplayWindowAsync(db, ct);
        }
        finally
        {
            // Only the database this test just created on the named local fixture.
            await using var cleanup = new OpenConnectDbContext(options);
            await cleanup.Database.EnsureDeletedAsync();
        }
    }

    private static void RestrictPostgresHostBinding(CreateContainerParameters parameters)
    {
        var host = parameters.HostConfig;
        if (host is null || host.PublishAllPorts ||
            host.PortBindings is not { Count: 1 } bindings ||
            !bindings.TryGetValue("5432/tcp", out var port) || port.Count != 1)
        {
            throw new InvalidOperationException("Expected a single disposable PostgreSQL port binding.");
        }

        // Preserve Testcontainers' randomly allocated port and restrict only its host interface.
        port[0].HostIP = "127.0.0.1";
    }

    private static async Task AssertReplayWindowAsync(OpenConnectDbContext db, CancellationToken ct)
    {
        var tenant = new TenantId(Ulid.NewUlid());
        var principal = new PrincipalId(Ulid.NewUlid());
        var session = new SessionId(Ulid.NewUlid());
        var otherSession = new SessionId(Ulid.NewUlid());
        var sessions = new EfSessionStore<OpenConnectDbContext>(db);
        await sessions.UpsertAsync(new SessionRecord(session, tenant, principal, 1), ct);
        await sessions.UpsertAsync(new SessionRecord(otherSession, tenant, principal, 1), ct);

        var expected = Enumerable.Range(1, EventCount)
            .Select(sequence => new EventRecord(session, sequence, "synthetic-event", $"payload-{sequence}"))
            .ToArray();
        var tenantScope = UlidGuidMapper.Scope(tenant.Value);
        db.AddRange(expected.Append(new EventRecord(otherSession, 20, "synthetic-event", "other-session"))
            .Select(record => new EventEntity
            {
                EventId = Guid.NewGuid(),
                TenantScope = tenantScope,
                SessionId = UlidGuidMapper.ToGuid(record.SessionId.Value),
                Sequence = record.Sequence,
                EventType = record.Type,
                SanitizedPayload = record.Data,
                OccurredUtc = DateTime.UnixEpoch,
            }));
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();

        var journal = new EfEventJournal<OpenConnectDbContext>(db);
        Assert.Equal(expected, await ReadAsync(journal.ReplayAsync(session, cancellationToken: ct)));
        Assert.Equal(expected.Skip(Cursor), await ReadAsync(journal.ReplayAsync(session, Cursor, ct)));
    }

    private static async Task<List<EventRecord>> ReadAsync(IAsyncEnumerable<EventRecord> replay)
    {
        var events = new List<EventRecord>();
        await foreach (var item in replay)
            events.Add(item);
        return events;
    }

    private sealed class DisposableEventReplayFactAttribute : FactAttribute
    {
        public DisposableEventReplayFactAttribute(string optIn)
        {
            if (!string.Equals(Environment.GetEnvironmentVariable(optIn), "true", StringComparison.OrdinalIgnoreCase))
                Skip = $"NOT VERIFIED: this test requires explicit opt-in ({optIn}=true) and a disposable provider fixture.";
        }
    }
}
