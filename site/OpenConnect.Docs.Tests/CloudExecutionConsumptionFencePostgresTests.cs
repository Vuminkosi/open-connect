using Microsoft.EntityFrameworkCore;
using OpenConnect.Docs.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

[Collection("Cloud PostgreSQL")]
public sealed class CloudExecutionConsumptionFencePostgresTests
{
    [CloudPostgresFact]
    public async Task WorkerConsumptionRechecksCredentialEntitlementAndRegion()
    {
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>().UseNpgsql(Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_TEST_DATABASE")).Options;
        IDbContextFactory<CloudControlPlaneDbContext> factory = new TestFactory(options);
        await using (var db = await factory.CreateDbContextAsync()) await db.Database.MigrateAsync();
        var owner = new CloudSubject("test-issuer", Guid.NewGuid().ToString("N"));
        var tenants = new EfCloudTenantStore(factory, TimeProvider.System);
        var tenant = await tenants.CreateTenantAsync(owner, "Consumption fence", default);
        var app = (await tenants.CreateApplicationAsync(owner, tenant.Id, "App", "Development", default)).Application!;
        var reference = "fence-" + Guid.NewGuid().ToString("N");
        var deployment = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        string availability, regions; bool verified;
        await using (var db = await factory.CreateDbContextAsync())
        {
            var catalog = await db.Capabilities.SingleAsync(x => x.Id == "classification.classify");
            availability = catalog.PlatformAvailability; regions = catalog.AllowedRegions; verified = catalog.AllowedRegionsVerified;
            catalog.PlatformAvailability = "available"; catalog.AllowedRegions = "[\"region-a\"]"; catalog.AllowedRegionsVerified = true;
            db.RegionPolicies.Add(new CloudRegionPolicy { TenantId = tenant.Id, AllowedRegions = "[\"region-a\"]", Revision = 1 });
            db.TenantEntitlements.Add(new CloudTenantEntitlement { TenantId = tenant.Id, CapabilityId = "classification.classify", PlanGranted = true, Revision = 1 });
            db.TenantCapabilitySelections.Add(new CloudTenantCapabilitySelection { TenantId = tenant.Id, CapabilityId = "classification.classify", Enabled = true, Revision = 1 });
            db.ApplicationCapabilitySelections.Add(new CloudApplicationCapabilitySelection { TenantId = tenant.Id, ApplicationId = app.Id, CapabilityId = "classification.classify", Enabled = true, Revision = 1 });
            db.WorkerDeployments.Add(new CloudWorkerDeployment { Id = deployment, CapabilityId = "classification.classify", EndpointReference = reference, Transport = "https-mtls", Region = "region-a", ContractVersion = "openconnect.v1", ModelId = "model", ModelRevision = "revision-1", ModelDigest = "sha256:" + new string('a', 64), ImageDigest = "sha256:" + new string('b', 64), Enabled = true, ConcurrencyLimit = 10 });
            db.QuotaBuckets.Add(new CloudQuotaBucket { Id = Guid.NewGuid(), TenantId = tenant.Id, ApplicationId = app.Id, CapabilityId = "classification.classify", Unit = "request", PeriodStart = now.AddMinutes(-1), PeriodEnd = now.AddMinutes(10), Limit = 10 });
            await db.SaveChangesAsync();
        }
        try
        {
            var credentials = new CloudCredentialService(new EfCloudCredentialStore(factory), TimeProvider.System);
            var issued = await credentials.CreateAsync(owner, tenant.Id, app.Id, ["classification.classify"], now.AddDays(1), default);
            Assert.Equal("succeeded", issued.Status);
            var quota = new CloudQuotaService(new EfCloudQuotaStore(factory), TimeProvider.System);
            var claims = new CloudExecutionClaimService(new EfCloudExecutionClaimStore(factory, TimeProvider.System));
            var consumption = new CloudExecutionConsumptionService(new EfCloudExecutionConsumptionStore(factory, TimeProvider.System));
            var proof = new byte[32];
            async Task<(Guid Reservation, Guid Claim)> CreateClaim()
            {
                var reservation = await quota.ReserveAsync(new CloudQuotaRequest(tenant.Id, app.Id, "classification.classify", "request", Guid.NewGuid(), "key", new byte[32], 1, now.AddMinutes(5)), default);
                Assert.Equal("reserved", reservation.Status);
                var claim = await claims.ClaimAsync(issued.Credential!.Id, tenant.Id, app.Id, reservation.ReservationId!.Value, "classification.classify", "key", new byte[32], new(reference, "region-a", "openconnect.v1", "revision-1", "sha256:" + new string('a', 64)), default, proof);
                Assert.Equal("claimed", claim.Status);
                return (reservation.ReservationId.Value, claim.ClaimId!.Value);
            }
            var legacyReservation = await quota.ReserveAsync(new CloudQuotaRequest(tenant.Id, app.Id, "classification.classify", "request", Guid.NewGuid(), "key", new byte[32], 1, now.AddMinutes(5)), default);
            var legacyClaim = Guid.NewGuid();
            await using (var db = await factory.CreateDbContextAsync())
            {
                db.ExecutionClaims.Add(new CloudExecutionClaim { ReservationId = legacyReservation.ReservationId!.Value, TenantId = tenant.Id, ApplicationId = app.Id, ClaimId = legacyClaim, DeploymentId = deployment, Region = "region-a", WorkerPayloadFingerprint = proof, ClaimedAt = now });
                await db.SaveChangesAsync();
            }
            Assert.Equal("unavailable", await consumption.ConsumeAsync(legacyReservation.ReservationId!.Value, legacyClaim, deployment, proof, default));
            var first = await CreateClaim();
            var admin = new CloudEntitlementAdministration(new EfCloudEntitlementAdministrationStore(factory, TimeProvider.System));
            var actor = new CloudSubject("test-issuer", "operator");
            Assert.Equal("succeeded", (await admin.PutAsync(actor, "trace", tenant.Id, "classification.classify", new(false, null), 1, default)).Status);
            Assert.Equal("unavailable", await consumption.ConsumeAsync(first.Reservation, first.Claim, deployment, proof, default));
            Assert.Equal("succeeded", (await admin.PutAsync(actor, "trace", tenant.Id, "classification.classify", new(true, null), 2, default)).Status);
            Assert.Equal("consumed", await consumption.ConsumeAsync(first.Reservation, first.Claim, deployment, proof, default));
            var second = await CreateClaim();
            await using (var db = await factory.CreateDbContextAsync())
                await db.RegionPolicies.Where(x => x.TenantId == tenant.Id).ExecuteUpdateAsync(x => x.SetProperty(y => y.AllowedRegions, "[]"));
            Assert.Equal("unavailable", await consumption.ConsumeAsync(second.Reservation, second.Claim, deployment, proof, default));
            await using (var db = await factory.CreateDbContextAsync())
                await db.RegionPolicies.Where(x => x.TenantId == tenant.Id).ExecuteUpdateAsync(x => x.SetProperty(y => y.AllowedRegions, "[\"region-a\"]"));
            Assert.Equal("succeeded", await credentials.RevokeAsync(owner, tenant.Id, app.Id, issued.Credential!.Id, default));
            Assert.Equal("unavailable", await consumption.ConsumeAsync(second.Reservation, second.Claim, deployment, proof, default));
        }
        finally
        {
            await using var db = await factory.CreateDbContextAsync();
            var catalog = await db.Capabilities.SingleAsync(x => x.Id == "classification.classify");
            catalog.PlatformAvailability = availability; catalog.AllowedRegions = regions; catalog.AllowedRegionsVerified = verified;
            await db.SaveChangesAsync();
        }
    }
    private sealed class TestFactory(DbContextOptions<CloudControlPlaneDbContext> options) : IDbContextFactory<CloudControlPlaneDbContext>
    {
        public CloudControlPlaneDbContext CreateDbContext() => new(options);
    }
}
