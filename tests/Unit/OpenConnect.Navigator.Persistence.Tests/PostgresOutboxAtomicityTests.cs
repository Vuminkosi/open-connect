using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OpenConnect.Navigator;
using OpenConnect.Navigator.Operational.Durable;
using OpenConnect.Navigator.Persistence.Postgres;
using OpenConnect.Persistence.Postgres;
using OpenConnect.Transport.Nats;
using Testcontainers.PostgreSql;
using Xunit;
using Xunit.Abstractions;

namespace OpenConnect.Navigator.Persistence.Tests;

[Trait("Category", "PostgresDocker")]
public sealed class PostgresOutboxAtomicityTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Append_event_commits_event_and_parseable_outbox_together_and_conflict_writes_neither()
    {
        await using var postgres = await StartPostgresOrSkip();
        if (postgres is null) return;

        var connectionString = postgres.GetConnectionString();
        Assert.Contains("openconnect_nav_atomicity_test", connectionString, StringComparison.OrdinalIgnoreCase);
        await using var db = new OpenConnectDbContext(new DbContextOptionsBuilder<OpenConnectDbContext>().UseNpgsql(connectionString).Options);
        Assert.Equal(connectionString, db.Database.GetDbConnection().ConnectionString);
        await db.GetService<IMigrator>().MigrateAsync("20261107193633_NavigatorDurability");

        var store = new PostgresDurableNavigatorStore(db);
        var tenant = Guid.NewGuid().ToString("D");
        var principal = Guid.NewGuid().ToString("D");
        var session = await store.CreateSessionAsync(tenant, principal, "client", "route");
        var eventId = Guid.NewGuid().ToString("D");
        var value = new NavigatorEvent(eventId, "navigator.session.updated", session, null, 0,
            "atomicity-test", DateTimeOffset.UtcNow, new Dictionary<string, string> { ["state"] = "updated" });

        Assert.Equal(NavigatorStoreOutcome.Succeeded,
            await store.AppendEventAsync(session, tenant, principal, 0, value));

        var persistedEvent = await db.Events.AsNoTracking().SingleAsync(x => x.Id == Guid.Parse(eventId));
        var outbox = await db.Outbox.AsNoTracking().SingleAsync(x => x.Id == Guid.Parse(eventId));
        var envelope = WorkerEnvelope.Parse(System.Text.Encoding.UTF8.GetBytes(outbox.Payload));
        Assert.Equal(Guid.Parse(eventId), envelope.MessageId);
        Assert.Equal(session, envelope.TaskId);
        Assert.Equal("navigator.navigator.session.updated", outbox.Topic);
        Assert.Equal(outbox.Topic, envelope.Kind);
        Assert.Equal($"navigator/{session}/{eventId}", envelope.PayloadRef);
        Assert.Equal("atomicity-test", envelope.CorrelationId);
        Assert.Equal("pending", outbox.State);
        Assert.Equal(0, persistedEvent.Sequence);

        var eventCount = await db.Events.CountAsync();
        var outboxCount = await db.Outbox.CountAsync();
        var conflicting = value with { EventId = Guid.NewGuid().ToString("D"), Sequence = 2 };
        Assert.Equal(NavigatorStoreOutcome.Conflict,
            await store.AppendEventAsync(session, tenant, principal, 2, conflicting));
        Assert.Equal(eventCount, await db.Events.CountAsync());
        Assert.Equal(outboxCount, await db.Outbox.CountAsync());
    }

    [Fact]
    public async Task Append_event_outbox_duplicate_rolls_back_event_and_clears_tracker()
    {
        await using var postgres = await StartPostgresOrSkip();
        if (postgres is null) return;

        var connectionString = postgres.GetConnectionString();
        Assert.Contains("openconnect_nav_atomicity_test", connectionString, StringComparison.OrdinalIgnoreCase);
        var options = new DbContextOptionsBuilder<OpenConnectDbContext>().UseNpgsql(connectionString).Options;
        await using var db = new OpenConnectDbContext(options);
        Assert.Equal(connectionString, db.Database.GetDbConnection().ConnectionString);
        await db.GetService<IMigrator>().MigrateAsync("20261107193633_NavigatorDurability");

        var store = new PostgresDurableNavigatorStore(db);
        var tenant = Guid.NewGuid().ToString("D");
        var principal = Guid.NewGuid().ToString("D");
        var session = await store.CreateSessionAsync(tenant, principal, "client", "route");
        var firstId = Guid.NewGuid().ToString("D");
        var first = new NavigatorEvent(firstId, "navigator.session.updated", session, null, 0,
            "atomicity-test", DateTimeOffset.UtcNow, new Dictionary<string, string> { ["state"] = "updated" });
        Assert.Equal(NavigatorStoreOutcome.Succeeded,
            await store.AppendEventAsync(session, tenant, principal, 0, first));

        var duplicateId = Guid.NewGuid();
        var conflicting = first with { EventId = duplicateId.ToString("D"), Sequence = 1 };
        var topic = $"navigator.{conflicting.Type}";
        var issuedAt = DateTimeOffset.UtcNow;
        var validEnvelope = System.Text.Json.JsonSerializer.Serialize(new
        {
            schemaVersion = "openconnect.v1",
            messageId = duplicateId,
            taskId = session,
            kind = topic,
            issuedAt,
            expiresAt = issuedAt.AddHours(1),
            lease = new { leaseId = Guid.NewGuid(), fencingToken = 1L },
            payloadRef = $"navigator/{session}/{conflicting.EventId}",
            correlationId = conflicting.CorrelationId
        }, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        await using (var seedDb = new OpenConnectDbContext(options))
        {
            seedDb.Outbox.Add(new OpenConnect.Persistence.Postgres.Entities.OutboxEntity
            {
                Id = duplicateId, Topic = topic, Payload = validEnvelope, State = "pending",
                NextAttemptAt = issuedAt, CreatedAt = issuedAt
            });
            await seedDb.SaveChangesAsync();
        }

        Assert.Equal(NavigatorStoreOutcome.Conflict,
            await store.AppendEventAsync(session, tenant, principal, 1, conflicting));
        Assert.Empty(db.ChangeTracker.Entries());
        Assert.False(await db.Events.AsNoTracking().AnyAsync(x => x.SessionId == Guid.Parse(session) && x.Sequence == 1));
        var outboxRows = await db.Outbox.AsNoTracking().Where(x => x.Id == duplicateId).ToListAsync();
        Assert.Single(outboxRows);
        var persistedEnvelope = WorkerEnvelope.Parse(System.Text.Encoding.UTF8.GetBytes(outboxRows[0].Payload));
        Assert.Equal(duplicateId, persistedEnvelope.MessageId);
        Assert.Equal(session, persistedEnvelope.TaskId);
        Assert.Equal(topic, persistedEnvelope.Kind);
        Assert.Equal("pending", outboxRows[0].State);

        var succeeding = conflicting with { EventId = Guid.NewGuid().ToString("D") };
        Assert.Equal(NavigatorStoreOutcome.Succeeded,
            await store.AppendEventAsync(session, tenant, principal, 1, succeeding));
        Assert.True(await db.Events.AsNoTracking().AnyAsync(x => x.SessionId == Guid.Parse(session) && x.Sequence == 1));
    }

    private async Task<PostgreSqlContainer?> StartPostgresOrSkip()
    {
        var postgres = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("openconnect_nav_atomicity_test").WithUsername("test").WithPassword("test").Build();
        Exception? startupError = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try { await postgres.StartAsync(); return postgres; }
            catch (Exception exception) { startupError = exception; }
        }

        await postgres.DisposeAsync();
        output.WriteLine($"SKIP PostgresDocker: Docker/Testcontainers could not start postgres:16-alpine after 2 attempts: {startupError?.Message}");
        return null;
    }
}
