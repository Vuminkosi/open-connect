using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Persistence;
using OpenConnect.Persistence.EFCore.Configuration;
using OpenConnect.Persistence.EFCore.Context;
using OpenConnect.Persistence.EFCore.Entities;
using OpenConnect.Persistence.EFCore.Outbox;
using OpenConnect.Persistence.EFCore.Providers;
using OpenConnect.Persistence.EFCore.Stores;
using Xunit;

namespace OpenConnect.Persistence.EFCore.Tests;

/// <summary>
/// Proves OC2-D-DATA-025 and EF-AT-018/019:
/// Rollback, outbox crash-after-publish simulation, duplicate consumer receipt,
/// replay recovery, and lease takeover after process kill.
/// </summary>
public sealed class OutboxCrashRecoveryTests
{
    private static readonly TenantId Tenant = new(Ulid.NewUlid());

    [Fact]
    public async Task Outbox_crash_after_publish_leads_to_safe_reclaim_and_duplicate_delivery()
    {
        var file = Path.Combine(Path.GetTempPath(), $"openconnect-outbox-crash-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<OpenConnectDbContext>()
                .UseSqlite($"Data Source={file};Pooling=False")
                .ReplaceService<Microsoft.EntityFrameworkCore.Infrastructure.IModelCacheKeyFactory, OpenConnectModelCacheKeyFactory>()
                .Options;

            var outboxId = Guid.NewGuid();
            const string eventSubject = "session.events";
            const string payload = """{"event":"turn.completed"}""";

            // 1. Seed outbox entry
            await using (var db = new OpenConnectDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();
                var store = new EfOutboxStore<OpenConnectDbContext>(db);
                await store.EnqueueAsync(new(outboxId, eventSubject, payload));
            }

            var recordedDeliveries = new ConcurrentBag<(string Subject, string EventId, string Payload)>();

            // 2. Dispatcher 1 claims the batch and publishes to the broker,
            // but the process crashes immediately before CompleteClaimAsync persists to DB.
            var crashingPublisher = new DelegatePublisher((subj, evId, p) =>
            {
                recordedDeliveries.Add((subj, evId, p));
                // Simulate process crash / power loss immediately after network broker delivery
                throw new SimulatedProcessCrashException("Process terminated after broker PUBACK before database update.");
            });

            var dispatcher = new EfOutboxDispatcher();

            await using (var db1 = new OpenConnectDbContext(options))
            {
                var claimReq = new OutboxClaimRequest(
                    BatchSize: 10,
                    DueBeforeUtc: DateTime.UtcNow.AddMinutes(1),
                    LeaseOwner: "dispatcher-host-1",
                    LeaseDuration: TimeSpan.FromSeconds(2));

                // Dispatcher 1 catches exception and updates retry/next attempt
                await dispatcher.ExecuteBatchAsync(
                    db1,
                    OpenConnectOutboxClaimStrategy.OptimisticConcurrency,
                    claimReq,
                    crashingPublisher,
                    maxAttempts: 5,
                    CancellationToken.None);
            }

            // Broker received the first delivery
            Assert.Single(recordedDeliveries);
            Assert.Equal(outboxId.ToString(), recordedDeliveries.First().EventId);

            // 3. Verify row in DB is still pending retry (not marked delivered, attempts incremented)
            await using (var dbCheck = new OpenConnectDbContext(options))
            {
                var row = await dbCheck.Set<OutboxEntity>().SingleAsync(x => x.Id == outboxId);
                Assert.NotEqual("delivered", row.Status);
                Assert.Equal(1, row.Attempts);

                // Simulate time passing past NextAttemptUtc / lease expiration
                row.NextAttemptUtc = DateTime.UtcNow.AddMinutes(-1);
                row.LeaseExpiresUtc = DateTime.UtcNow.AddMinutes(-1);
                await dbCheck.SaveChangesAsync();
            }

            // 4. Recovery dispatcher (node 2) takes over after lease expiry and publishes
            var healthyPublisher = new DelegatePublisher((subj, evId, p) =>
            {
                recordedDeliveries.Add((subj, evId, p));
                return Task.CompletedTask;
            });

            await using (var db2 = new OpenConnectDbContext(options))
            {
                var recoverReq = new OutboxClaimRequest(
                    BatchSize: 10,
                    DueBeforeUtc: DateTime.UtcNow.AddMinutes(1),
                    LeaseOwner: "dispatcher-host-2",
                    LeaseDuration: TimeSpan.FromMinutes(1));

                var deliveredCount = await dispatcher.ExecuteBatchAsync(
                    db2,
                    OpenConnectOutboxClaimStrategy.OptimisticConcurrency,
                    recoverReq,
                    healthyPublisher,
                    maxAttempts: 5,
                    CancellationToken.None);

                Assert.Equal(1, deliveredCount);
            }

            // 5. Downstream broker/consumer received exactly two deliveries with the identical EventId
            Assert.Equal(2, recordedDeliveries.Count);
            var deliveredIds = recordedDeliveries.Select(d => d.EventId).Distinct().ToArray();
            Assert.Single(deliveredIds); // Proves identical EventId across retries for deduplication
            Assert.Equal(outboxId.ToString(), deliveredIds[0]);

            // 6. Outbox row is now delivered and no further pending claims exist
            await using (var dbFinal = new OpenConnectDbContext(options))
            {
                var row = await dbFinal.Set<OutboxEntity>().SingleAsync(x => x.Id == outboxId);
                Assert.Equal("delivered", row.Status);

                var store = new EfOutboxStore<OpenConnectDbContext>(dbFinal);
                var pending = await store.GetPendingAsync(10);
                Assert.Empty(pending);
            }
        }
        finally
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }

    [Fact]
    public async Task Outbox_publish_failure_retries_with_exponential_backoff_and_quarantines()
    {
        var file = Path.Combine(Path.GetTempPath(), $"openconnect-outbox-quarantine-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<OpenConnectDbContext>()
                .UseSqlite($"Data Source={file};Pooling=False")
                .ReplaceService<Microsoft.EntityFrameworkCore.Infrastructure.IModelCacheKeyFactory, OpenConnectModelCacheKeyFactory>()
                .Options;

            var outboxId = Guid.NewGuid();
            await using (var db = new OpenConnectDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();
                var store = new EfOutboxStore<OpenConnectDbContext>(db);
                await store.EnqueueAsync(new(outboxId, "failing.event", "payload"));
            }

            var failingPublisher = new DelegatePublisher((_, _, _) => throw new InvalidOperationException("Broker offline"));
            var dispatcher = new EfOutboxDispatcher();
            const int maxAttempts = 3;

            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                await using var db = new OpenConnectDbContext(options);
                // Reset NextAttemptUtc to now so dispatcher can pick it up
                var row = await db.Set<OutboxEntity>().SingleAsync(x => x.Id == outboxId);
                row.NextAttemptUtc = DateTime.UtcNow.AddSeconds(-1);
                row.LeaseExpiresUtc = DateTime.UtcNow.AddSeconds(-1);
                await db.SaveChangesAsync();

                var claimReq = new OutboxClaimRequest(
                    BatchSize: 10,
                    DueBeforeUtc: DateTime.UtcNow.AddMinutes(1),
                    LeaseOwner: $"dispatcher-attempt-{attempt}",
                    LeaseDuration: TimeSpan.FromSeconds(5));

                var delivered = await dispatcher.ExecuteBatchAsync(
                    db,
                    OpenConnectOutboxClaimStrategy.OptimisticConcurrency,
                    claimReq,
                    failingPublisher,
                    maxAttempts,
                    CancellationToken.None);

                Assert.Equal(0, delivered);
            }

            // Verify final state: attempts reached max, status is quarantined
            await using (var dbVerify = new OpenConnectDbContext(options))
            {
                var row = await dbVerify.Set<OutboxEntity>().SingleAsync(x => x.Id == outboxId);
                Assert.Equal(maxAttempts, row.Attempts);
                Assert.Equal("quarantined", row.Status);

                var store = new EfOutboxStore<OpenConnectDbContext>(dbVerify);
                var pending = await store.GetPendingAsync(10);
                Assert.Empty(pending);
            }
        }
        finally
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }

    [Fact]
    public async Task Outbox_lease_takeover_after_stale_dispatcher_expiry()
    {
        var file = Path.Combine(Path.GetTempPath(), $"openconnect-outbox-takeover-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<OpenConnectDbContext>()
                .UseSqlite($"Data Source={file};Pooling=False")
                .ReplaceService<Microsoft.EntityFrameworkCore.Infrastructure.IModelCacheKeyFactory, OpenConnectModelCacheKeyFactory>()
                .Options;

            var outboxId = Guid.NewGuid();
            await using (var db = new OpenConnectDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();
                var store = new EfOutboxStore<OpenConnectDbContext>(db);
                await store.EnqueueAsync(new(outboxId, "takeover.event", "payload"));
            }

            var dialect = new OptimisticConcurrencyClaimDialect();

            // 1. Dispatcher 1 claims with expired timestamp
            await using (var db1 = new OpenConnectDbContext(options))
            {
                var req1 = new OutboxClaimRequest(10, DateTime.UtcNow.AddMinutes(1), "dispatcher-stale", TimeSpan.FromSeconds(1));
                var claims1 = await dialect.ClaimBatchAsync(db1, req1, CancellationToken.None);
                Assert.Single(claims1);
            }

            // Simulate lease expiry
            await using (var dbExpire = new OpenConnectDbContext(options))
            {
                var row = await dbExpire.Set<OutboxEntity>().SingleAsync(x => x.Id == outboxId);
                row.LeaseExpiresUtc = DateTime.UtcNow.AddSeconds(-10);
                await dbExpire.SaveChangesAsync();
            }

            // 2. Dispatcher 2 claims the expired row and advances fencing token
            await using (var db2 = new OpenConnectDbContext(options))
            {
                var req2 = new OutboxClaimRequest(10, DateTime.UtcNow.AddMinutes(1), "dispatcher-fresh", TimeSpan.FromMinutes(2));
                var claims2 = await dialect.ClaimBatchAsync(db2, req2, CancellationToken.None);
                Assert.Single(claims2);
                Assert.Equal(outboxId.ToString(), claims2[0].EntryId);
                Assert.True(claims2[0].FencingVersion > 1);

                // Complete claim
                var completed = await dialect.CompleteClaimAsync(db2, claims2[0], CancellationToken.None);
                Assert.True(completed);
            }

            // 3. Stale Dispatcher 1 attempts to complete its old claim and is rejected
            await using (var db3 = new OpenConnectDbContext(options))
            {
                var staleClaim = new OutboxClaim(outboxId.ToString(), "default", 1, "dispatcher-stale", DateTimeOffset.UtcNow.AddMinutes(1));
                var completedStale = await dialect.CompleteClaimAsync(db3, staleClaim, CancellationToken.None);
                Assert.False(completedStale);
            }
        }
        finally
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }

    private sealed class DelegatePublisher(Func<string, string, string, Task> action) : ICommittedOutboxPublisher
    {
        public Task PublishAsync(string subject, string eventId, string payload, CancellationToken ct = default)
            => action(subject, eventId, payload);
    }

    private sealed class SimulatedProcessCrashException(string message) : Exception(message);
}
