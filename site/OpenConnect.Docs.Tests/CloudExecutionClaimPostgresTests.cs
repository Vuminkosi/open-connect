using Microsoft.EntityFrameworkCore;
using OpenConnect.Docs.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

[Collection("Cloud PostgreSQL")]
public sealed class CloudExecutionClaimPostgresTests
{
    [CloudPostgresFact]
    public async Task ClaimsAreSingleUseScopedAndCannotRaceARefund()
    {
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>().UseNpgsql(Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_TEST_DATABASE")).Options;
        IDbContextFactory<CloudControlPlaneDbContext> factory = new TestFactory(options);
        await using (var db = await factory.CreateDbContextAsync()) await db.Database.MigrateAsync();
        var owner = new CloudSubject("test-issuer", Guid.NewGuid().ToString("N"));
        var tenants = new EfCloudTenantStore(factory, TimeProvider.System);
        var tenant = await tenants.CreateTenantAsync(owner, "Execution test", default);
        var app = (await tenants.CreateApplicationAsync(owner, tenant.Id, "App", "Development", default)).Application!;
        var capability = "claim." + Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Capabilities.Add(new CloudCapabilityDefinition { Id = capability, Name = "Test", Description = "Test", ContractVersion = "1", BillingUnit = "request", PlatformAvailability = "available", Dependencies = "[]", Modalities = "[]", AllowedRegions = "[]" });
            db.QuotaBuckets.Add(new CloudQuotaBucket { Id = Guid.NewGuid(), TenantId = tenant.Id, ApplicationId = app.Id, CapabilityId = capability, Unit = "request", PeriodStart = now.AddMinutes(-1), PeriodEnd = now.AddHours(1), Limit = 10 });
            await db.SaveChangesAsync();
        }
        var quota = new CloudQuotaService(new EfCloudQuotaStore(factory), TimeProvider.System);
        var claims = new CloudExecutionClaimService(new EfCloudExecutionClaimStore(factory, TimeProvider.System));
        async Task<Guid> Reserve()
        {
            var result = await quota.ReserveAsync(new CloudQuotaRequest(tenant.Id, app.Id, capability, "request", Guid.NewGuid(), "key", new byte[32], 1, now.AddMinutes(5)), default);
            Assert.Equal("reserved", result.Status);
            return result.ReservationId!.Value;
        }
        var id = await Reserve();
        Assert.Equal("unavailable", (await claims.ClaimAsync(Guid.NewGuid(), app.Id, id, capability, "key", new byte[32], default)).Status);
        Assert.Equal("unavailable", (await claims.ClaimAsync(tenant.Id, Guid.NewGuid(), id, capability, "key", new byte[32], default)).Status);
        Assert.Equal("unavailable", (await claims.ClaimAsync(tenant.Id, app.Id, id, "other", "key", new byte[32], default)).Status);
        Assert.Equal("idempotency_conflict", (await claims.ClaimAsync(tenant.Id, app.Id, id, capability, "other-key", new byte[32], default)).Status);
        Assert.Equal("idempotency_conflict", (await claims.ClaimAsync(tenant.Id, app.Id, id, capability, "key", Enumerable.Repeat((byte)1, 32).ToArray(), default)).Status);
        var attempts = await Task.WhenAll(Enumerable.Range(0, 30).Select(_ => claims.ClaimAsync(tenant.Id, app.Id, id, capability, "key", new byte[32], default)));
        Assert.Single(attempts, x => x.Status == "claimed" && x.ClaimId is not null);
        Assert.Equal(29, attempts.Count(x => x.Status == "already_claimed" && x.ClaimId is null));
        Assert.Equal("execution_requires_reconciliation", (await quota.FinalizeAsync(tenant.Id, app.Id, id, 0, default)).Status);
        await using (var db = await factory.CreateDbContextAsync())
        {
            await Assert.ThrowsAnyAsync<Exception>(() => db.QuotaReservations.Where(x => x.Id == id).ExecuteUpdateAsync(x => x.SetProperty(y => y.Actual, 0L).SetProperty(y => y.FinalizedAt, DateTimeOffset.UtcNow)));
            Assert.Single(await db.ExecutionClaims.Where(x => x.ReservationId == id).ToListAsync());
            await Assert.ThrowsAnyAsync<Exception>(() => db.ExecutionClaims.Where(x => x.ReservationId == id).ExecuteDeleteAsync());
            await Assert.ThrowsAnyAsync<Exception>(() => db.ExecutionClaims.Where(x => x.ReservationId == id).ExecuteUpdateAsync(x => x.SetProperty(y => y.ClaimId, Guid.NewGuid())));
            await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE cloud.execution_claims"));
        }
        var expired = await Reserve();
        var expiredClaims = new CloudExecutionClaimService(new EfCloudExecutionClaimStore(factory, new TestClock(now.AddMinutes(6))));
        Assert.Equal("unavailable", (await expiredClaims.ClaimAsync(tenant.Id, app.Id, expired, capability, "key", new byte[32], default)).Status);
        var legacy = await Reserve();
        await using (var db = await factory.CreateDbContextAsync())
            await db.QuotaReservations.Where(x => x.Id == legacy).ExecuteUpdateAsync(x => x.SetProperty(y => y.RequestFingerprint, (byte[]?)null).SetProperty(y => y.FingerprintKeyId, (string?)null));
        Assert.Equal("unavailable", (await claims.ClaimAsync(tenant.Id, app.Id, legacy, capability, "key", new byte[32], default)).Status);
        var finalized = await Reserve();
        Assert.Equal("finalized", (await quota.FinalizeAsync(tenant.Id, app.Id, finalized, 0, default)).Status);
        Assert.Equal("unavailable", (await claims.ClaimAsync(tenant.Id, app.Id, finalized, capability, "key", new byte[32], default)).Status);
        var racing = await Reserve();
        var claimTask = claims.ClaimAsync(tenant.Id, app.Id, racing, capability, "key", new byte[32], default);
        var refundTask = quota.FinalizeAsync(tenant.Id, app.Id, racing, 0, default);
        await Task.WhenAll(claimTask, refundTask);
        Assert.True((claimTask.Result.Status == "claimed" && refundTask.Result.Status == "execution_requires_reconciliation") ||
            (claimTask.Result.Status == "unavailable" && refundTask.Result.Status == "finalized"));
        var legacyRace = await Reserve();
        await using (var claimant = await factory.CreateDbContextAsync())
        await using (var oldWriter = await factory.CreateDbContextAsync())
        await using (var observer = await factory.CreateDbContextAsync())
        {
            await using var transaction = await claimant.Database.BeginTransactionAsync();
            await claimant.QuotaReservations.FromSqlInterpolated($"SELECT * FROM cloud.quota_reservations WHERE \"Id\" = {legacyRace} FOR UPDATE").AsNoTracking().SingleAsync();
            claimant.ExecutionClaims.Add(new CloudExecutionClaim { ReservationId = legacyRace, TenantId = tenant.Id, ApplicationId = app.Id, ClaimId = Guid.NewGuid(), ClaimedAt = DateTimeOffset.UtcNow });
            await claimant.SaveChangesAsync();
            await oldWriter.Database.OpenConnectionAsync();
            var pid = await oldWriter.Database.SqlQueryRaw<int>("SELECT pg_backend_pid() AS \"Value\"").SingleAsync();
            var oldWrite = oldWriter.QuotaReservations.Where(x => x.Id == legacyRace).ExecuteUpdateAsync(x => x.SetProperty(y => y.Actual, 0L).SetProperty(y => y.FinalizedAt, DateTimeOffset.UtcNow));
            var waiting = false;
            for (var attempt = 0; attempt < 50 && !waiting; attempt++)
            {
                waiting = await observer.Database.SqlQuery<bool>($"SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE pid = {pid} AND wait_event_type = 'Lock') AS \"Value\"").SingleAsync();
                if (!waiting) await Task.Delay(20);
            }
            Assert.True(waiting);
            await transaction.CommitAsync();
            await Assert.ThrowsAnyAsync<Exception>(async () => await oldWrite);
            Assert.Null((await observer.QuotaReservations.AsNoTracking().SingleAsync(x => x.Id == legacyRace)).FinalizedAt);
        }
    }

    private sealed class TestClock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
    private sealed class TestFactory(DbContextOptions<CloudControlPlaneDbContext> options) : IDbContextFactory<CloudControlPlaneDbContext>
    {
        public CloudControlPlaneDbContext CreateDbContext() => new(options);
    }
}
