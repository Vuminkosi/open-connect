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

    private sealed class TestFactory(DbContextOptions<CloudControlPlaneDbContext> options) : IDbContextFactory<CloudControlPlaneDbContext>
    {
        public CloudControlPlaneDbContext CreateDbContext() => new(options);
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
