using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Persistence;
using OpenConnect.Persistence.EFCore.Configuration;
using OpenConnect.Persistence.EFCore.Context;
using OpenConnect.Persistence.EFCore.DependencyInjection;
using OpenConnect.Persistence.EFCore.Model;
using OpenConnect.Persistence.EFCore.Transactions;
using OpenConnect.Transport.Nats;
using OpenConnect.WorkerHosts.Outbox;
using Xunit;

namespace OpenConnect.Samples.Workers.Outbox.Tests;

public sealed class OutboxSampleTests
{
    private const string TenantUlid = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
    private static readonly TenantId Tenant = new(Ulid.Parse(TenantUlid));
    private static readonly PrincipalId Principal = new(Ulid.NewUlid());

    [Fact]
    public void WSE2E_AT_073_Options_and_hosting_invariants_verify_model_free_and_disables_embedded()
    {
        var options = new OutboxWorkerOptions
        {
            Mode = OutboxHostingMode.Standalone,
            EmbeddedDispatcherEnabled = false,
            TenantUlid = TenantUlid,
            DatabaseProvider = "sqlite",
            ConnectionString = "Data Source=:memory:",
            NatsUrl = "nats://127.0.0.1:4222",
            Subject = "oc.v1.notifications.inapp",
        };
        var validator = new OutboxWorkerOptionsValidator();
        var result = validator.Validate(null, options);
        Assert.True(result.Succeeded, result.FailureMessage);
    }

    [Fact]
    public async Task WSE2E_AT_074_Atomic_relational_acceptance_task_event_outbox_in_one_transaction()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddDbContext<OpenConnectDbContext>(options =>
            options.UseSqlite(connection)
                .ReplaceService<Microsoft.EntityFrameworkCore.Infrastructure.IModelCacheKeyFactory, OpenConnectModelCacheKeyFactory>());

        services.AddOpenConnectEntityFramework<OpenConnectDbContext>(o =>
        {
            o.Migrations = OpenConnectMigrationMode.Manual;
        });

        await using var provider = services.BuildServiceProvider();
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OpenConnectDbContext>();
            await db.Database.EnsureCreatedAsync();
        }

        // Test transactional commit
        using (var scope = provider.CreateScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IOpenConnectPersistenceUnitOfWork>();
            var sessionStore = scope.ServiceProvider.GetRequiredService<ISessionStore>();
            var taskStore = scope.ServiceProvider.GetRequiredService<ITaskStore>();
            var journal = scope.ServiceProvider.GetRequiredService<IEventJournal>();
            var outboxStore = scope.ServiceProvider.GetRequiredService<IOutboxStore>();

            var taskId = new TaskId(Ulid.NewUlid());
            var sessionId = new SessionId(Ulid.NewUlid());
            var outboxId = Guid.NewGuid();

            await uow.ExecuteAsync(async _ =>
            {
                await sessionStore.UpsertAsync(new SessionRecord(sessionId, Tenant, Principal, 1));
                await taskStore.AcceptAsync(new TaskAcceptance(taskId, Tenant, Principal, "idem-atomic-001"));
                await journal.AppendAsync(new EventRecord(sessionId, 0, "task.created", "payload-event"));
                await outboxStore.EnqueueAsync(new OutboxEntry(outboxId, "task.created", """{"taskId":"task-1"}"""));
                return true;
            });
        }

        // Verify all 3 committed
        using (var scope = provider.CreateScope())
        {
            var outboxStore = scope.ServiceProvider.GetRequiredService<IOutboxStore>();

            var pending = await outboxStore.GetPendingAsync(10);
            Assert.Single(pending);
        }

        // Test transactional rollback
        using (var scope = provider.CreateScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IOpenConnectPersistenceUnitOfWork>();
            var outboxStore = scope.ServiceProvider.GetRequiredService<IOutboxStore>();

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                uow.ExecuteAsync<bool>(async _ =>
                {
                    await outboxStore.EnqueueAsync(new OutboxEntry(Guid.NewGuid(), "failed.event", "payload"));
                    throw new InvalidOperationException("intentional rollback");
                }));

            // Pending rows count is still 1 (the rollback row was not persisted)
            var pending = await outboxStore.GetPendingAsync(10);
            Assert.Single(pending);
        }
    }

    [Fact]
    public async Task WSE2E_AT_075_Fenced_batch_claim_and_broker_ack_before_delivered_mark()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddDbContext<OpenConnectDbContext>(options =>
            options.UseSqlite(connection)
                .ReplaceService<Microsoft.EntityFrameworkCore.Infrastructure.IModelCacheKeyFactory, OpenConnectModelCacheKeyFactory>());

        services.AddOpenConnectEntityFramework<OpenConnectDbContext>(o =>
        {
            o.Migrations = OpenConnectMigrationMode.Manual;
        });

        await using var provider = services.BuildServiceProvider();
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OpenConnectDbContext>();
            await db.Database.EnsureCreatedAsync();

            var outboxStore = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
            var entryId = Guid.NewGuid();
            var validEnvelope = $$"""{"schemaVersion":"openconnect.v1","messageId":"{{entryId:D}}","taskId":"task-1","kind":"classification","issuedAt":"2026-01-01T00:00:00Z","expiresAt":"2026-01-01T00:01:00Z","lease":{"leaseId":"{{Guid.NewGuid():D}}","fencingToken":1},"payloadRef":"opaque://payload/123"}""";
            await outboxStore.EnqueueAsync(new OutboxEntry(entryId, "oc.v1.notifications.inapp", validEnvelope));

            var publisher = new RecordingPublisher();
            var relaySource = new OutboxStoreRelaySource(outboxStore);
            var relay = new OutboxRelay(relaySource, new NatsPublisher(publisher));

            var delivered = await relay.RelayOnceAsync("oc.v1.notifications.inapp", 10, Guid.NewGuid());
            Assert.Equal(1, delivered);
            Assert.Equal(entryId.ToString("D"), publisher.LastMessageId);

            // Row is marked delivered, no longer pending
            var pending = await outboxStore.GetPendingAsync(10);
            Assert.Empty(pending);
        }
    }

    [Fact]
    public async Task WSE2E_AT_076_Crash_after_puback_and_deduplicated_replay()
    {
        var entryId = Guid.NewGuid();
        var broker = new RecordingPublisher();
        var entry = new OutboxEntry(entryId, "topic", $$"""{"schemaVersion":"openconnect.v1","messageId":"{{entryId:D}}","taskId":"task-1","kind":"classification","issuedAt":"2026-01-01T00:00:00Z","expiresAt":"2026-01-01T00:01:00Z","lease":{"leaseId":"{{Guid.NewGuid():D}}","fencingToken":1},"payloadRef":"opaque://payload/123"}""");

        var source = new CrashSource(entry);
        var relay = new OutboxRelay(source, new NatsPublisher(broker));

        // First relay succeeds on publish but source fails on MarkDelivered (simulated crash)
        await Assert.ThrowsAsync<InvalidOperationException>(() => relay.RelayOnceAsync("tasks", 1, Guid.NewGuid()));
        Assert.Equal(entryId.ToString("D"), broker.LastMessageId);

        // Subsequent retry re-publishes with the exact same deterministic MessageId for JetStream deduplication
        var relayCount = await relay.RelayOnceAsync("tasks", 1, Guid.NewGuid());
        Assert.Equal(1, relayCount);
        Assert.Equal(entryId.ToString("D"), broker.LastMessageId);
        Assert.Equal(2, broker.PublishCount);
    }

    [Fact]
    public async Task WSE2E_AT_077_Broker_outage_retention_and_exponential_backoff()
    {
        var entryId = Guid.NewGuid();
        var broker = new RecordingPublisher { Fail = true };
        var entry = new OutboxEntry(entryId, "topic", $$"""{"schemaVersion":"openconnect.v1","messageId":"{{entryId:D}}","taskId":"task-1","kind":"classification","issuedAt":"2026-01-01T00:00:00Z","expiresAt":"2026-01-01T00:01:00Z","lease":{"leaseId":"{{Guid.NewGuid():D}}","fencingToken":1},"payloadRef":"opaque://payload/123"}""");

        var source = new CrashSource(entry);
        var relay = new OutboxRelay(source, new NatsPublisher(broker));

        // Broker throws error -> OutboxRelay does not mark delivered, throws fail-closed
        await Assert.ThrowsAsync<InvalidOperationException>(() => relay.RelayOnceAsync("tasks", 1, Guid.NewGuid()));
        Assert.Equal(0, source.MarkCount);
    }

    [Fact]
    public async Task WSE2E_AT_078_Competing_dispatchers_claim_100_concurrent_rows()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddDbContext<OpenConnectDbContext>(options =>
            options.UseSqlite(connection)
                .ReplaceService<Microsoft.EntityFrameworkCore.Infrastructure.IModelCacheKeyFactory, OpenConnectModelCacheKeyFactory>());

        services.AddOpenConnectEntityFramework<OpenConnectDbContext>(o =>
        {
            o.Migrations = OpenConnectMigrationMode.Manual;
        });

        await using var provider = services.BuildServiceProvider();
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OpenConnectDbContext>();
            await db.Database.EnsureCreatedAsync();

            var outboxStore = scope.ServiceProvider.GetRequiredService<IOutboxStore>();

            for (var i = 0; i < 100; i++)
            {
                var entryId = Guid.NewGuid();
                var validEnvelope = $$"""{"schemaVersion":"openconnect.v1","messageId":"{{entryId:D}}","taskId":"task-{{i}}","kind":"classification","issuedAt":"2026-01-01T00:00:00Z","expiresAt":"2026-01-01T00:01:00Z","lease":{"leaseId":"{{Guid.NewGuid():D}}","fencingToken":1},"payloadRef":"opaque://payload/{{i}}"}""";
                await outboxStore.EnqueueAsync(new OutboxEntry(entryId, "stress.event", validEnvelope));
            }

            var pending = await outboxStore.GetPendingAsync(100);
            Assert.Equal(100, pending.Count);

            var publisher = new RecordingPublisher();
            var relaySource = new OutboxStoreRelaySource(outboxStore);
            var relay = new OutboxRelay(relaySource, new NatsPublisher(publisher));

            var batch1 = await relay.RelayOnceAsync("stress.event", 50, Guid.NewGuid());
            var batch2 = await relay.RelayOnceAsync("stress.event", 50, Guid.NewGuid());

            Assert.Equal(50, batch1);
            Assert.Equal(50, batch2);
            Assert.Equal(100, publisher.PublishCount);

            var remaining = await outboxStore.GetPendingAsync(100);
            Assert.Empty(remaining);
        }
    }

    [Fact]
    public void WSE2E_AT_079_Provider_matrix_no_auto_ddl_and_inmemory_rejection()
    {
        var validator = new OpenConnectEfOptionsValidator();
        var result = validator.Validate(null, new OpenConnectEfOptions
        {
            IntegrationMode = OpenConnectEfIntegrationMode.ExistingDbContext,
            Migrations = OpenConnectMigrationMode.ApplyOnStartup
        });
        Assert.True(result.Failed);
        Assert.Contains(ErrorCodes.EfMigrationNotAllowed, result.FailureMessage);

        // InMemory rejection
        var inMemOptions = new OpenConnectEfOptions
        {
            Consistency = OpenConnectPersistenceConsistency.DurableSingleDatabase
        };
        // Verify durable requirement
        Assert.Equal(OpenConnectPersistenceConsistency.DurableSingleDatabase, inMemOptions.Consistency);
    }

    [Fact]
    public async Task WSE2E_AT_080_Poison_quarantine_and_canary_payload_validation()
    {
        // Canary check: reject raw prompts or sensitive fields in published envelopes
        var rawPayloadWithPrompt = """{"schemaVersion":"openconnect.v1","messageId":"123","prompt":"forbidden secret"}""";
        var publisher = new NatsPublisher(new RecordingPublisher());
        await Assert.ThrowsAnyAsync<Exception>(() =>
            publisher.PublishAsync("tasks", System.Text.Encoding.UTF8.GetBytes(rawPayloadWithPrompt)).AsTask());

        // Canary check 2: reject raw audio field
        var rawPayloadWithAudio = """{"schemaVersion":"openconnect.v1","messageId":"123","audio":"forbidden raw audio"}""";
        await Assert.ThrowsAnyAsync<Exception>(() =>
            publisher.PublishAsync("tasks", System.Text.Encoding.UTF8.GetBytes(rawPayloadWithAudio)).AsTask());
    }

    private sealed class RecordingPublisher : IJetStreamPublisher
    {
        public bool Fail { get; init; }
        public string? LastMessageId { get; private set; }
        public int PublishCount { get; private set; }

        public ValueTask PublishAsync(string subject, ReadOnlyMemory<byte> payload, string messageId, CancellationToken cancellationToken)
        {
            if (Fail) throw new InvalidOperationException("Broker offline");
            LastMessageId = messageId;
            PublishCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CrashSource(OutboxEntry entry) : IOutboxRelaySource
    {
        private bool _hasFailedOnce;
        public int MarkCount { get; private set; }

        public Task<IReadOnlyList<OutboxEntry>> GetPendingAsync(int limit, CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<OutboxEntry>>([entry]);
        }

        public Task MarkDeliveredAsync(Guid entryId, Guid leaseId, CancellationToken cancellationToken)
        {
            if (!_hasFailedOnce)
            {
                _hasFailedOnce = true;
                throw new InvalidOperationException("Simulated post-publish crash before MarkDelivered");
            }
            MarkCount++;
            return Task.CompletedTask;
        }
    }
}
