using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenConnect.Docs.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

[Collection("Cloud PostgreSQL")]
public sealed class CloudQuotaRecoveryPostgresTests
{
    [Fact]
    public async Task RecoveryWorkerRunsBoundedBatchWithGracePeriod()
    {
        var store = new CapturingStore();
        var services = new ServiceCollection();
        services.AddSingleton<ICloudQuotaRecoveryStore>(store);
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<CloudQuotaRecovery>();
        await using var provider = services.BuildServiceProvider();
        using var worker = new CloudQuotaRecoveryWorker(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<CloudQuotaRecoveryWorker>.Instance);
        var before = DateTimeOffset.UtcNow;
        await worker.StartAsync(default);
        var (cutoff, limit) = await store.Called.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.StopAsync(default);
        Assert.Equal(100, limit);
        Assert.InRange(cutoff, before.AddMinutes(-5).AddSeconds(-1), DateTimeOffset.UtcNow.AddMinutes(-5).AddSeconds(1));
    }

    [Fact]
    public async Task UnconsumedClaimWorkerUsesBoundedBatchAndGracePeriod()
    {
        var store = new CapturingClaimStore();
        var services = new ServiceCollection();
        services.AddSingleton<ICloudUnconsumedClaimRecoveryStore>(store);
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<CloudUnconsumedClaimRecovery>();
        await using var provider = services.BuildServiceProvider();
        using var worker = new CloudUnconsumedClaimRecoveryWorker(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<CloudUnconsumedClaimRecoveryWorker>.Instance);
        var before = DateTimeOffset.UtcNow;
        await worker.StartAsync(default);
        var (cutoff, limit) = await store.Called.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.StopAsync(default);
        Assert.Equal(100, limit);
        Assert.InRange(cutoff, before.AddMinutes(-5).AddSeconds(-1), DateTimeOffset.UtcNow.AddMinutes(-5).AddSeconds(1));
    }

    [CloudPostgresFact]
    public async Task ExpiredUnclaimedClassificationReservationsRecoverOnceAndFenceOlderClaims()
    {
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_TEST_DATABASE")).Options;
        IDbContextFactory<CloudControlPlaneDbContext> factory = new TestFactory(options);
        await using (var db = await factory.CreateDbContextAsync()) await db.Database.MigrateAsync();
        var owner = new CloudSubject("test-issuer", Guid.NewGuid().ToString("N"));
        var tenants = new EfCloudTenantStore(factory, TimeProvider.System);
        var tenant = await tenants.CreateTenantAsync(owner, "Recovery test", default);
        var app = (await tenants.CreateApplicationAsync(owner, tenant.Id, "App", "Development", default)).Application!;
        var now = DateTimeOffset.UtcNow;
        var classificationBucket = Guid.NewGuid();
        var otherBucket = Guid.NewGuid();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.QuotaBuckets.AddRange(
                new CloudQuotaBucket { Id = classificationBucket, TenantId = tenant.Id, ApplicationId = app.Id, CapabilityId = "classification.classify", Unit = "request", PeriodStart = now.AddHours(-1), PeriodEnd = now.AddHours(1), Limit = 10 },
                new CloudQuotaBucket { Id = otherBucket, TenantId = tenant.Id, ApplicationId = app.Id, CapabilityId = "inference.chat", Unit = "request", PeriodStart = now.AddHours(-1), PeriodEnd = now.AddHours(1), Limit = 10 });
            await db.SaveChangesAsync();
        }
        var quota = new CloudQuotaService(new EfCloudQuotaStore(factory), TimeProvider.System);
        async Task<Guid> Reserve(string capability)
        {
            var result = await quota.ReserveAsync(new CloudQuotaRequest(tenant.Id, app.Id, capability, "request", Guid.NewGuid(), "key", new byte[32], 1, now.AddMinutes(1)), default);
            Assert.Equal("reserved", result.Status);
            return result.ReservationId!.Value;
        }
        var expired = await Reserve("classification.classify");
        var claimed = await Reserve("classification.classify");
        var live = await Reserve("classification.classify");
        var recentlyExpired = await Reserve("classification.classify");
        var other = await Reserve("inference.chat");
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.ExecutionClaims.Add(new CloudExecutionClaim { ReservationId = claimed, TenantId = tenant.Id, ApplicationId = app.Id, ClaimId = Guid.NewGuid(), ClaimedAt = now });
            await db.SaveChangesAsync();
            await db.QuotaReservations.Where(x => x.Id == expired || x.Id == claimed || x.Id == other)
                .ExecuteUpdateAsync(x => x.SetProperty(y => y.ExpiresAt, now.AddMinutes(-10)));
            await db.QuotaReservations.Where(x => x.Id == recentlyExpired).ExecuteUpdateAsync(x => x.SetProperty(y => y.ExpiresAt, now.AddMinutes(-2)));
        }
        var store = new EfCloudQuotaRecoveryStore(factory, TimeProvider.System);
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => store.ReleaseUnclaimedAsync(now.AddMinutes(-5), 100, default)));
        Assert.True(results.Sum() >= 1);
        await using (var db = await factory.CreateDbContextAsync())
        {
            var released = await db.QuotaReservations.SingleAsync(x => x.Id == expired);
            Assert.Equal(0, released.Actual);
            Assert.NotNull(released.FinalizedAt);
            Assert.Null((await db.QuotaReservations.SingleAsync(x => x.Id == claimed)).FinalizedAt);
            Assert.Null((await db.QuotaReservations.SingleAsync(x => x.Id == live)).FinalizedAt);
            Assert.Null((await db.QuotaReservations.SingleAsync(x => x.Id == recentlyExpired)).FinalizedAt);
            Assert.Null((await db.QuotaReservations.SingleAsync(x => x.Id == other)).FinalizedAt);
            Assert.Equal(3, (await db.QuotaBuckets.SingleAsync(x => x.Id == classificationBucket)).Reserved);
            Assert.Equal(1, (await db.QuotaBuckets.SingleAsync(x => x.Id == otherBucket)).Reserved);
            Assert.Equal(0, (await db.UsageRecords.SingleAsync(x => x.ReservationId == expired)).Quantity);
            db.ExecutionClaims.Add(new CloudExecutionClaim { ReservationId = expired, TenantId = tenant.Id, ApplicationId = app.Id, ClaimId = Guid.NewGuid(), ClaimedAt = now });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        var racing = await Reserve("classification.classify");
        await using (var db = await factory.CreateDbContextAsync())
            await db.QuotaReservations.Where(x => x.Id == racing).ExecuteUpdateAsync(x => x.SetProperty(y => y.ExpiresAt, now.AddMinutes(-10)));
        await using (var writer = await factory.CreateDbContextAsync())
        {
            await using var transaction = await writer.Database.BeginTransactionAsync();
            writer.ExecutionClaims.Add(new CloudExecutionClaim { ReservationId = racing, TenantId = tenant.Id, ApplicationId = app.Id, ClaimId = Guid.NewGuid(), ClaimedAt = now });
            await writer.SaveChangesAsync();
            await store.ReleaseUnclaimedAsync(now.AddMinutes(-5), 100, default);
            await transaction.CommitAsync();
        }
        await store.ReleaseUnclaimedAsync(now.AddMinutes(-5), 100, default);
        await using (var db = await factory.CreateDbContextAsync())
            Assert.Null((await db.QuotaReservations.SingleAsync(x => x.Id == racing)).FinalizedAt);
    }

    [CloudPostgresFact]
    public async Task ExpiredUnconsumedClaimsRecoverOnceAndFenceOlderConsumptionWriters()
    {
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_TEST_DATABASE")).Options;
        IDbContextFactory<CloudControlPlaneDbContext> factory = new TestFactory(options);
        await using (var db = await factory.CreateDbContextAsync()) await db.Database.MigrateAsync();
        var owner = new CloudSubject("test-issuer", Guid.NewGuid().ToString("N"));
        var tenants = new EfCloudTenantStore(factory, TimeProvider.System);
        var tenant = await tenants.CreateTenantAsync(owner, "Claim recovery", default);
        var app = (await tenants.CreateApplicationAsync(owner, tenant.Id, "App", "Development", default)).Application!;
        var now = DateTimeOffset.UtcNow;
        var bucket = Guid.NewGuid(); var deployment = Guid.NewGuid();
        var expired = Guid.NewGuid(); var consumed = Guid.NewGuid(); var legacy = Guid.NewGuid(); var recent = Guid.NewGuid(); var racing = Guid.NewGuid(); var completing = Guid.NewGuid();
        var expiredClaim = Guid.NewGuid(); var consumedClaim = Guid.NewGuid(); var legacyClaim = Guid.NewGuid(); var recentClaim = Guid.NewGuid(); var racingClaim = Guid.NewGuid(); var completingClaim = Guid.NewGuid();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.QuotaBuckets.Add(new CloudQuotaBucket { Id = bucket, TenantId = tenant.Id, ApplicationId = app.Id, CapabilityId = "classification.classify",
                Unit = "request", PeriodStart = now.AddHours(-1), PeriodEnd = now.AddHours(1), Limit = 20, Reserved = 6 });
            db.WorkerDeployments.Add(new CloudWorkerDeployment { Id = deployment, CapabilityId = "classification.classify", EndpointReference = "recovery-" + Guid.NewGuid().ToString("N"),
                Transport = "https-mtls", Region = "region-a", ContractVersion = "openconnect.v1", ImageDigest = "sha256:" + new string('a', 64), Enabled = true, ConcurrencyLimit = 20 });
            await db.SaveChangesAsync();
            foreach (var id in new[] { expired, consumed, legacy, recent, racing, completing })
                db.QuotaReservations.Add(new CloudQuotaReservation { Id = id, BucketId = bucket, TenantId = tenant.Id, ApplicationId = app.Id, RequestId = Guid.NewGuid(),
                    Amount = 1, ExpiresAt = id == recent ? now.AddMinutes(-2) : now.AddMinutes(-10) });
            await db.SaveChangesAsync();
            foreach (var pair in new[] { (expired, expiredClaim), (consumed, consumedClaim), (legacy, legacyClaim), (recent, recentClaim), (racing, racingClaim), (completing, completingClaim) })
                db.ExecutionClaims.Add(new CloudExecutionClaim { ReservationId = pair.Item1, ClaimId = pair.Item2, TenantId = tenant.Id, ApplicationId = app.Id,
                    DeploymentId = deployment, Region = "region-a", WorkerPayloadFingerprint = pair.Item1 == legacy ? null : new byte[32], ClaimedAt = now.AddMinutes(-20) });
            await db.SaveChangesAsync();
            db.ExecutionConsumptions.Add(new CloudExecutionConsumption { ReservationId = consumed, ClaimId = consumedClaim, DeploymentId = deployment, ConsumedAt = now.AddMinutes(-9) });
            await db.SaveChangesAsync();
        }
        var recovery = new EfCloudUnconsumedClaimRecoveryStore(factory, TimeProvider.System);
        // An older writer holds the reservation lock while its consumption is uncommitted.
        await using (var writer = await factory.CreateDbContextAsync())
        await using (var completionWriter = await factory.CreateDbContextAsync())
        {
            await using var transaction = await writer.Database.BeginTransactionAsync();
            await using var completionTransaction = await completionWriter.Database.BeginTransactionAsync();
            writer.ExecutionConsumptions.Add(new CloudExecutionConsumption { ReservationId = racing, ClaimId = racingClaim, DeploymentId = deployment, ConsumedAt = now });
            await writer.SaveChangesAsync();
            completionWriter.ExecutionCompletions.Add(new CloudExecutionCompletion { ReservationId = completing, ClaimId = completingClaim, Outcome = "completed", CompletedAt = now });
            await completionWriter.SaveChangesAsync();
            var counts = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => recovery.ReleaseAsync(now.AddMinutes(-5), 100, default)));
            Assert.Equal(1, counts.Sum());
            await transaction.CommitAsync();
            await completionTransaction.CommitAsync();
        }
        Assert.Equal(0, await recovery.ReleaseAsync(now.AddMinutes(-5), 100, default));
        await using (var verify = await factory.CreateDbContextAsync())
        {
            Assert.Equal(0, (await verify.QuotaReservations.SingleAsync(x => x.Id == expired)).Actual);
            foreach (var id in new[] { consumed, legacy, recent, racing, completing }) Assert.Null((await verify.QuotaReservations.SingleAsync(x => x.Id == id)).FinalizedAt);
            Assert.Equal(5, (await verify.QuotaBuckets.SingleAsync(x => x.Id == bucket)).Reserved);
            Assert.Equal(0, (await verify.UsageRecords.SingleAsync(x => x.ReservationId == expired)).Quantity);
            Assert.True(await verify.Database.SqlQuery<bool>($"SELECT EXISTS (SELECT 1 FROM cloud.execution_abandonments WHERE \"ReservationId\" = {expired}) AS \"Value\"").SingleAsync());
        }
        // A late old-version consumption insert must be rejected by the database itself.
        await using (var late = await factory.CreateDbContextAsync())
        {
            late.ExecutionConsumptions.Add(new CloudExecutionConsumption { ReservationId = expired, ClaimId = expiredClaim, DeploymentId = deployment, ConsumedAt = now });
            await Assert.ThrowsAsync<DbUpdateException>(() => late.SaveChangesAsync());
        }
        await using (var lateCompletion = await factory.CreateDbContextAsync())
        {
            lateCompletion.ExecutionCompletions.Add(new CloudExecutionCompletion { ReservationId = expired, ClaimId = expiredClaim, Outcome = "completed", CompletedAt = now });
            await Assert.ThrowsAsync<DbUpdateException>(() => lateCompletion.SaveChangesAsync());
        }
        await using (var mutation = await factory.CreateDbContextAsync())
        {
            await Assert.ThrowsAnyAsync<Exception>(() => mutation.Database.ExecuteSqlRawAsync("DELETE FROM cloud.execution_abandonments"));
            await Assert.ThrowsAnyAsync<Exception>(() => mutation.Database.ExecuteSqlRawAsync("TRUNCATE TABLE cloud.execution_abandonments"));
        }

        var waitingReservation = Guid.NewGuid(); var waitingClaim = Guid.NewGuid();
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.QuotaBuckets.Where(x => x.Id == bucket).ExecuteUpdateAsync(x => x.SetProperty(y => y.Reserved, y => y.Reserved + 1));
            db.QuotaReservations.Add(new CloudQuotaReservation { Id = waitingReservation, BucketId = bucket, TenantId = tenant.Id, ApplicationId = app.Id,
                RequestId = Guid.NewGuid(), Amount = 1, ExpiresAt = now.AddMinutes(-10) });
            await db.SaveChangesAsync();
            db.ExecutionClaims.Add(new CloudExecutionClaim { ReservationId = waitingReservation, ClaimId = waitingClaim, TenantId = tenant.Id,
                ApplicationId = app.Id, DeploymentId = deployment, Region = "region-a", WorkerPayloadFingerprint = new byte[32], ClaimedAt = now.AddMinutes(-20) });
            await db.SaveChangesAsync();
        }
        await using (var recovering = await factory.CreateDbContextAsync())
        await using (var waitingWriter = await factory.CreateDbContextAsync())
        {
            await using var transaction = await recovering.Database.BeginTransactionAsync();
            await recovering.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO cloud.execution_abandonments (\"ReservationId\", \"ClaimId\", \"AbandonedAt\", \"Reason\") VALUES ({waitingReservation}, {waitingClaim}, {now}, {"expired_unconsumed"})");
            waitingWriter.ExecutionConsumptions.Add(new CloudExecutionConsumption { ReservationId = waitingReservation, ClaimId = waitingClaim, DeploymentId = deployment, ConsumedAt = now });
            var pending = waitingWriter.SaveChangesAsync();
            Assert.NotSame(pending, await Task.WhenAny(pending, Task.Delay(200)));
            await recovering.QuotaReservations.Where(x => x.Id == waitingReservation).ExecuteUpdateAsync(x => x.SetProperty(y => y.Actual, 0L).SetProperty(y => y.FinalizedAt, now));
            await recovering.QuotaBuckets.Where(x => x.Id == bucket).ExecuteUpdateAsync(x => x.SetProperty(y => y.Reserved, y => y.Reserved - 1));
            await transaction.CommitAsync();
            await Assert.ThrowsAsync<DbUpdateException>(async () => await pending);
        }
    }

    private sealed class TestFactory(DbContextOptions<CloudControlPlaneDbContext> options) : IDbContextFactory<CloudControlPlaneDbContext>
    {
        public CloudControlPlaneDbContext CreateDbContext() => new(options);
    }

    private sealed class CapturingClaimStore : ICloudUnconsumedClaimRecoveryStore
    {
        public TaskCompletionSource<(DateTimeOffset Cutoff, int Limit)> Called { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<int> ReleaseAsync(DateTimeOffset cutoff, int limit, CancellationToken cancellationToken)
        {
            Called.TrySetResult((cutoff, limit));
            return Task.FromResult(0);
        }
    }

    private sealed class CapturingStore : ICloudQuotaRecoveryStore
    {
        public TaskCompletionSource<(DateTimeOffset Cutoff, int Limit)> Called { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<int> ReleaseUnclaimedAsync(DateTimeOffset cutoff, int limit, CancellationToken cancellationToken)
        {
            Called.TrySetResult((cutoff, limit));
            return Task.FromResult(0);
        }
    }
}
