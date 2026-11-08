using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OpenConnect.Navigator.AspNetCore.Outbox;
using OpenConnect.Navigator.Persistence.Postgres;
using OpenConnect.Persistence.Postgres;
using OpenConnect.Persistence.Postgres.Entities;
using OpenConnect.Transport.Nats;
using Testcontainers.PostgreSql;
using Xunit;
using Xunit.Abstractions;

namespace OpenConnect.Navigator.Persistence.Tests;

[Trait("Category", "PostgresDocker")]
public sealed class PostgresOutboxRelayAdapterTests(ITestOutputHelper output)
{
    private const string PayloadTemplate = "{{\"schemaVersion\":\"openconnect.v1\",\"messageId\":\"{0:D}\",\"taskId\":\"task-1\",\"kind\":\"navigator.event\",\"issuedAt\":\"2026-01-01T00:00:00Z\",\"expiresAt\":\"2027-01-01T00:00:00Z\",\"lease\":{{\"leaseId\":\"123e4567-e89b-42d3-a456-426614174001\",\"fencingToken\":1}},\"payloadRef\":\"opaque://navigator/event\"}}";

    [Fact]
    public async Task Adapter_publishes_navigator_payloads_and_marks_each_after_ack()
    {
        await using var postgres = await StartPostgresOrSkip();
        if (postgres is null) return;
        var connectionString = postgres.GetConnectionString();
        Assert.Contains("openconnect_nav_relay_test", connectionString, StringComparison.OrdinalIgnoreCase);
        await using var db = CreateDb(connectionString);
        Assert.Equal(connectionString, db.Database.GetDbConnection().ConnectionString);
        await db.GetService<IMigrator>().MigrateAsync("20261106220532_PendingModelSync");

        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var payloads = new[] { string.Format(PayloadTemplate, firstId), string.Format(PayloadTemplate, secondId) };
        AddPending(db, firstId, payloads[0]);
        AddPending(db, secondId, payloads[1]);
        await db.SaveChangesAsync();

        var broker = new FakePublisher();
        var adapter = new NavigatorOutboxRelayAdapter(new NavigatorPostgresOutboxRelaySource(db), new NatsPublisher(broker));
        Assert.Equal(2, await adapter.RelayOnceAsync(10, Guid.NewGuid(), CancellationToken.None));
        Assert.Equal(payloads.Length, broker.Payloads.Count);
        Assert.All(payloads, expected => Assert.Contains(broker.Payloads, actual => JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual))));
        Assert.Equal(new[] { "oc.v1.navigator.event", "oc.v1.navigator.event" }, broker.Subjects);
        Assert.All(await db.Outbox.AsNoTracking().Where(x => x.Id == firstId || x.Id == secondId).ToListAsync(),
            row => Assert.Equal("delivered", row.State));
    }

    [Fact]
    public async Task Adapter_leaves_rows_pending_when_broker_ack_fails()
    {
        await using var postgres = await StartPostgresOrSkip();
        if (postgres is null) return;
        var connectionString = postgres.GetConnectionString();
        Assert.Contains("openconnect_nav_relay_test", connectionString, StringComparison.OrdinalIgnoreCase);
        await using var db = CreateDb(connectionString);
        Assert.Equal(connectionString, db.Database.GetDbConnection().ConnectionString);
        await db.GetService<IMigrator>().MigrateAsync("20261106220532_PendingModelSync");

        var id = Guid.NewGuid();
        AddPending(db, id, string.Format(PayloadTemplate, id));
        await db.SaveChangesAsync();
        var broker = new FakePublisher { Fail = true };
        var adapter = new NavigatorOutboxRelayAdapter(new NavigatorPostgresOutboxRelaySource(db), new NatsPublisher(broker));
        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.RelayOnceAsync(10, Guid.NewGuid(), CancellationToken.None));
        Assert.Equal(0, broker.Acks);
        Assert.Equal("pending", (await db.Outbox.AsNoTracking().SingleAsync(x => x.Id == id)).State);
    }

    private async Task<PostgreSqlContainer?> StartPostgresOrSkip()
    {
        var postgres = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("openconnect_nav_relay_test").WithUsername("test").WithPassword("test").Build();
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

    private static OpenConnectDbContext CreateDb(string connectionString) =>
        new(new DbContextOptionsBuilder<OpenConnectDbContext>().UseNpgsql(connectionString).Options);

    private static void AddPending(OpenConnectDbContext db, Guid id, string payload) => db.Outbox.Add(new OutboxEntity
    {
        Id = id, Topic = "navigator.event", Payload = payload, State = "pending",
        NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-1), CreatedAt = DateTimeOffset.UtcNow
    });

    private sealed class FakePublisher : IJetStreamPublisher
    {
        public bool Fail { get; init; }
        public int Acks { get; private set; }
        public List<string> Payloads { get; } = [];
        public List<string> Subjects { get; } = [];

        public ValueTask PublishAsync(string subject, ReadOnlyMemory<byte> payload, string messageId, CancellationToken cancellationToken)
        {
            if (Fail) throw new InvalidOperationException("Broker acknowledgement failed.");
            Assert.Equal(WorkerEnvelope.Parse(payload.Span).MessageId.ToString("D"), messageId);
            Subjects.Add(subject);
            Payloads.Add(Encoding.UTF8.GetString(payload.Span));
            Acks++;
            return ValueTask.CompletedTask;
        }
    }
}
