using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenConnect.Navigator;
using OpenConnect.Navigator.Operational;
using OpenConnect.Navigator.Operational.Durable;
using OpenConnect.Navigator.Persistence.Postgres;
using OpenConnect.Persistence.Postgres;
using Testcontainers.PostgreSql;
using Xunit;
using Xunit.Abstractions;

namespace OpenConnect.Navigator.Persistence.Tests;

[Trait("Category", "PostgresDocker")]
public sealed class PostgresIsolationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task PostgreSql_migration_quarantines_duplicate_sequence_orphans_without_losing_originals()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("openconnect_quarantine_test")
            .WithUsername("test")
            .WithPassword("test")
            .Build();
        try { await postgres.StartAsync(); }
        catch (Exception e)
        {
            output.WriteLine($"SKIP PostgresDocker: Docker/Testcontainers could not start postgres:16-alpine: {e.Message}");
            return;
        }

        await using var db = new OpenConnectDbContext(new DbContextOptionsBuilder<OpenConnectDbContext>()
            .UseNpgsql(postgres.GetConnectionString()).Options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261106220532_PendingModelSync");

        var sentinelEvent = Guid.NewGuid();
        var firstSession = Guid.NewGuid();
        var secondSession = Guid.NewGuid();
        var scalarSession = Guid.NewGuid();
        var arraySession = Guid.NewGuid();
        var sentinelSession = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var firstEvent = Guid.NewGuid();
        var secondEvent = Guid.NewGuid();
        var scalarEvent = Guid.NewGuid();
        var arrayEvent = Guid.NewGuid();
        var sentinelObjectEvent = Guid.NewGuid();
        var sentinelScalarEvent = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO oc_events (id, session_id, sequence, event_type, payload, occurred_at, correlation_id)
            VALUES ({{sentinelEvent}}, {{sentinelSession}}, 2000000000, 'legacy', '{"existing":true}'::jsonb, CURRENT_TIMESTAMP, NULL),
                   ({{firstEvent}}, {{firstSession}}, 7, 'legacy', '{"kept":"value"}'::jsonb, CURRENT_TIMESTAMP, NULL),
                   ({{secondEvent}}, {{secondSession}}, 7, 'legacy', '42'::jsonb, CURRENT_TIMESTAMP, NULL),
                   ({{scalarEvent}}, {{scalarSession}}, 9, 'legacy', '"scalar"'::jsonb, CURRENT_TIMESTAMP, NULL),
                   ({{arrayEvent}}, {{arraySession}}, 9, 'legacy', '[1,"two",null]'::jsonb, CURRENT_TIMESTAMP, NULL),
                   ({{sentinelObjectEvent}}, {{sentinelSession}}, 7, 'legacy', '{"sentinel":"object"}'::jsonb, CURRENT_TIMESTAMP, NULL),
                   ({{sentinelScalarEvent}}, {{sentinelSession}}, 8, 'legacy', '42'::jsonb, CURRENT_TIMESTAMP, NULL)
            """);

        await db.Database.MigrateAsync();

        var quarantined = await db.Database.SqlQuery<QuarantinedEvent>($$"""
            SELECT id AS "Id", session_id AS "SessionId", sequence AS "Sequence", payload AS "Payload"
            FROM oc_events WHERE id IN ({{firstEvent}}::uuid, {{secondEvent}}::uuid, {{scalarEvent}}::uuid, {{arrayEvent}}::uuid,
                {{sentinelObjectEvent}}::uuid, {{sentinelScalarEvent}}::uuid) ORDER BY id
            """).ToListAsync();
        Assert.Equal(6, quarantined.Count);
        Assert.All(quarantined, item => Assert.Equal(sentinelSession, item.SessionId));
        Assert.All(quarantined, item => Assert.True(item.Sequence > 2000000000));
        Assert.Equal(6, quarantined.Select(item => item.Sequence).Distinct().Count());
        Assert.All(quarantined, item =>
        {
            using var payload = JsonDocument.Parse(item.Payload);
            var originalSession = Guid.Parse(payload.RootElement.GetProperty("_navigator_quarantine_original_session_id").GetString()!);
            Assert.Contains(originalSession, new[] { firstSession, secondSession, scalarSession, arraySession, sentinelSession });
            Assert.Equal(originalSession == firstSession || originalSession == secondSession ||
                    (originalSession == sentinelSession && item.Id == sentinelObjectEvent) ? 7 :
                    originalSession == sentinelSession ? 8 : 9,
                payload.RootElement.GetProperty("_navigator_quarantine_original_sequence").GetInt64());
            if (originalSession == firstSession)
                Assert.Equal("value", payload.RootElement.GetProperty("kept").GetString());
            if (originalSession == secondSession)
                Assert.Equal("42", payload.RootElement.GetProperty("_navigator_quarantine_original_payload").GetRawText());
            if (originalSession == scalarSession)
                Assert.Equal("\"scalar\"", payload.RootElement.GetProperty("_navigator_quarantine_original_payload").GetRawText());
            if (originalSession == arraySession)
            {
                Assert.True(JsonNode.DeepEquals(JsonNode.Parse("[1,\"two\",null]"),
                    JsonNode.Parse(payload.RootElement.GetProperty("_navigator_quarantine_original_payload").GetRawText())));
            }
            if (originalSession == sentinelSession && item.Id == sentinelObjectEvent)
                Assert.Equal("object", payload.RootElement.GetProperty("sentinel").GetString());
            if (originalSession == sentinelSession && item.Id == sentinelScalarEvent)
                Assert.Equal("42", payload.RootElement.GetProperty("_navigator_quarantine_original_payload").GetRawText());
        });
    }

    private sealed class QuarantinedEvent
    {
        public Guid Id { get; set; }
        public Guid SessionId { get; set; }
        public long Sequence { get; set; }
        public string Payload { get; set; } = string.Empty;
    }

    [Fact]
    public async Task PostgreSql_migrations_and_durable_operations_round_trip()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("openconnect_test")
            .WithUsername("test")
            .WithPassword("test")
            .Build();

        Exception? startupError = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try { await postgres.StartAsync(); startupError = null; break; }
            catch (Exception e) { startupError = e; if (attempt == 2) break; }
        }
        if (startupError is not null)
        {
            output.WriteLine($"SKIP PostgresDocker: Docker/Testcontainers could not start postgres:16-alpine after 2 attempts: {startupError.Message}");
            return;
        }

        var connectionString = postgres.GetConnectionString();
        Assert.Contains("openconnect_test", connectionString, StringComparison.OrdinalIgnoreCase);
        await using var db = new OpenConnectDbContext(new DbContextOptionsBuilder<OpenConnectDbContext>().UseNpgsql(connectionString).Options);
        Assert.Equal(connectionString, db.Database.GetDbConnection().ConnectionString);
        await db.Database.MigrateAsync();

        var store = new PostgresDurableNavigatorStore(db);
        var tenant = Guid.NewGuid().ToString("D");
        var principal = Guid.NewGuid().ToString("D");
        var sessionId = await store.CreateSessionAsync(tenant, principal, "client", "route");
        Assert.NotNull(await store.GetSessionAsync(sessionId, tenant, principal));
        Assert.Null(await store.GetSessionAsync(sessionId, Guid.NewGuid().ToString("D"), principal));

        var eventValue = new NavigatorEvent(Guid.NewGuid().ToString("D"), "navigator.session.updated", sessionId, null, 0, null, DateTimeOffset.UtcNow, new Dictionary<string, string> { ["state"] = "Pairing" });
        Assert.Equal(NavigatorStoreOutcome.Succeeded, await store.AppendEventAsync(sessionId, tenant, principal, 0, eventValue));
        Assert.Equal("Pairing", Assert.Single(await store.GetEventsAsync(sessionId, tenant, principal, -1)).Metadata!["state"]);

        var command = "command-" + Guid.NewGuid().ToString("N");
        var key = "key-" + Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(NavigatorStoreOutcome.Succeeded, await store.CreatePendingApprovalAsync(sessionId, tenant, principal, new NavigatorApprovalRecord(sessionId, command, "digest", now.AddMinutes(10))));
        var receipt = new NavigatorReceiptRecord(command, key, "digest", "result", now.AddMinutes(10), 1);
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            await using var peerDb = new OpenConnectDbContext(new DbContextOptionsBuilder<OpenConnectDbContext>().UseNpgsql(connectionString).Options);
            return await new PostgresDurableNavigatorStore(peerDb).ConsumeApprovalAndSaveReceiptAsync(sessionId, tenant, principal, command, "digest", now, receipt);
        }));
        Assert.Single(outcomes, x => x.Outcome == NavigatorStoreOutcome.Succeeded && !x.IsReplay);
        Assert.Single(outcomes, x => x.Outcome == NavigatorStoreOutcome.Succeeded && x.IsReplay);
        var storedReceipt = await store.GetReceiptAsync(tenant, principal, key, now);
        Assert.NotNull(storedReceipt);
        Assert.Equal(receipt.CommandId, storedReceipt.CommandId);
        Assert.Equal(receipt.IdempotencyKey, storedReceipt.IdempotencyKey);
        Assert.Equal(receipt.Digest, storedReceipt.Digest);
        Assert.Equal(receipt.ExpiresAt.Ticks - receipt.ExpiresAt.Ticks % 10, storedReceipt.ExpiresAt.Ticks);
        Assert.Equal(receipt.FencingToken, storedReceipt.FencingToken);
        Assert.Equal(JsonSerializer.Serialize(receipt.Result), JsonSerializer.Serialize(storedReceipt.Result));

        var nullResultKey = key + "-null";
        var nullCommand = command + "-null";
        var nullReceipt = receipt with { CommandId = nullCommand, IdempotencyKey = nullResultKey, Result = null };
        Assert.Equal(NavigatorStoreOutcome.Succeeded, await store.CreatePendingApprovalAsync(sessionId, tenant, principal, new NavigatorApprovalRecord(sessionId, nullCommand, "digest", now.AddMinutes(10))));
        Assert.Equal(NavigatorStoreOutcome.Succeeded, (await store.ConsumeApprovalAndSaveReceiptAsync(sessionId, tenant, principal, nullCommand, "digest", now, nullReceipt)).Outcome);
        var storedNullReceipt = await store.GetReceiptAsync(tenant, principal, nullResultKey, now);
        Assert.NotNull(storedNullReceipt);
        Assert.Null(storedNullReceipt.Result);

        var executionId = tenant + ":execution-" + Guid.NewGuid().ToString("N");
        var first = await store.TryAcquireLeaseAsync(executionId, "worker", TimeSpan.FromMinutes(1), now, default);
        Assert.NotNull(first);
        Assert.Equal(1, first.FencingToken);
        await store.ReleaseLeaseAsync(first, default);
        var second = await store.TryAcquireLeaseAsync(executionId, "worker", TimeSpan.FromMinutes(1), now, default);
        Assert.NotNull(second);
        Assert.True(second.FencingToken > first.FencingToken);
    }
}
