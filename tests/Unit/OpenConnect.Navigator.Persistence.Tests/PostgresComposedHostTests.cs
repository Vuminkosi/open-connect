using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Navigator;
using OpenConnect.Navigator.AspNetCore.Outbox;
using OpenConnect.Navigator.Operational.Durable;
using OpenConnect.Navigator.Persistence.Postgres;
using OpenConnect.Persistence.Postgres;
using OpenConnect.Transport.Nats;
using Testcontainers.PostgreSql;
using Xunit;
using Xunit.Abstractions;

namespace OpenConnect.Navigator.Persistence.Tests;

[Trait("Category", "PostgresDocker")]
public sealed class PostgresComposedHostTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Composed_host_appends_and_relays_event_through_postgres_outbox()
    {
        await using var postgres = await StartPostgresOrSkip();
        if (postgres is null) return;

        var connectionString = postgres.GetConnectionString();
        Assert.Contains("openconnect_nav_composed_test", connectionString, StringComparison.OrdinalIgnoreCase);

        var services = new ServiceCollection();
        services.AddDbContext<OpenConnectDbContext>(options => options.UseNpgsql(connectionString));
        services.AddNavigatorPostgresStore();
        services.AddNavigatorOutboxRelay();

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var scoped = scope.ServiceProvider;
        var db = scoped.GetRequiredService<OpenConnectDbContext>();
        Assert.Equal(connectionString, db.Database.GetDbConnection().ConnectionString);
        await db.GetService<IMigrator>().MigrateAsync("20261107193633_NavigatorDurability");

        Assert.IsType<PostgresDurableNavigatorStore>(scoped.GetRequiredService<IDurableNavigatorStore>());
        var store = scoped.GetRequiredService<IDurableNavigatorStore>();
        var tenant = Guid.NewGuid().ToString("D");
        var principal = Guid.NewGuid().ToString("D");
        var session = await store.CreateSessionAsync(tenant, principal, "client", "route");
        var eventId = Guid.NewGuid().ToString("D");
        var value = new NavigatorEvent(eventId, "navigator.session.updated", session, null, 0,
            "composed-host-test", DateTimeOffset.UtcNow, new Dictionary<string, string> { ["state"] = "updated" });

        Assert.Equal(NavigatorStoreOutcome.Succeeded,
            await store.AppendEventAsync(session, tenant, principal, 0, value));
        var relaySource = scoped.GetRequiredService<IOutboxRelaySource>();
        var pending = await relaySource.GetPendingAsync(10, default);
        var entry = Assert.Single(pending);
        Assert.Equal(Guid.Parse(eventId), entry.Id);
        var persistedEnvelope = WorkerEnvelope.Parse(Encoding.UTF8.GetBytes(entry.Payload));
        Assert.Equal(entry.Id, persistedEnvelope.MessageId);

        var broker = new RecordingPublisher();
        var relay = new OutboxRelay(relaySource, new NatsPublisher(broker));
        Assert.Equal(1, await relay.RelayOnceAsync("navigator.events", 10, Guid.NewGuid()));

        var publication = Assert.Single(broker.Publications);
        Assert.Equal("navigator.events", publication.Subject);
        Assert.Equal(entry.Id.ToString("D"), publication.MessageId);
        Assert.Equal(entry.Payload, Encoding.UTF8.GetString(publication.Payload));
        Assert.Equal(entry.Id, WorkerEnvelope.Parse(publication.Payload).MessageId);
        Assert.Empty(await relaySource.GetPendingAsync(10, default));
        Assert.Equal(0, await relay.RelayOnceAsync("navigator.events", 10, Guid.NewGuid()));
    }

    private async Task<PostgreSqlContainer?> StartPostgresOrSkip()
    {
        var postgres = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("openconnect_nav_composed_test").WithUsername("test").WithPassword("test").Build();
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

    private sealed class RecordingPublisher : IJetStreamPublisher
    {
        public List<Publication> Publications { get; } = [];

        public ValueTask PublishAsync(string subject, ReadOnlyMemory<byte> payload, string messageId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Publications.Add(new Publication(subject, payload.ToArray(), messageId));
            return ValueTask.CompletedTask;
        }
    }

    private sealed record Publication(string Subject, byte[] Payload, string MessageId);
}
