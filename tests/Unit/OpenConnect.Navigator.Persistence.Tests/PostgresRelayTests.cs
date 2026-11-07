using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OpenConnect.Navigator.Persistence.Postgres;
using OpenConnect.Persistence.Postgres;
using OpenConnect.Persistence.Postgres.Entities;
using OpenConnect.Transport.Nats;
using Testcontainers.PostgreSql;
using Xunit;
using Xunit.Abstractions;

namespace OpenConnect.Navigator.Persistence.Tests;

[Trait("Category", "PostgresDocker")]
public sealed class PostgresRelayTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Relay_marks_only_acknowledged_navigator_messages_delivered()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("openconnect_relay_test").WithUsername("test").WithPassword("test").Build();
        try { await postgres.StartAsync(); }
        catch (Exception e)
        {
            output.WriteLine($"SKIP PostgresDocker: Docker/Testcontainers could not start postgres:16-alpine: {e.Message}");
            return;
        }

        await using var db = new OpenConnectDbContext(new DbContextOptionsBuilder<OpenConnectDbContext>()
            .UseNpgsql(postgres.GetConnectionString()).Options);
        Assert.Contains("openconnect_relay_test", db.Database.GetConnectionString());
        await db.GetService<IMigrator>().MigrateAsync("20261106220532_PendingModelSync");

        var id = Guid.NewGuid();
        var payload = $$"""{"schemaVersion":"openconnect.v1","messageId":"{{id:D}}","taskId":"task-1","kind":"classification","issuedAt":"2026-01-01T00:00:00Z","expiresAt":"2027-01-01T00:00:00Z","lease":{"leaseId":"123e4567-e89b-42d3-a456-426614174001","fencingToken":1},"payloadRef":"opaque://payload/abc"}""";
        db.Outbox.Add(new OutboxEntity { Id = id, Topic = "navigator.event", Payload = payload,
            State = "pending", NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-1), CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        var publisher = new FakePublisher();
        var relay = new OutboxRelay(new NavigatorPostgresOutboxRelaySource(db), new NatsPublisher(publisher));
        Assert.Equal(1, await relay.RelayOnceAsync("navigator.events", 10, Guid.NewGuid()));
        Assert.Equal(id.ToString("D"), publisher.MessageId);
        Assert.Equal(1, publisher.Acks);
        Assert.Equal(id, WorkerEnvelope.Parse(Encoding.UTF8.GetBytes(publisher.Payload!)).MessageId);
        Assert.Equal("delivered", (await db.Outbox.AsNoTracking().SingleAsync(x => x.Id == id)).State);

        var retryId = Guid.NewGuid();
        db.Outbox.Add(new OutboxEntity { Id = retryId, Topic = "navigator.event", Payload = payload.Replace(id.ToString("D"), retryId.ToString("D")),
            State = "pending", NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-1), CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        var failing = new FakePublisher { Fail = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new OutboxRelay(new NavigatorPostgresOutboxRelaySource(db), new NatsPublisher(failing))
            .RelayOnceAsync("navigator.events", 10, Guid.NewGuid()));
        Assert.Equal("pending", (await db.Outbox.AsNoTracking().SingleAsync(x => x.Id == retryId)).State);
    }

    private sealed class FakePublisher : IJetStreamPublisher
    {
        public bool Fail { get; init; }
        public int Acks { get; private set; }
        public string? MessageId { get; private set; }
        public string? Payload { get; private set; }
        public ValueTask PublishAsync(string subject, ReadOnlyMemory<byte> payload, string messageId, CancellationToken cancellationToken)
        {
            if (Fail) throw new InvalidOperationException("Broker acknowledgement failed.");
            MessageId = messageId;
            Payload = Encoding.UTF8.GetString(payload.Span);
            Acks++;
            return ValueTask.CompletedTask;
        }
    }
}
