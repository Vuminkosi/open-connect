using Microsoft.EntityFrameworkCore;
using OpenConnect.Docs.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

[Collection("Cloud PostgreSQL")]
public sealed class CloudTranslationClaimPostgresTests
{
    [CloudPostgresFact]
    public async Task ClaimRequiresCurrentProfilePrivacyAndCapacity()
    {
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_TEST_DATABASE")).Options;
        IDbContextFactory<CloudControlPlaneDbContext> factory = new TestFactory(options);
        await using (var db = await factory.CreateDbContextAsync())
            await db.Database.MigrateAsync();
        var owner = new CloudSubject("https://issuer.example", Guid.NewGuid().ToString("N"));
        var tenants = new EfCloudTenantStore(factory, TimeProvider.System);
        var tenant = await tenants.CreateTenantAsync(owner, "Translation claim", default);
        var app = (await tenants.CreateApplicationAsync(owner, tenant.Id, "Translation app", "Development", default)).Application!;
        var material = CloudCredentialSecrets.Create("Development");
        var deploymentId = Guid.NewGuid();
        var endpoint = "nllb-" + Guid.NewGuid().ToString("N");
        var digest = "sha256:" + new string('a', 64);
        var image = "sha256:" + new string('b', 64);
        var tokenizer = new string('c', 64);
        var now = DateTimeOffset.UtcNow;
        await using (var db = await factory.CreateDbContextAsync())
        {
            var capability = await db.Capabilities.SingleAsync(x => x.Id == "translation.translate");
            capability.PlatformAvailability = "available";
            capability.AllowedRegions = "[\"region-a\"]";
            capability.AllowedRegionsVerified = true;
            db.RegionPolicies.Add(new CloudRegionPolicy { TenantId = tenant.Id, AllowedRegions = "[\"region-a\"]", Revision = 1 });
            db.WorkerDeployments.Add(new CloudWorkerDeployment
            {
                Id = deploymentId,
                CapabilityId = "translation.translate",
                EndpointReference = endpoint,
                Transport = "https-mtls",
                Region = "region-a",
                ContractVersion = "v1",
                ModelId = "nllb",
                ModelRevision = "revision-1",
                ModelDigest = digest,
                ImageDigest = image,
                Enabled = true,
                ConcurrencyLimit = 1,
                Revision = 1
            });
            db.TenantEntitlements.Add(new CloudTenantEntitlement { TenantId = tenant.Id, CapabilityId = "translation.translate", PlanGranted = true, Revision = 1 });
            db.TenantCapabilitySelections.Add(new CloudTenantCapabilitySelection { TenantId = tenant.Id, CapabilityId = "translation.translate", Enabled = true, Revision = 1 });
            db.ApplicationCapabilitySelections.Add(new CloudApplicationCapabilitySelection { TenantId = tenant.Id, ApplicationId = app.Id, CapabilityId = "translation.translate", Enabled = true, Revision = 1 });
            db.QuotaBuckets.Add(new CloudQuotaBucket
            {
                Id = Guid.NewGuid(),
                TenantId = tenant.Id,
                ApplicationId = app.Id,
                CapabilityId = "translation.translate",
                Unit = "character",
                PeriodStart = now.AddMinutes(-1),
                PeriodEnd = now.AddHours(1),
                Limit = 20
            });
            db.Credentials.Add(new CloudCredential
            {
                Id = material.Id,
                TenantId = tenant.Id,
                ApplicationId = app.Id,
                Environment = "Development",
                KeyHash = material.Hash,
                Fingerprint = material.Fingerprint,
                Scopes = "[\"translation.translate\"]",
                IssuedAt = now.AddMinutes(-1),
                ExpiresAt = now.AddHours(1)
            });
            await db.SaveChangesAsync();
        }
        var identity = new CloudCredentialIdentity(material.Id, tenant.Id, app.Id, "Development", new HashSet<string> { "translation.translate" });
        var evidence = new EfCloudTranslationPrivacyEvidenceStore(factory, TimeProvider.System);
        var quota = new CloudTranslationQuota(new EfCloudTranslationQuotaStore(factory, TimeProvider.System), TimeProvider.System);
        var profiles = new CloudTranslationDeploymentProfiles(new EfCloudTranslationDeploymentProfileStore(factory, TimeProvider.System));
        var claim = new CloudTranslationClaim(new EfCloudTranslationClaimStore(factory, TimeProvider.System));
        var fingerprint = new byte[32];
        var workerHash = Enumerable.Repeat((byte)7, 32).ToArray();
        var deployment = new CloudTranslationDeploymentMatchRequest(deploymentId, endpoint, "region-a", "v1", "nllb", "revision-1",
            digest, image, tokenizer, "eng_Latn", "fra_Latn");
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        async Task<Guid> Reserve(Guid requestId)
        {
            await evidence.AppendAsync(identity, requestId, "key", fingerprint, 4, "allowed", new string('a', 32), default);
            var result = await quota.ReserveAsync(new CloudQuotaRequest(tenant.Id, app.Id, "translation.translate", "character", requestId,
                "key", fingerprint, 4, now.AddMinutes(1)), default);
            Assert.Equal("reserved", result.Status);
            return result.ReservationId!.Value;
        }
        var first = await Reserve(firstId);
        var second = await Reserve(secondId);
        Task<CloudExecutionClaimResult> Claim(Guid reservation, CloudTranslationDeploymentMatchRequest? match = null) =>
            claim.ClaimAsync(identity, reservation, "key", fingerprint, workerHash, match ?? deployment, default);
        Assert.Equal("unavailable", (await Claim(first)).Status);
        var profile = new CloudTranslationDeploymentProfile(deploymentId, 1, 0, tokenizer, [new("eng_Latn", "fra_Latn")]);
        Assert.Equal("succeeded", await profiles.PutAsync(owner, "trace", profile, 0, default));
        Assert.Equal("unavailable", (await Claim(first, deployment with { TargetLang = "deu_Latn" })).Status);
        Assert.Equal("unavailable", (await Claim(first, deployment with { TokenizerHash = new string('d', 64) })).Status);
        await evidence.AppendAsync(identity, firstId, "key", fingerprint, 4, "privacy_blocked", new string('a', 32), default);
        Assert.Equal("unavailable", (await Claim(first)).Status);
        await evidence.AppendAsync(identity, firstId, "key", fingerprint, 4, "allowed", new string('a', 32), default);
        var attempts = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Claim(first)));
        Assert.Single(attempts, x => x.Status == "claimed");
        Assert.Equal(5, attempts.Count(x => x.Status == "already_claimed"));
        Assert.Equal("unavailable", (await Claim(second)).Status);
        await using var verify = await factory.CreateDbContextAsync();
        var row = await verify.Database.SqlQuery<long>($"SELECT \"ProfileRevision\" AS \"Value\" FROM cloud.translation_claim_evidence WHERE \"ReservationId\" = {first}").SingleAsync();
        Assert.Equal(1, row);
        await Assert.ThrowsAnyAsync<Exception>(() => verify.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM cloud.translation_claim_evidence WHERE \"ReservationId\" = {first}"));
    }

    private sealed class TestFactory(DbContextOptions<CloudControlPlaneDbContext> options) : IDbContextFactory<CloudControlPlaneDbContext>
    {
        public CloudControlPlaneDbContext CreateDbContext() => new(options);
    }
}
