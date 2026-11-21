using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OpenConnect.Docs.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

[Collection("Cloud PostgreSQL")]
public sealed class CloudVoiceClaimPostgresTests
{
    [CloudPostgresFact]
    public async Task ClaimsRequireMatchingInspectionAndRespectDeploymentCapacity()
    {
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_TEST_DATABASE")).Options;
        IDbContextFactory<CloudControlPlaneDbContext> factory = new TestFactory(options);
        await using (var db = await factory.CreateDbContextAsync()) await db.Database.MigrateAsync();
        var owner = new CloudSubject("https://issuer.example", Guid.NewGuid().ToString("N"));
        var tenants = new EfCloudTenantStore(factory, TimeProvider.System);
        var tenant = await tenants.CreateTenantAsync(owner, "Voice claim", default);
        var app = (await tenants.CreateApplicationAsync(owner, tenant.Id, "Voice app", "Development", default)).Application!;
        var material = CloudCredentialSecrets.Create("Development");
        var deploymentId = Guid.NewGuid(); var endpoint = "voice-" + Guid.NewGuid().ToString("N");
        var digest = "sha256:" + new string('a', 64); var now = DateTimeOffset.UtcNow;
        await using (var db = await factory.CreateDbContextAsync())
        {
            var capability = await db.Capabilities.SingleAsync(x => x.Id == "voice.synthesize");
            capability.PlatformAvailability = "available"; capability.AllowedRegions = "[\"region-a\"]"; capability.AllowedRegionsVerified = true;
            db.RegionPolicies.Add(new CloudRegionPolicy { TenantId = tenant.Id, AllowedRegions = "[\"region-a\"]", Revision = 1 });
            db.WorkerDeployments.Add(new CloudWorkerDeployment { Id = deploymentId, CapabilityId = "voice.synthesize", EndpointReference = endpoint,
                Transport = "https-mtls", Region = "region-a", ContractVersion = "v1", ModelId = "kokoro", ModelRevision = "revision-1",
                ModelDigest = digest, ImageDigest = "sha256:" + new string('b', 64), Enabled = true, ConcurrencyLimit = 1 });
            db.TenantEntitlements.Add(new CloudTenantEntitlement { TenantId = tenant.Id, CapabilityId = "voice.synthesize", PlanGranted = true, Revision = 1 });
            db.TenantCapabilitySelections.Add(new CloudTenantCapabilitySelection { TenantId = tenant.Id, CapabilityId = "voice.synthesize", Enabled = true, Revision = 1 });
            db.ApplicationCapabilitySelections.Add(new CloudApplicationCapabilitySelection { TenantId = tenant.Id, ApplicationId = app.Id, CapabilityId = "voice.synthesize", Enabled = true, Revision = 1 });
            db.QuotaBuckets.Add(new CloudQuotaBucket { Id = Guid.NewGuid(), TenantId = tenant.Id, ApplicationId = app.Id, CapabilityId = "voice.synthesize",
                Unit = "character", PeriodStart = now.AddMinutes(-1), PeriodEnd = now.AddHours(1), Limit = 20 });
            db.Credentials.Add(new CloudCredential { Id = material.Id, TenantId = tenant.Id, ApplicationId = app.Id, Environment = "Development",
                KeyHash = material.Hash, Fingerprint = material.Fingerprint, Scopes = "[\"voice.synthesize\"]", IssuedAt = now.AddMinutes(-1), ExpiresAt = now.AddHours(1) });
            await db.SaveChangesAsync();
        }
        var identity = new CloudCredentialIdentity(material.Id, tenant.Id, app.Id, "Development", new HashSet<string> { "voice.synthesize" });
        var quota = new CloudQuotaService(new EfCloudQuotaStore(factory), TimeProvider.System);
        var evidence = new EfCloudVoicePrivacyEvidenceStore(factory, TimeProvider.System);
        var claim = new CloudVoiceClaimService(new EfCloudVoiceClaimStore(factory, TimeProvider.System));
        var fingerprint = new byte[32]; var workerHash = Enumerable.Repeat((byte)7, 32).ToArray();
        var deployment = new CloudInferenceDeploymentMatch(endpoint, "region-a", "v1", "revision-1", digest);
        var firstId = Guid.NewGuid(); var secondId = Guid.NewGuid();
        async Task<Guid> Reserve(Guid id) => (await quota.ReserveAsync(new CloudQuotaRequest(tenant.Id, app.Id, "voice.synthesize", "character", id,
            "key", fingerprint, 4, now.AddMinutes(1)), default)).ReservationId!.Value;
        var first = await Reserve(firstId); var second = await Reserve(secondId);
        Task<CloudExecutionClaimResult> Claim(Guid id) => claim.ClaimAsync(identity, id, "key", fingerprint, workerHash, "kokoro", deployment, default);
        Assert.Equal("unavailable", (await Claim(first)).Status);
        await evidence.AppendAsync(identity, firstId, "key", fingerprint, 5, "allowed", new string('a', 32), default);
        Assert.Equal("unavailable", (await Claim(first)).Status);
        await evidence.AppendAsync(identity, firstId, "key", fingerprint, 4, "allowed", new string('a', 32), default);
        await evidence.AppendAsync(identity, secondId, "key", fingerprint, 4, "allowed", new string('a', 32), default);
        await using (var db = await factory.CreateDbContextAsync())
        {
            var revoked = await db.Credentials.SingleAsync(x => x.Id == material.Id);
            revoked.RevokedAt = now;
            await db.SaveChangesAsync();
        }
        Assert.Equal("unavailable", (await Claim(first)).Status);
        await using (var db = await factory.CreateDbContextAsync())
        {
            var restored = await db.Credentials.SingleAsync(x => x.Id == material.Id);
            restored.RevokedAt = null;
            await db.SaveChangesAsync();
        }
        await evidence.AppendAsync(identity, secondId, "key", fingerprint, 4, "privacy_blocked", new string('a', 32), default);
        Assert.Equal("unavailable", (await Claim(second)).Status);
        await evidence.AppendAsync(identity, secondId, "key", fingerprint, 4, "allowed", new string('a', 32), default);
        var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Claim(first)));
        Assert.Single(attempts, x => x.Status == "claimed");
        Assert.Equal(7, attempts.Count(x => x.Status == "already_claimed"));
        Assert.Equal("unavailable", (await Claim(second)).Status);
        Assert.Equal("idempotency_conflict", (await claim.ClaimAsync(identity, second, "key", Enumerable.Repeat((byte)1, 32).ToArray(),
            workerHash, "kokoro", deployment, default)).Status);
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.ExecutionClaims.Add(new CloudExecutionClaim { ReservationId = second, ClaimId = Guid.NewGuid(), TenantId = tenant.Id,
                ApplicationId = app.Id, DeploymentId = deploymentId, Region = "region-a", WorkerPayloadFingerprint = workerHash, ClaimedAt = now });
            await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        await using (var db = await factory.CreateDbContextAsync())
        {
            var script = db.GetService<IMigrator>().GenerateScript("20261123000100_AddVoicePrivacyEvidence",
                "20261123000200_AllowVoiceDeploymentClaims", MigrationsSqlGenerationOptions.Idempotent);
            await db.Database.ExecuteSqlRawAsync(script);
        }
    }

    private sealed class TestFactory(DbContextOptions<CloudControlPlaneDbContext> options) : IDbContextFactory<CloudControlPlaneDbContext>
    {
        public CloudControlPlaneDbContext CreateDbContext() => new(options);
    }
}
