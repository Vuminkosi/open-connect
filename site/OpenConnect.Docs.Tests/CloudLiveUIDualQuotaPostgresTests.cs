using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using OpenConnect.Docs.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

[Collection("Cloud PostgreSQL")]
public sealed class CloudLiveUIDualQuotaPostgresTests
{
    [Fact]
    public async Task RecoveryWorkerUsesFiveMinuteGraceAndScopedStore()
    {
        var now = DateTimeOffset.UtcNow;
        var called = new TaskCompletionSource<(DateTimeOffset Cutoff, int Limit)>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var provider = new ServiceCollection().AddScoped<ICloudLiveUIUnclaimedRecoveryStore>(_ => new RecoveryProbe(called)).BuildServiceProvider();
        using var worker = new CloudLiveUIUnclaimedRecoveryWorker(provider.GetRequiredService<IServiceScopeFactory>(), new FixedClock(now),
            NullLogger<CloudLiveUIUnclaimedRecoveryWorker>.Instance);
        await worker.StartAsync(default);
        var call = await called.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.StopAsync(default);
        Assert.Equal(now.AddMinutes(-5), call.Cutoff);
        Assert.Equal(100, call.Limit);
    }

    [CloudPostgresFact]
    public async Task ExpiredUnclaimedDualReservationsRecoverOnceAndClaimsStayHeld()
    {
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_TEST_DATABASE")).Options;
        IDbContextFactory<CloudControlPlaneDbContext> factory = new Factory(options);
        await using (var db = await factory.CreateDbContextAsync()) await db.Database.MigrateAsync();
        var owner = new CloudSubject("test-issuer", Guid.NewGuid().ToString("N"));
        var tenants = new EfCloudTenantStore(factory, TimeProvider.System);
        var tenant = await tenants.CreateTenantAsync(owner, "LiveUI recovery", default);
        var app = (await tenants.CreateApplicationAsync(owner, tenant.Id, "App", "Development", default)).Application!;
        var now = DateTimeOffset.UtcNow; var planId = Guid.NewGuid(); var tokenId = Guid.NewGuid(); var deploymentId = Guid.NewGuid();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.QuotaBuckets.AddRange(Bucket(planId, tenant.Id, app.Id, "plan", 10, now), Bucket(tokenId, tenant.Id, app.Id, "token", 100, now));
            db.WorkerDeployments.Add(new CloudWorkerDeployment { Id = deploymentId, CapabilityId = "inference.liveui",
                EndpointReference = "fixture-" + Guid.NewGuid().ToString("N"), Transport = "https-mtls", Region = "test",
                ContractVersion = "v1", ModelId = "owner/model", ModelRevision = "revision", ModelDigest = "sha256:" + new string('a', 64),
                ImageDigest = "sha256:" + new string('b', 64), Enabled = true, ConcurrencyLimit = 3 });
            await db.SaveChangesAsync();
        }
        var quota = new CloudLiveUIDualQuotaService(new EfCloudLiveUIDualQuotaStore(factory, TimeProvider.System), TimeProvider.System);
        async Task<Guid> Reserve() => (await quota.ReserveAsync(new(tenant.Id, app.Id, Guid.NewGuid(), "key", new byte[32], 20, now.AddMinutes(5)), default)).ReservationId!.Value;
        var expired = await Reserve(); var claimed = await Reserve(); var fresh = await Reserve(); var racing = await Reserve();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.ExecutionClaims.Add(new CloudExecutionClaim { ReservationId = claimed, TenantId = tenant.Id, ApplicationId = app.Id,
                ClaimId = Guid.NewGuid(), DeploymentId = deploymentId, Region = "test", ClaimedAt = now });
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE cloud.quota_reservations SET "ExpiresAt" = {now.AddMinutes(-10)} WHERE "Id" IN ({expired}, {claimed}, {racing})
                """);
            await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE cloud.liveui_token_reservations SET "Actual" = 0, "FinalizedAt" = {now} WHERE "PlanReservationId" = {claimed}
                """));
            await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE cloud.liveui_token_reservations SET "Actual" = 0, "FinalizedAt" = {now} WHERE "PlanReservationId" = {fresh}
                """));
            await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE cloud.liveui_token_reservations SET "Actual" = 0, "FinalizedAt" = {now} WHERE "PlanReservationId" = {expired}
                """));
        }
        var recovery = new EfCloudLiveUIUnclaimedRecoveryStore(factory, TimeProvider.System);
        await using (var claimant = await factory.CreateDbContextAsync())
        await using (var transaction = await claimant.Database.BeginTransactionAsync())
        {
            claimant.ExecutionClaims.Add(new CloudExecutionClaim { ReservationId = racing, TenantId = tenant.Id, ApplicationId = app.Id,
                ClaimId = Guid.NewGuid(), DeploymentId = deploymentId, Region = "test", ClaimedAt = now });
            await claimant.SaveChangesAsync();
            var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => recovery.ReleaseAsync(now.AddMinutes(-5), 100, default)));
            Assert.Equal(1, results.Sum());
            await transaction.CommitAsync();
        }
        Assert.Equal(0, await recovery.ReleaseAsync(now.AddMinutes(-5), 100, default));
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal(3, (await verify.QuotaBuckets.SingleAsync(x => x.Id == planId)).Reserved);
        Assert.Equal(60, (await verify.QuotaBuckets.SingleAsync(x => x.Id == tokenId)).Reserved);
        Assert.Equal(0, (await verify.QuotaReservations.SingleAsync(x => x.Id == expired)).Actual);
        Assert.Null((await verify.QuotaReservations.SingleAsync(x => x.Id == claimed)).FinalizedAt);
        Assert.Null((await verify.QuotaReservations.SingleAsync(x => x.Id == fresh)).FinalizedAt);
        Assert.Null((await verify.QuotaReservations.SingleAsync(x => x.Id == racing)).FinalizedAt);
        Assert.Equal(0, await verify.Database.SqlQuery<long>($"""
            SELECT "Actual" AS "Value" FROM cloud.liveui_token_reservations WHERE "PlanReservationId" = {expired}
            """).SingleAsync());
        verify.ExecutionClaims.Add(new CloudExecutionClaim { ReservationId = expired, TenantId = tenant.Id, ApplicationId = app.Id,
            ClaimId = Guid.NewGuid(), DeploymentId = deploymentId, Region = "test", ClaimedAt = now });
        await Assert.ThrowsAsync<DbUpdateException>(() => verify.SaveChangesAsync());
    }

    [CloudPostgresFact]
    public async Task PlanAndTokenCapacityAreReservedAtomicallyAndReplaysAreIdempotent()
    {
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_TEST_DATABASE")).Options;
        IDbContextFactory<CloudControlPlaneDbContext> factory = new Factory(options);
        await using (var db = await factory.CreateDbContextAsync()) await db.Database.MigrateAsync();
        var owner = new CloudSubject("test-issuer", Guid.NewGuid().ToString("N"));
        var tenants = new EfCloudTenantStore(factory, TimeProvider.System);
        var tenant = await tenants.CreateTenantAsync(owner, "LiveUI quota", default);
        var app = (await tenants.CreateApplicationAsync(owner, tenant.Id, "App", "Development", default)).Application!;
        var now = DateTimeOffset.UtcNow;
        var expiry = now.AddMinutes(5);
        var planId = Guid.NewGuid(); var tokenId = Guid.NewGuid();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.QuotaBuckets.AddRange(Bucket(planId, tenant.Id, app.Id, "plan", 10, now), Bucket(tokenId, tenant.Id, app.Id, "token", 100, now));
            await db.SaveChangesAsync();
        }
        var service = new CloudLiveUIDualQuotaService(new EfCloudLiveUIDualQuotaStore(factory, TimeProvider.System), TimeProvider.System);
        CloudLiveUIDualQuotaRequest Request(Guid id) => new(tenant.Id, app.Id, id, "key", new byte[32], 20, expiry);
        var requests = Enumerable.Range(0, 20).Select(_ => Request(Guid.NewGuid())).ToArray();
        var results = await Task.WhenAll(requests.Select(request => service.ReserveAsync(request, default)));
        Assert.Equal(5, results.Count(result => result.Status == "reserved"));
        Assert.Equal(15, results.Count(result => result.Status == "quota_exceeded"));
        var first = Array.FindIndex(results, result => result.Status == "reserved");
        Assert.Equal("duplicate", (await service.ReserveAsync(requests[first], default)).Status);
        Assert.Equal("idempotency_conflict", (await service.ReserveAsync(requests[first] with { TokenAmount = 21 }, default)).Status);
        Assert.Equal("idempotency_conflict", (await service.ReserveAsync(requests[first] with { RequestFingerprint = Enumerable.Repeat((byte)1, 32).ToArray() }, default)).Status);
        var advanced = new CloudLiveUIDualQuotaService(new EfCloudLiveUIDualQuotaStore(factory, new FixedClock(now.AddMinutes(10))), TimeProvider.System);
        Assert.Equal("invalid_request", (await advanced.ReserveAsync(Request(Guid.NewGuid()), default)).Status);
        await using var verify = await factory.CreateDbContextAsync();
        var plan = await verify.QuotaBuckets.SingleAsync(item => item.Id == planId);
        var tokens = await verify.QuotaBuckets.SingleAsync(item => item.Id == tokenId);
        Assert.Equal(5, plan.Reserved); Assert.Equal(100, tokens.Reserved);
        Assert.Equal(5, await verify.QuotaReservations.CountAsync(item => item.TenantId == tenant.Id && item.ApplicationId == app.Id));
        Assert.Equal(5, await verify.Database.SqlQuery<int>($"SELECT count(*)::integer AS \"Value\" FROM cloud.liveui_token_reservations WHERE \"TenantId\" = {tenant.Id} AND \"ApplicationId\" = {app.Id}").SingleAsync());
        var reservationId = results[first].ReservationId!.Value;
        await Assert.ThrowsAsync<PostgresException>(() => verify.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO cloud.liveui_token_reservations ("PlanReservationId", "TenantId", "ApplicationId", "TokenBucketId", "Amount")
            VALUES ({Guid.NewGuid()}, {tenant.Id}, {app.Id}, {tokenId}, {20})
            """));
        await Assert.ThrowsAsync<PostgresException>(() => verify.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE cloud.liveui_token_reservations SET "TokenBucketId" = {planId} WHERE "PlanReservationId" = {reservationId}
            """));
        await Assert.ThrowsAsync<PostgresException>(() => verify.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM cloud.liveui_token_reservations WHERE "PlanReservationId" = {reservationId}
            """));
        var legacyRequest = Request(Guid.NewGuid());
        var legacy = new CloudQuotaService(new EfCloudQuotaStore(factory), TimeProvider.System);
        var legacyReservation = await legacy.ReserveAsync(new(tenant.Id, app.Id, "inference.liveui", "plan",
            legacyRequest.RequestId, "key", legacyRequest.RequestFingerprint, 1, expiry), default);
        Assert.Equal("reserved", legacyReservation.Status);
        await Assert.ThrowsAsync<PostgresException>(() => verify.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO cloud.liveui_token_reservations ("PlanReservationId", "TenantId", "ApplicationId", "TokenBucketId", "Amount")
            VALUES ({legacyReservation.ReservationId!.Value}, {tenant.Id}, {app.Id}, {planId}, {20})
            """));
        await Assert.ThrowsAsync<PostgresException>(() => verify.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO cloud.liveui_token_reservations ("PlanReservationId", "TenantId", "ApplicationId", "TokenBucketId", "Amount", "Actual", "FinalizedAt")
            VALUES ({legacyReservation.ReservationId!.Value}, {tenant.Id}, {app.Id}, {tokenId}, {20}, {0}, {now})
            """));
        var otherTenant = await tenants.CreateTenantAsync(owner, "Other tenant", default);
        var otherApp = (await tenants.CreateApplicationAsync(owner, otherTenant.Id, "Other app", "Development", default)).Application!;
        var otherToken = Guid.NewGuid();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.QuotaBuckets.Add(Bucket(otherToken, otherTenant.Id, otherApp.Id, "token", 100, now));
            await db.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<PostgresException>(() => verify.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO cloud.liveui_token_reservations ("PlanReservationId", "TenantId", "ApplicationId", "TokenBucketId", "Amount")
            VALUES ({legacyReservation.ReservationId!.Value}, {tenant.Id}, {app.Id}, {otherToken}, {20})
            """));
    }

    [CloudPostgresFact]
    public async Task ConcurrentIdenticalRequestsCreateOneDualReservation()
    {
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_TEST_DATABASE")).Options;
        IDbContextFactory<CloudControlPlaneDbContext> factory = new Factory(options);
        await using (var db = await factory.CreateDbContextAsync()) await db.Database.MigrateAsync();
        var owner = new CloudSubject("test-issuer", Guid.NewGuid().ToString("N"));
        var tenants = new EfCloudTenantStore(factory, TimeProvider.System);
        var tenant = await tenants.CreateTenantAsync(owner, "LiveUI duplicate quota", default);
        var app = (await tenants.CreateApplicationAsync(owner, tenant.Id, "App", "Development", default)).Application!;
        var now = DateTimeOffset.UtcNow; var planId = Guid.NewGuid(); var tokenId = Guid.NewGuid();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.QuotaBuckets.AddRange(Bucket(planId, tenant.Id, app.Id, "plan", 10, now), Bucket(tokenId, tenant.Id, app.Id, "token", 100, now));
            await db.SaveChangesAsync();
        }
        var service = new CloudLiveUIDualQuotaService(new EfCloudLiveUIDualQuotaStore(factory, TimeProvider.System), TimeProvider.System);
        var request = new CloudLiveUIDualQuotaRequest(tenant.Id, app.Id, Guid.NewGuid(), "key", new byte[32], 20, now.AddMinutes(5));
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => service.ReserveAsync(request, default)));
        Assert.Single(outcomes, item => item.Status == "reserved");
        Assert.Equal(11, outcomes.Count(item => item.Status == "duplicate"));
        Assert.Single(outcomes.Select(item => item.ReservationId).Distinct());
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal(1, (await verify.QuotaBuckets.SingleAsync(item => item.Id == planId)).Reserved);
        Assert.Equal(20, (await verify.QuotaBuckets.SingleAsync(item => item.Id == tokenId)).Reserved);
    }

    [CloudPostgresFact]
    public async Task MissingTokenBucketAndLegacyReservationsNeverDebitAPlanTwice()
    {
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_TEST_DATABASE")).Options;
        IDbContextFactory<CloudControlPlaneDbContext> factory = new Factory(options);
        await using (var db = await factory.CreateDbContextAsync()) await db.Database.MigrateAsync();
        var owner = new CloudSubject("test-issuer", Guid.NewGuid().ToString("N"));
        var tenants = new EfCloudTenantStore(factory, TimeProvider.System);
        var tenant = await tenants.CreateTenantAsync(owner, "LiveUI legacy quota", default);
        var app = (await tenants.CreateApplicationAsync(owner, tenant.Id, "App", "Development", default)).Application!;
        var now = DateTimeOffset.UtcNow; var expiry = now.AddMinutes(5); var bucketId = Guid.NewGuid();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.QuotaBuckets.Add(Bucket(bucketId, tenant.Id, app.Id, "plan", 10, now)); await db.SaveChangesAsync();
        }
        var dual = new CloudLiveUIDualQuotaService(new EfCloudLiveUIDualQuotaStore(factory, TimeProvider.System), TimeProvider.System);
        var request = new CloudLiveUIDualQuotaRequest(tenant.Id, app.Id, Guid.NewGuid(), "key", new byte[32], 20, expiry);
        Assert.Equal("quota_unavailable", (await dual.ReserveAsync(request, default)).Status);
        await using (var db = await factory.CreateDbContextAsync())
            Assert.Equal(0, (await db.QuotaBuckets.SingleAsync(item => item.Id == bucketId)).Reserved);
        var legacy = new CloudQuotaService(new EfCloudQuotaStore(factory), TimeProvider.System);
        Assert.Equal("reserved", (await legacy.ReserveAsync(new(tenant.Id, app.Id, "inference.liveui", "plan",
            request.RequestId, request.FingerprintKeyId, request.RequestFingerprint, 1, expiry), default)).Status);
        Assert.Equal("quota_unavailable", (await dual.ReserveAsync(request, default)).Status);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal(1, (await verify.QuotaBuckets.SingleAsync(item => item.Id == bucketId)).Reserved);
        Assert.Equal(1, await verify.QuotaReservations.CountAsync(item => item.TenantId == tenant.Id && item.ApplicationId == app.Id));
    }

    private static CloudQuotaBucket Bucket(Guid id, Guid tenant, Guid application, string unit, long limit, DateTimeOffset now) =>
        new() { Id = id, TenantId = tenant, ApplicationId = application, CapabilityId = "inference.liveui", Unit = unit,
            PeriodStart = now.AddMinutes(-1), PeriodEnd = now.AddHours(1), Limit = limit };

    private sealed class Factory(DbContextOptions<CloudControlPlaneDbContext> options) : IDbContextFactory<CloudControlPlaneDbContext>
    {
        public CloudControlPlaneDbContext CreateDbContext() => new(options);
    }

    private sealed class FixedClock(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class RecoveryProbe(TaskCompletionSource<(DateTimeOffset Cutoff, int Limit)> called) : ICloudLiveUIUnclaimedRecoveryStore
    {
        public Task<int> ReleaseAsync(DateTimeOffset cutoff, int limit, CancellationToken cancellationToken)
        {
            called.TrySetResult((cutoff, limit));
            return Task.FromResult(0);
        }
    }
}
