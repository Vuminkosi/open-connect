using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NATS.Client;
using OpenConnect.Navigator;
using OpenConnect.Navigator.Operational.Durable;
using OpenConnect.Navigator.Persistence.Postgres;
using OpenConnect.Persistence.Postgres;
using OpenConnect.Persistence.Postgres.Entities;
using OpenConnect.Transport.Nats;
using DotNet.Testcontainers;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Testcontainers.PostgreSql;
using Xunit;
using Xunit.Abstractions;

namespace OpenConnect.Navigator.Persistence.Tests;

[Trait("Category", "PostgresDocker")]
public sealed class PostgresRelayLiveBrokerTests(ITestOutputHelper output)
{
    private const string PayloadTemplate = "{{\"schemaVersion\":\"openconnect.v1\",\"messageId\":\"{0:D}\",\"taskId\":\"task-live\",\"kind\":\"navigator.event\",\"issuedAt\":\"2026-01-01T00:00:00Z\",\"expiresAt\":\"2027-01-01T00:00:00Z\",\"lease\":{{\"leaseId\":\"123e4567-e89b-42d3-a456-426614174001\",\"fencingToken\":1}},\"payloadRef\":\"opaque://navigator/live\"}}";

    [Fact]
    public async Task Durable_navigator_commit_is_relayed_to_live_subscriber_once()
    {
        await using var postgres = await StartPostgresOrSkip();
        if (postgres is null) return;
        await using var nats = await StartNatsOrSkip();
        if (nats is null) return;

        var connectionString = postgres.GetConnectionString();
        Assert.Contains("openconnect_nav_relay_test", connectionString, StringComparison.OrdinalIgnoreCase);
        await using var db = new OpenConnectDbContext(new DbContextOptionsBuilder<OpenConnectDbContext>().UseNpgsql(connectionString).Options);
        Assert.Equal(connectionString, db.Database.GetDbConnection().ConnectionString);
        await db.GetService<IMigrator>().MigrateAsync("20261107193633_NavigatorDurability");

        var store = new PostgresDurableNavigatorStore(db);
        var tenant = Guid.NewGuid().ToString("D");
        var principal = Guid.NewGuid().ToString("D");
        var sessionId = await store.CreateSessionAsync(tenant, principal, "client-live", "route-live");
        var eventId = Guid.NewGuid().ToString("D");
        var eventValue = new NavigatorEvent(eventId, "navigator.session.updated", sessionId, null, 0,
            "live-relay-test", DateTimeOffset.UtcNow, new Dictionary<string, string> { ["state"] = "private-event-content" });
        Assert.Equal(NavigatorStoreOutcome.Succeeded,
            await store.AppendEventAsync(sessionId, tenant, principal, 0, eventValue));

        var entry = await db.Outbox.AsNoTracking().SingleAsync(x => x.Id == Guid.Parse(eventId));
        Assert.Equal("pending", entry.State);

        var options = ConnectionFactory.GetDefaultOptions();
        options.Url = $"nats://{nats.Hostname}:{nats.GetMappedPublicPort(4222)}";
        using var connection = new ConnectionFactory().CreateConnection(options);
        using var subscription = connection.SubscribeSync("oc.v1.navigator.event");
        subscription.AutoUnsubscribe(1);

        var publisher = new NatsPublisher(new CoreNatsPublisher(connection));
        var relay = new OutboxRelay(new NavigatorPostgresOutboxRelaySource(db), publisher);
        Assert.Equal(1, await relay.RelayOnceAsync("oc.v1.navigator.event", 10, Guid.NewGuid(), CancellationToken.None));

        var message = subscription.NextMessage(5000);
        var envelope = WorkerEnvelope.Parse(message.Data);
        Assert.Equal(entry.Id, envelope.MessageId);
        Assert.StartsWith("navigator.", envelope.Kind, StringComparison.Ordinal);
        Assert.Equal($"navigator/{sessionId}/{eventId}", envelope.PayloadRef);
        Assert.DoesNotContain("private-event-content", Encoding.UTF8.GetString(message.Data), StringComparison.Ordinal);
        Assert.Empty(await new NavigatorPostgresOutboxRelaySource(db).GetPendingAsync(10, CancellationToken.None));
        Assert.Equal(0, await relay.RelayOnceAsync("oc.v1.navigator.event", 10, Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task Relay_commits_to_live_nats_and_marks_delivered_after_publish_ack()
    {
        await using var postgres = await StartPostgresOrSkip();
        if (postgres is null) return;
        await using var nats = await StartNatsOrSkip();
        if (nats is null) return;

        var connectionString = postgres.GetConnectionString();
        Assert.Contains("openconnect_nav_relay_test", connectionString, StringComparison.OrdinalIgnoreCase);
        await using var db = new OpenConnectDbContext(new DbContextOptionsBuilder<OpenConnectDbContext>().UseNpgsql(connectionString).Options);
        Assert.Equal(connectionString, db.Database.GetDbConnection().ConnectionString);
        await db.GetService<IMigrator>().MigrateAsync("20261106220532_PendingModelSync");

        var id = Guid.NewGuid();
        var payload = string.Format(PayloadTemplate, id);
        db.Outbox.Add(new OutboxEntity
        {
            Id = id, Topic = "navigator.event", Payload = payload, State = "pending",
            NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-1), CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var options = ConnectionFactory.GetDefaultOptions();
        options.Url = $"nats://{nats.Hostname}:{nats.GetMappedPublicPort(4222)}";
        using var connection = new ConnectionFactory().CreateConnection(options);
        using var subscription = connection.SubscribeSync("oc.v1.navigator.event");
        subscription.AutoUnsubscribe(1);

        var broker = new CoreNatsPublisher(connection);
        var relay = new NavigatorOutboxRelayAdapter(new NavigatorPostgresOutboxRelaySource(db), new NatsPublisher(broker));
        Assert.Equal(1, await relay.RelayOnceAsync(10, Guid.NewGuid(), CancellationToken.None));

        var message = subscription.NextMessage(5000);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(payload), JsonNode.Parse(Encoding.UTF8.GetString(message.Data))));
        Assert.Equal(id, WorkerEnvelope.Parse(message.Data).MessageId);
        Assert.Empty(await new NavigatorPostgresOutboxRelaySource(db).GetPendingAsync(10, CancellationToken.None));
        Assert.Equal(0, await relay.RelayOnceAsync(10, Guid.NewGuid(), CancellationToken.None));
    }

    private async Task<PostgreSqlContainer?> StartPostgresOrSkip()
    {
        var container = new PostgreSqlBuilder("postgres:16-alpine").WithDatabase("openconnect_nav_relay_test").WithUsername("test").WithPassword("test").Build();
        Exception? error = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try { await container.StartAsync(); return container; }
            catch (Exception ex) { error = ex; }
        }
        await container.DisposeAsync();
        output.WriteLine($"SKIP PostgresDocker: Docker/Testcontainers could not start postgres:16-alpine after 2 attempts: {error?.Message}");
        return null;
    }

    private async Task<IContainer?> StartNatsOrSkip()
    {
        var container = new ContainerBuilder("nats:2-alpine").WithPortBinding(4222, true).Build();
        Exception? error = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try { await container.StartAsync(); return container; }
            catch (Exception ex) { error = ex; }
        }
        await container.DisposeAsync();
        output.WriteLine($"SKIP NatsDocker: Docker/Testcontainers could not start nats:2-alpine after 2 attempts: {error?.Message}");
        return null;
    }

    // Core NATS is sufficient to prove subscriber delivery; this does not prove JetStream persistence/stream provisioning.
    private sealed class CoreNatsPublisher(IConnection connection) : IJetStreamPublisher
    {
        public ValueTask PublishAsync(string subject, ReadOnlyMemory<byte> payload, string messageId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(WorkerEnvelope.Parse(payload.Span).MessageId.ToString("D"), messageId);
            connection.Publish(subject, payload.ToArray());
            connection.Flush(); // Flush waits for the server PONG, confirming the publish was processed.
            return ValueTask.CompletedTask;
        }
    }
}
