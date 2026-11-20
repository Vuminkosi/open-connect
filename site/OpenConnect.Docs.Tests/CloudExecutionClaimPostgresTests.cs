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
        var credentialId = Guid.NewGuid();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Capabilities.Add(new CloudCapabilityDefinition { Id = capability, Name = "Test", Description = "Test", ContractVersion = "1", BillingUnit = "request", PlatformAvailability = "available", Dependencies = "[]", Modalities = "[]", AllowedRegions = "[]" });
            db.TenantEntitlements.Add(new CloudTenantEntitlement { TenantId = tenant.Id, CapabilityId = capability, PlanGranted = true, Revision = 1 });
            db.TenantCapabilitySelections.Add(new CloudTenantCapabilitySelection { TenantId = tenant.Id, CapabilityId = capability, Enabled = true, Revision = 1 });
            db.ApplicationCapabilitySelections.Add(new CloudApplicationCapabilitySelection { TenantId = tenant.Id, ApplicationId = app.Id, CapabilityId = capability, Enabled = true, Revision = 1 });
            db.Credentials.Add(new CloudCredential { Id = credentialId, TenantId = tenant.Id, ApplicationId = app.Id, Environment = "Development", KeyHash = new byte[32], Fingerprint = "claim-test", Scopes = System.Text.Json.JsonSerializer.Serialize(new[] { capability }), IssuedAt = now, ExpiresAt = now.AddHours(1) });
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
        await using (var disabler = await factory.CreateDbContextAsync())
        {
            await using var transaction = await disabler.Database.BeginTransactionAsync();
            await disabler.ApplicationCapabilitySelections.Where(x => x.TenantId == tenant.Id && x.ApplicationId == app.Id && x.CapabilityId == capability)
                .ExecuteUpdateAsync(x => x.SetProperty(y => y.Enabled, false));
            var waitingClaim = claims.ClaimAsync(credentialId, tenant.Id, app.Id, id, capability, "key", new byte[32], null, default);
            await Task.Delay(100);
            Assert.False(waitingClaim.IsCompleted);
            await transaction.CommitAsync();
            Assert.Equal("unavailable", (await waitingClaim).Status);
        }
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.ApplicationCapabilitySelections.Where(x => x.TenantId == tenant.Id && x.ApplicationId == app.Id && x.CapabilityId == capability).ExecuteUpdateAsync(x => x.SetProperty(y => y.Enabled, true));
            await db.TenantEntitlements.Where(x => x.TenantId == tenant.Id && x.CapabilityId == capability).ExecuteUpdateAsync(x => x.SetProperty(y => y.PlanGranted, false));
        }
        Assert.Equal("unavailable", (await claims.ClaimAsync(credentialId, tenant.Id, app.Id, id, capability, "key", new byte[32], null, default)).Status);
        await using (var db = await factory.CreateDbContextAsync())
            await db.TenantEntitlements.Where(x => x.TenantId == tenant.Id && x.CapabilityId == capability).ExecuteUpdateAsync(x => x.SetProperty(y => y.PlanGranted, true));
        await using (var db = await factory.CreateDbContextAsync())
            await db.Capabilities.Where(x => x.Id == capability).ExecuteUpdateAsync(x => x.SetProperty(y => y.Dependencies, "[\"inference.chat\"]"));
        Assert.Equal("unavailable", (await claims.ClaimAsync(credentialId, tenant.Id, app.Id, id, capability, "key", new byte[32], null, default)).Status);
        await using (var db = await factory.CreateDbContextAsync())
            await db.Capabilities.Where(x => x.Id == capability).ExecuteUpdateAsync(x => x.SetProperty(y => y.Dependencies, "[]"));
        Assert.Equal("unavailable", (await claims.ClaimAsync(credentialId, Guid.NewGuid(), app.Id, id, capability, "key", new byte[32], null, default)).Status);
        Assert.Equal("unavailable", (await claims.ClaimAsync(credentialId, tenant.Id, Guid.NewGuid(), id, capability, "key", new byte[32], null, default)).Status);
        Assert.Equal("unavailable", (await claims.ClaimAsync(credentialId, tenant.Id, app.Id, id, "other", "key", new byte[32], null, default)).Status);
        Assert.Equal("idempotency_conflict", (await claims.ClaimAsync(credentialId, tenant.Id, app.Id, id, capability, "other-key", new byte[32], null, default)).Status);
        Assert.Equal("idempotency_conflict", (await claims.ClaimAsync(credentialId, tenant.Id, app.Id, id, capability, "key", Enumerable.Repeat((byte)1, 32).ToArray(), null, default)).Status);
        var attempts = await Task.WhenAll(Enumerable.Range(0, 30).Select(_ => claims.ClaimAsync(credentialId, tenant.Id, app.Id, id, capability, "key", new byte[32], null, default)));
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
        Assert.Equal("unavailable", (await expiredClaims.ClaimAsync(credentialId, tenant.Id, app.Id, expired, capability, "key", new byte[32], null, default)).Status);
        var legacy = await Reserve();
        await using (var db = await factory.CreateDbContextAsync())
            await db.QuotaReservations.Where(x => x.Id == legacy).ExecuteUpdateAsync(x => x.SetProperty(y => y.RequestFingerprint, (byte[]?)null).SetProperty(y => y.FingerprintKeyId, (string?)null));
        Assert.Equal("unavailable", (await claims.ClaimAsync(credentialId, tenant.Id, app.Id, legacy, capability, "key", new byte[32], null, default)).Status);
        var finalized = await Reserve();
        Assert.Equal("finalized", (await quota.FinalizeAsync(tenant.Id, app.Id, finalized, 0, default)).Status);
        Assert.Equal("unavailable", (await claims.ClaimAsync(credentialId, tenant.Id, app.Id, finalized, capability, "key", new byte[32], null, default)).Status);
        var racing = await Reserve();
        var claimTask = claims.ClaimAsync(credentialId, tenant.Id, app.Id, racing, capability, "key", new byte[32], null, default);
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
        var revokedRace = await Reserve();
        await using (var revoker = await factory.CreateDbContextAsync())
        {
            await using var transaction = await revoker.Database.BeginTransactionAsync();
            await revoker.Credentials.Where(x => x.Id == credentialId).ExecuteUpdateAsync(x => x.SetProperty(y => y.RevokedAt, DateTimeOffset.UtcNow));
            var duringRevocation = claims.ClaimAsync(credentialId, tenant.Id, app.Id, revokedRace, capability, "key", new byte[32], null, default);
            await Task.Delay(100);
            Assert.False(duringRevocation.IsCompleted);
            await transaction.CommitAsync();
            Assert.Equal("unavailable", (await duringRevocation).Status);
        }
        await using (var db = await factory.CreateDbContextAsync())
            Assert.False(await db.ExecutionClaims.AnyAsync(x => x.ReservationId == revokedRace));
        var secondCredential = Guid.NewGuid();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Credentials.Add(new CloudCredential { Id = secondCredential, TenantId = tenant.Id, ApplicationId = app.Id, Environment = "Development", KeyHash = new byte[32], Fingerprint = "claim-test-2", Scopes = System.Text.Json.JsonSerializer.Serialize(new[] { capability }), IssuedAt = now, ExpiresAt = now.AddHours(1) });
            await db.SaveChangesAsync();
        }
        var claimFirst = await Reserve();
        await using (var locker = await factory.CreateDbContextAsync())
        await using (var revoker = await factory.CreateDbContextAsync())
        await using (var disabler = await factory.CreateDbContextAsync())
        await using (var observer = await factory.CreateDbContextAsync())
        {
            await using var transaction = await locker.Database.BeginTransactionAsync();
            await locker.QuotaReservations.FromSqlInterpolated($"SELECT * FROM cloud.quota_reservations WHERE \"Id\" = {claimFirst} FOR UPDATE").AsNoTracking().SingleAsync();
            var pendingClaim = claims.ClaimAsync(secondCredential, tenant.Id, app.Id, claimFirst, capability, "key", new byte[32], null, default);
            var claimWaiting = false;
            for (var attempt = 0; attempt < 50 && !claimWaiting; attempt++)
            {
                claimWaiting = await observer.Database.SqlQueryRaw<bool>("SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE wait_event_type = 'Lock' AND query LIKE '%cloud.quota_reservations%FOR UPDATE%') AS \"Value\"").SingleAsync();
                if (!claimWaiting) await Task.Delay(20);
            }
            Assert.True(claimWaiting);
            var pendingRevocation = revoker.Credentials.Where(x => x.Id == secondCredential).ExecuteUpdateAsync(x => x.SetProperty(y => y.RevokedAt, DateTimeOffset.UtcNow));
            var pendingDisable = disabler.ApplicationCapabilitySelections.Where(x => x.TenantId == tenant.Id && x.ApplicationId == app.Id && x.CapabilityId == capability)
                .ExecuteUpdateAsync(x => x.SetProperty(y => y.Enabled, false));
            await Task.Delay(100);
            Assert.False(pendingRevocation.IsCompleted);
            Assert.False(pendingDisable.IsCompleted);
            await transaction.CommitAsync();
            Assert.Equal("claimed", (await pendingClaim).Status);
            Assert.Equal(1, await pendingRevocation);
            Assert.Equal(1, await pendingDisable);
        }
    }

    private sealed class TestClock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
    private sealed class TestFactory(DbContextOptions<CloudControlPlaneDbContext> options) : IDbContextFactory<CloudControlPlaneDbContext>
    {
        public CloudControlPlaneDbContext CreateDbContext() => new(options);
    }
}
