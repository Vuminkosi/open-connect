using Microsoft.EntityFrameworkCore;
using OpenConnect.Docs.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

[Collection("Cloud PostgreSQL")]
public sealed class CloudQuotaPostgresTests
{
    private static CancellationToken None => CancellationToken.None;
    [CloudPostgresFact]
    public async Task ReservationsAreAtomicAndIdempotent()
    {
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_TEST_DATABASE")).Options;
        IDbContextFactory<CloudControlPlaneDbContext> factory = new TestFactory(options);
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.MigrateAsync();
        }
        var owner = new CloudSubject("test-issuer", Guid.NewGuid().ToString("N"));
        var tenants = new EfCloudTenantStore(factory, TimeProvider.System);
        var tenant = await tenants.CreateTenantAsync(owner, "Quota test", None);
        var app = (await tenants.CreateApplicationAsync(owner, tenant.Id, "App", "Development", None)).Application!;
        var capability = "test." + Guid.NewGuid().ToString("N");
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Capabilities.Add(new CloudCapabilityDefinition { Id = capability, Name = "Test", Description = "Test", ContractVersion = "1", BillingUnit = "request", PlatformAvailability = "available", Dependencies = "[]", Modalities = "[]", AllowedRegions = "[]" });
            await db.SaveChangesAsync();
            await db.Database.MigrateAsync();
        }
        var now = DateTimeOffset.UtcNow;
        var expiry = now.AddMinutes(5);
        var bucketId = Guid.NewGuid();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.QuotaBuckets.Add(new CloudQuotaBucket { Id = bucketId, TenantId = tenant.Id, ApplicationId = app.Id, CapabilityId = capability,
                Unit = "request", PeriodStart = now.AddHours(-1), PeriodEnd = now.AddHours(1), Limit = 10 });
            await db.SaveChangesAsync();
        }
        var otherTenant = await tenants.CreateTenantAsync(owner, "Other quota tenant", None);
        var otherApp = (await tenants.CreateApplicationAsync(owner, otherTenant.Id, "Other app", "Development", None)).Application!;
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.QuotaReservations.Add(new CloudQuotaReservation { Id = Guid.NewGuid(), BucketId = bucketId, TenantId = otherTenant.Id,
                ApplicationId = otherApp.Id, RequestId = Guid.NewGuid(), Amount = 1, ExpiresAt = expiry });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        var service = new CloudQuotaService(new EfCloudQuotaStore(factory), TimeProvider.System);
        CloudQuotaRequest Request(Guid id, long amount = 1) => new(tenant.Id, app.Id, capability, "request", id, "key_1", new byte[32], amount, expiry);
        var attempts = await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => service.ReserveAsync(Request(Guid.NewGuid()), None)));
        var accepted = attempts.Where(x => x.Status == "reserved").ToArray();
        Assert.Equal(10, accepted.Length);
        Assert.Equal(90, attempts.Count(x => x.Status == "quota_exceeded"));
        Assert.All(accepted, x => Assert.NotNull(x.ReservationId));
        var id = accepted[0].ReservationId!.Value;
        await using (var db = await factory.CreateDbContextAsync())
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            await db.QuotaReservations.Where(x => x.Id == id).ExecuteUpdateAsync(x =>
                x.SetProperty(y => y.Actual, 0L).SetProperty(y => y.FinalizedAt, DateTimeOffset.UtcNow));
            Assert.True(await db.UsageRecords.AnyAsync(x => x.ReservationId == id));
            await transaction.RollbackAsync();
        }
        await using (var db = await factory.CreateDbContextAsync())
        {
            Assert.False(await db.UsageRecords.AnyAsync(x => x.ReservationId == id));
            Assert.Null((await db.QuotaReservations.SingleAsync(x => x.Id == id)).FinalizedAt);
        }
        Assert.Equal("not_found", (await service.FinalizeAsync(Guid.NewGuid(), app.Id, id, 0, None)).Status);
        Assert.Equal("overage_requires_reconciliation", (await service.FinalizeAsync(tenant.Id, app.Id, id, 2, None)).Status);
        var finalized = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => service.FinalizeAsync(tenant.Id, app.Id, id, 0, None)));
        Assert.Single(finalized, x => x.Status == "finalized");
        Assert.Equal(7, finalized.Count(x => x.Status == "already_finalized"));
        Assert.Equal("idempotency_conflict", (await service.FinalizeAsync(tenant.Id, app.Id, id, 1, None)).Status);
        var replayRequest = Request(Guid.NewGuid());
        var duplicates = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => service.ReserveAsync(replayRequest, None)));
        Assert.Single(duplicates, x => x.Status == "reserved");
        Assert.Equal(11, duplicates.Count(x => x.Status == "duplicate"));
        Assert.Equal("found", (await service.FindFingerprintKeyAsync(tenant.Id, app.Id, capability, replayRequest.RequestId, None)).Status);
        Assert.Equal("key_1", (await service.FindFingerprintKeyAsync(tenant.Id, app.Id, capability, replayRequest.RequestId, None)).KeyId);
        Assert.Equal("not_found", (await service.FindFingerprintKeyAsync(Guid.NewGuid(), app.Id, capability, replayRequest.RequestId, None)).Status);
        Assert.Equal("not_found", (await service.FindFingerprintKeyAsync(tenant.Id, app.Id, "other", replayRequest.RequestId, None)).Status);
        Assert.Equal("idempotency_conflict", (await service.ReserveAsync(replayRequest with { FingerprintKeyId = "key_2" }, None)).Status);
        Assert.Equal("idempotency_conflict", (await service.ReserveAsync(replayRequest with { Amount = 2 }, None)).Status);
        Assert.Equal("idempotency_conflict", (await service.ReserveAsync(replayRequest with { RequestFingerprint = Enumerable.Repeat((byte)1, 32).ToArray() }, None)).Status);
        Assert.Equal("invalid_request", (await service.ReserveAsync(replayRequest with { RequestId = Guid.NewGuid(), RequestFingerprint = new byte[31] }, None)).Status);
        var chargeId = accepted[1].ReservationId!.Value;
        Assert.Equal("finalized", (await service.FinalizeAsync(tenant.Id, app.Id, chargeId, 1, None)).Status);
        Assert.Equal("already_finalized", (await service.FinalizeAsync(tenant.Id, app.Id, chargeId, 1, None)).Status);
        var afterExpiry = new CloudQuotaService(new EfCloudQuotaStore(factory), new TestClock(expiry.AddSeconds(1)));
        Assert.Equal("quota_exceeded", (await afterExpiry.ReserveAsync(Request(Guid.NewGuid()) with { ExpiresAt = expiry.AddMinutes(5) }, None)).Status);
        await using var verify = await factory.CreateDbContextAsync();
        var bucket = await verify.QuotaBuckets.AsNoTracking().SingleAsync(x => x.Id == bucketId);
        Assert.Equal(9, bucket.Reserved);
        Assert.Equal(1, bucket.Consumed);
        Assert.Equal(11, await verify.QuotaReservations.CountAsync(x => x.BucketId == bucketId));
        var usage = await verify.UsageRecords.AsNoTracking().Where(x => x.TenantId == tenant.Id && x.ApplicationId == app.Id).ToListAsync();
        Assert.Equal(2, usage.Count);
        Assert.Contains(usage, x => x.ReservationId == chargeId && x.Quantity == 1 && x.CapabilityId == capability && x.Unit == "request" && x.Provenance == "reservation");
        await Assert.ThrowsAnyAsync<Exception>(() => verify.UsageRecords.Where(x => x.ReservationId == chargeId).ExecuteUpdateAsync(x => x.SetProperty(y => y.Quantity, 0)));
        await Assert.ThrowsAnyAsync<Exception>(() => verify.UsageRecords.Where(x => x.ReservationId == chargeId).ExecuteDeleteAsync());
        await Assert.ThrowsAnyAsync<Exception>(() => verify.Database.ExecuteSqlRawAsync("TRUNCATE TABLE cloud.usage_records"));
        await Assert.ThrowsAnyAsync<Exception>(() => verify.QuotaReservations.Where(x => x.Id == chargeId).ExecuteDeleteAsync());
    }

    [CloudPostgresFact]
    public async Task LegacyReservationCannotBeReplayed()
    {
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_TEST_DATABASE")).Options;
        IDbContextFactory<CloudControlPlaneDbContext> factory = new TestFactory(options);
        await using (var db = await factory.CreateDbContextAsync())
            await db.Database.MigrateAsync();
        var owner = new CloudSubject("test-issuer", Guid.NewGuid().ToString("N"));
        var tenants = new EfCloudTenantStore(factory, TimeProvider.System);
        var tenant = await tenants.CreateTenantAsync(owner, "Legacy quota", None);
        var app = (await tenants.CreateApplicationAsync(owner, tenant.Id, "App", "Development", None)).Application!;
        var capability = "test." + Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        var rawExpiry = now.AddMinutes(5);
        var expiry = new DateTimeOffset(rawExpiry.Ticks - rawExpiry.Ticks % 10, TimeSpan.Zero);
        var bucketId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Capabilities.Add(new CloudCapabilityDefinition { Id = capability, Name = "Test", Description = "Test", ContractVersion = "1", BillingUnit = "request", PlatformAvailability = "available", Dependencies = "[]", Modalities = "[]", AllowedRegions = "[]" });
            db.QuotaBuckets.Add(new CloudQuotaBucket { Id = bucketId, TenantId = tenant.Id, ApplicationId = app.Id, CapabilityId = capability,
                Unit = "request", PeriodStart = now.AddHours(-1), PeriodEnd = now.AddHours(1), Limit = 10, Reserved = 1 });
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO cloud.quota_reservations (\"Id\", \"BucketId\", \"TenantId\", \"ApplicationId\", \"RequestId\", \"Amount\", \"ExpiresAt\") VALUES ({reservationId}, {bucketId}, {tenant.Id}, {app.Id}, {requestId}, {1L}, {expiry})");
            await db.Database.MigrateAsync();
        }
        var service = new CloudQuotaService(new EfCloudQuotaStore(factory), TimeProvider.System);
        var retry = new CloudQuotaRequest(tenant.Id, app.Id, capability, "request", requestId, "key_1", new byte[32], 1, expiry);
        Assert.Equal("legacy_unverifiable", (await service.FindFingerprintKeyAsync(tenant.Id, app.Id, capability, requestId, None)).Status);
        Assert.Equal("idempotency_conflict", (await service.ReserveAsync(retry, None)).Status);
        await using (var db = await factory.CreateDbContextAsync())
            await db.QuotaReservations.Where(x => x.Id == reservationId)
                .ExecuteUpdateAsync(x => x.SetProperty(y => y.RequestFingerprint, new byte[32]));
        Assert.Equal("legacy_unverifiable", (await service.FindFingerprintKeyAsync(tenant.Id, app.Id, capability, requestId, None)).Status);
        Assert.Equal("idempotency_conflict", (await service.ReserveAsync(retry, None)).Status);
        Assert.Equal("finalized", (await service.FinalizeAsync(tenant.Id, app.Id, reservationId, 0, None)).Status);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal(0, (await verify.QuotaBuckets.SingleAsync(x => x.Id == bucketId)).Reserved);
    }

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TestFactory(DbContextOptions<CloudControlPlaneDbContext> options) : IDbContextFactory<CloudControlPlaneDbContext>
    {
        public CloudControlPlaneDbContext CreateDbContext() => new(options);
    }
}
