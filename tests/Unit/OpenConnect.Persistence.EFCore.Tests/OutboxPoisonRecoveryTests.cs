using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using OpenConnect.Abstractions;
using OpenConnect.Persistence.EFCore.Configuration;
using OpenConnect.Persistence.EFCore.Context;
using OpenConnect.Persistence.EFCore.Entities;
using OpenConnect.Persistence.EFCore.Outbox;
using OpenConnect.Persistence.EFCore.Providers;
using OpenConnect.Persistence.EFCore.Stores;
using Xunit;

namespace OpenConnect.Persistence.EFCore.Tests;

public sealed class OutboxPoisonRecoveryTests
{
    [Fact]
    public async Task Expired_claim_failure_cannot_increment_attempts_or_quarantine()
    {
        await using var fixture = await Database.CreateAsync();
        await using var db = fixture.Open();
        var id = Guid.NewGuid();
        await new EfOutboxStore<OpenConnectDbContext>(db).EnqueueAsync(new(id, "test", "payload"));
        var publisher = new Publisher(async (_, _, _, _) =>
        {
            // Force the claim to expire while its external publish is in progress.
            var row = await db.Set<OutboxEntity>().SingleAsync();
            row.LeaseExpiresUtc = DateTime.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
            throw new InvalidOperationException("untrusted payload in broker error");
        });
        Assert.Equal(0, await Dispatch(db, publisher, "stale", 1));
        await using var read = fixture.Open();
        var actual = await read.Set<OutboxEntity>().SingleAsync();
        Assert.Equal(0, actual.Attempts);
        Assert.Equal("pending", actual.Status);
    }

    [Fact]
    public async Task Cancellation_propagates_without_incrementing_attempts()
    {
        await using var fixture = await Database.CreateAsync();
        await using var db = fixture.Open();
        await new EfOutboxStore<OpenConnectDbContext>(db).EnqueueAsync(new(Guid.NewGuid(), "test", "payload"));
        using var cancellation = new CancellationTokenSource();
        var publisher = new Publisher((_, _, _, ct) =>
        {
            cancellation.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Dispatch(db, publisher, "cancelled", 1, cancellation.Token));
        Assert.Equal(0, (await db.Set<OutboxEntity>().SingleAsync()).Attempts);
        await using var read = fixture.Open();
        Assert.Equal(0, (await read.Set<OutboxEntity>().SingleAsync()).Attempts);
    }

    [Fact]
    public async Task Failure_backoff_and_quarantine_are_durable_and_do_not_block_good_rows()
    {
        await using var fixture = await Database.CreateAsync();
        var bad = Guid.NewGuid();
        var good = Guid.NewGuid();
        await using (var seed = fixture.Open())
        {
            var store = new EfOutboxStore<OpenConnectDbContext>(seed);
            await store.EnqueueAsync(new(bad, "bad", "poison"));
            await store.EnqueueAsync(new(good, "good", "valid"));
        }
        var deliveries = new List<string>();
        var publisher = new Publisher((subject, eventId, _, _) =>
        {
            if (subject == "bad")
                throw new InvalidDataException("poison");
            deliveries.Add(eventId);
            return Task.CompletedTask;
        });
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var before = DateTime.UtcNow;
            await using (var run = fixture.Open())
                Assert.Equal(attempt == 1 ? 1 : 0, await Dispatch(run, publisher, "host", 3));
            await using var read = fixture.Open();
            var row = await read.Set<OutboxEntity>().SingleAsync(x => x.Id == bad);
            Assert.Equal(attempt, row.Attempts);
            Assert.True(row.NextAttemptUtc >= before.AddSeconds(Math.Pow(2, attempt)));
            Assert.Null(row.LeaseOwner);
            Assert.Null(row.LeaseExpiresUtc);
            Assert.Equal(attempt == 3 ? "quarantined" : "pending", row.Status);
            await using (var early = fixture.Open())
                Assert.Equal(0, await Dispatch(early, publisher, "other-host", 3));
            row.NextAttemptUtc = DateTime.UtcNow.AddMinutes(-1);
            await read.SaveChangesAsync();
        }
        Assert.Equal([good.ToString("D")], deliveries);
        await using var final = fixture.Open();
        Assert.Equal(0, await Dispatch(final, publisher, "later", 3));
        Assert.Equal(3, (await final.Set<OutboxEntity>().SingleAsync(x => x.Id == bad)).Attempts);
    }

    [Fact]
    public async Task Two_dispatchers_cannot_complete_the_same_live_claim()
    {
        await using var fixture = await Database.CreateAsync();
        await using (var seed = fixture.Open())
            await new EfOutboxStore<OpenConnectDbContext>(seed).EnqueueAsync(new(Guid.NewGuid(), "test", "payload"));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var published = 0;
        var publisher = new Publisher(async (_, _, _, _) =>
        {
            Interlocked.Increment(ref published);
            entered.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(10));
        });
        await using var a = fixture.Open();
        await using var b = fixture.Open();
        var first = Dispatch(a, publisher, "host-a", 3);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, await Dispatch(b, publisher, "host-b", 3));
        }
        finally { release.TrySetResult(); }
        Assert.Equal(1, await first);
        Assert.Equal(1, published);
        await using var read = fixture.Open();
        Assert.Equal("delivered", (await read.Set<OutboxEntity>().SingleAsync()).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Late_failure_cannot_mutate_a_reclaimed_row(bool completeNewClaim)
    {
        await using var fixture = await Database.CreateAsync();
        await using (var seed = fixture.Open())
            await new EfOutboxStore<OpenConnectDbContext>(seed).EnqueueAsync(new(Guid.NewGuid(), "test", "payload"));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stalePublisher = new Publisher(async (_, _, _, _) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(10));
            throw new InvalidDataException("late untrusted publish failure");
        });
        await using var stale = fixture.Open();
        var oldDispatch = Dispatch(stale, stalePublisher, "old-owner", 1);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await using (var expire = fixture.Open())
            {
                var row = await expire.Set<OutboxEntity>().SingleAsync();
                row.LeaseExpiresUtc = DateTime.UtcNow.AddMinutes(-1);
                await expire.SaveChangesAsync();
            }
            await using var fresh = fixture.Open();
            if (completeNewClaim)
                Assert.Equal(1, await Dispatch(fresh, new Publisher((_, _, _, _) => Task.CompletedTask), "new-owner", 1));
            else
                Assert.Single(await new OptimisticConcurrencyClaimDialect().ClaimBatchAsync(fresh,
                    new OutboxClaimRequest(1, DateTime.UtcNow, "new-owner", TimeSpan.FromMinutes(1)), CancellationToken.None));
        }
        finally { release.TrySetResult(); }
        Assert.Equal(0, await oldDispatch);
        await using var read = fixture.Open();
        var result = await read.Set<OutboxEntity>().SingleAsync();
        Assert.Equal(completeNewClaim ? "delivered" : "pending", result.Status);
        Assert.Equal(0, result.Attempts);
        Assert.Equal("new-owner", result.LeaseOwner);
        Assert.Equal(2, result.FencingVersion);
    }

    [Fact]
    public async Task NoTracking_context_persists_claim_before_publish_and_delivery_or_retry_afterwards()
    {
        await using var fixture = await Database.CreateAsync();
        var good = Guid.NewGuid();
        var bad = Guid.NewGuid();
        await using (var seed = fixture.Open())
        {
            var store = new EfOutboxStore<OpenConnectDbContext>(seed);
            await store.EnqueueAsync(new(good, "good", "payload"));
            await store.EnqueueAsync(new(bad, "bad", "payload"));
        }
        var calls = 0;
        var claimsWerePersisted = true;
        var publisher = new Publisher(async (subject, eventId, _, _) =>
        {
            calls++;
            await using var read = fixture.Open();
            var row = await read.Set<OutboxEntity>().SingleAsync(x => x.Id == Guid.Parse(eventId));
            claimsWerePersisted &= row.LeaseOwner == "no-tracking" && row.FencingVersion > 0 && row.LeaseExpiresUtc > DateTime.UtcNow;
            if (subject == "bad")
                throw new InvalidDataException("poison");
        });
        await using (var db = fixture.Open())
        {
            db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
            Assert.Equal(1, await new EfOutboxDispatcher().ExecuteBatchAsync(db, publisher,
                "no-tracking", TimeSpan.FromMinutes(1), maxAttempts: 2));
        }
        Assert.Equal(2, calls);
        Assert.True(claimsWerePersisted, "A live row claim must be durably stored before any external publish.");
        await using var check = fixture.Open();
        Assert.Equal("delivered", (await check.Set<OutboxEntity>().SingleAsync(x => x.Id == good)).Status);
        var failed = await check.Set<OutboxEntity>().SingleAsync(x => x.Id == bad);
        Assert.Equal(1, failed.Attempts);
        Assert.Equal("pending", failed.Status);
        Assert.True(failed.NextAttemptUtc > DateTime.UtcNow);
        Assert.Null(failed.LeaseOwner);
    }

    [Fact]
    public async Task Claim_expired_while_waiting_in_batch_is_not_published()
    {
        await using var fixture = await Database.CreateAsync();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        await using var db = fixture.Open();
        var store = new EfOutboxStore<OpenConnectDbContext>(db);
        await store.EnqueueAsync(new(first, "first", "payload"));
        await store.EnqueueAsync(new(second, "second", "payload"));
        var published = new List<string>();
        var publisher = new Publisher(async (subject, _, _, _) =>
        {
            published.Add(subject);
            if (subject == "first")
            {
                var later = await db.Set<OutboxEntity>().SingleAsync(x => x.Id == second);
                later.LeaseExpiresUtc = DateTime.UtcNow.AddMinutes(-1);
                await db.SaveChangesAsync();
            }
        });
        Assert.Equal(1, await Dispatch(db, publisher, "slow-owner", 2));
        Assert.Equal(["first"], published);
        await using var read = fixture.Open();
        var deferred = await read.Set<OutboxEntity>().SingleAsync(x => x.Id == second);
        Assert.Equal("pending", deferred.Status);
        Assert.Equal(0, deferred.Attempts);
    }

    [Fact]
    public async Task Prepublish_guard_reads_a_later_claims_new_owner_from_database()
    {
        await using var fixture = await Database.CreateAsync();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        await using var db = fixture.Open();
        var store = new EfOutboxStore<OpenConnectDbContext>(db);
        await store.EnqueueAsync(new(first, "first", "payload"));
        await store.EnqueueAsync(new(second, "second", "payload"));
        var published = new List<string>();
        var publisher = new Publisher(async (subject, _, _, _) =>
        {
            published.Add(subject);
            if (subject == "first")
            {
                await using var replacement = fixture.Open();
                var row = await replacement.Set<OutboxEntity>().SingleAsync(x => x.Id == second);
                row.LeaseOwner = "replacement-owner";
                row.FencingVersion++;
                row.LeaseExpiresUtc = DateTime.UtcNow.AddMinutes(2);
                await replacement.SaveChangesAsync();
            }
        });
        Assert.Equal(1, await Dispatch(db, publisher, "old-owner", 2));
        Assert.Equal(["first"], published);
        await using var read = fixture.Open();
        var unchanged = await read.Set<OutboxEntity>().SingleAsync(x => x.Id == second);
        Assert.Equal("pending", unchanged.Status);
        Assert.Equal("replacement-owner", unchanged.LeaseOwner);
        Assert.Equal(0, unchanged.Attempts);
    }

    [Fact]
    public async Task Completion_does_not_adopt_a_reloaded_replacement_owners_token()
    {
        await using var fixture = await Database.CreateAsync();
        await using var db = fixture.Open();
        await new EfOutboxStore<OpenConnectDbContext>(db).EnqueueAsync(new(Guid.NewGuid(), "test", "payload"));
        var publisher = new Publisher(async (_, _, _, _) =>
        {
            await using var replacement = fixture.Open();
            var row = await replacement.Set<OutboxEntity>().SingleAsync();
            row.LeaseOwner = "new-owner";
            row.FencingVersion++;
            await replacement.SaveChangesAsync();
        });
        Assert.Equal(0, await Dispatch(db, publisher, "old-owner", 2));
        await using var read = fixture.Open();
        var pending = await read.Set<OutboxEntity>().SingleAsync();
        Assert.Equal("pending", pending.Status);
        Assert.Equal("new-owner", pending.LeaseOwner);
        Assert.Equal(2, pending.FencingVersion);
        Assert.Equal(0, pending.Attempts);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Expiry_between_validation_and_write_prevents_completion_or_retry(bool publishFails, bool elapseNaturally)
    {
        await using var fixture = await Database.CreateAsync();
        var interceptor = new ExpireBeforeMutation(fixture, elapseNaturally);
        await using var db = fixture.Open(interceptor);
        var id = Guid.NewGuid();
        await new EfOutboxStore<OpenConnectDbContext>(db).EnqueueAsync(new(id, "test", "payload"));
        var publisher = new Publisher(async (_, _, _, _) =>
        {
            if (elapseNaturally)
            {
                await using var other = fixture.Open();
                await other.Database.ExecuteSqlInterpolatedAsync($"UPDATE Outbox SET LeaseExpiresUtc={DateTime.UtcNow.AddSeconds(1)}");
            }
            interceptor.Armed = true;
            if (publishFails)
                throw new InvalidOperationException("broker failure");
        });
        Assert.Equal(0, await Dispatch(db, publisher, "owner", 1));
        Assert.True(interceptor.Expired);
        await using var read = fixture.Open();
        var row = await read.Set<OutboxEntity>().SingleAsync();
        Assert.Equal("pending", row.Status);
        Assert.Equal(0, row.Attempts);
        Assert.Null(row.PublishedUtc);
        Assert.Equal("owner", row.LeaseOwner);
        Assert.Equal(1, row.FencingVersion);
        Assert.True(row.LeaseExpiresUtc < DateTime.UtcNow);
    }

    private sealed class ExpireBeforeMutation(Database fixture, bool elapseNaturally) : DbCommandInterceptor
    {
        public bool Armed { get; set; }
        public bool Expired { get; private set; }
        private async Task BeforeMutation(DbCommand command, CancellationToken ct)
        {
            if (!Armed || Expired || !command.CommandText.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase))
                return;
            Expired = true;
            await using var other = fixture.Open();
            if (elapseNaturally)
            {
                var expires = (await other.Set<OutboxEntity>().SingleAsync(ct)).LeaseExpiresUtc!.Value;
                var remaining = expires - DateTime.UtcNow;
                if (remaining > TimeSpan.Zero)
                    await Task.Delay(remaining + TimeSpan.FromMilliseconds(20), ct);
            }
            else
                await other.Database.ExecuteSqlInterpolatedAsync($"UPDATE Outbox SET LeaseExpiresUtc={DateTime.UtcNow.AddMinutes(-1)}", ct);
        }
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            await BeforeMutation(command, cancellationToken);
            return result;
        }
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            await BeforeMutation(command, cancellationToken);
            return result;
        }
    }

    [Fact]
    public async Task Unsupported_provider_is_rejected_before_claim_or_publication()
    {
        await using var db = new OpenConnectDbContext(new DbContextOptionsBuilder<OpenConnectDbContext>()
            .UseInMemoryDatabase($"outbox-unsupported-{Guid.NewGuid():N}").Options);
        var published = false;
        var publisher = new Publisher((_, _, _, _) =>
        {
            published = true;
            return Task.CompletedTask;
        });
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => Dispatch(db, publisher, "owner", 1));
        Assert.Equal("Atomic outbox lease updates require a supported relational provider.", failure.Message);
        Assert.False(published);
        Assert.Empty(db.ChangeTracker.Entries());
    }

    private static Task<int> Dispatch(OpenConnectDbContext db, ICommittedOutboxPublisher publisher,
        string owner, int maxAttempts, CancellationToken ct = default) =>
        new EfOutboxDispatcher().ExecuteBatchAsync(db, OpenConnectOutboxClaimStrategy.Auto,
            new OutboxClaimRequest(10, DateTime.UtcNow, owner, TimeSpan.FromMinutes(1)), publisher, maxAttempts, ct);

    private sealed class Publisher(Func<string, string, string, CancellationToken, Task> publish) : ICommittedOutboxPublisher
    {
        public Task PublishAsync(string subject, string eventId, string sanitizedPayload, CancellationToken ct) => publish(subject, eventId, sanitizedPayload, ct);
    }

    private sealed class Database(string file) : IAsyncDisposable
    {
        public OpenConnectDbContext Open(params IInterceptor[] interceptors) => new(new DbContextOptionsBuilder<OpenConnectDbContext>()
            .UseSqlite($"Data Source={file};Pooling=False").AddInterceptors(interceptors).Options);
        public static async Task<Database> CreateAsync()
        {
            var fixture = new Database(Path.Combine(Path.GetTempPath(), $"oc-outbox-poison-{Guid.NewGuid():N}.db"));
            await using var db = fixture.Open();
            await db.Database.EnsureCreatedAsync();
            return fixture;
        }
        public ValueTask DisposeAsync()
        {
            File.Delete(file);
            return ValueTask.CompletedTask;
        }
    }
}
