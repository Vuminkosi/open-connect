using Microsoft.EntityFrameworkCore;
using OpenConnect.Docs.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

[Collection("Cloud PostgreSQL")]
public sealed class CloudAnalyticsClaimPostgresTests
{
    [CloudPostgresFact]
    public async Task ClaimRequiresPrivacyAndIsSingleUseAcrossConcurrentCallers()
    {
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_TEST_DATABASE")).Options;
        IDbContextFactory<CloudControlPlaneDbContext> factory = new Factory(options);
        await using (var db = await factory.CreateDbContextAsync())
            await db.Database.MigrateAsync();
        var tenants = new EfCloudTenantStore(factory, TimeProvider.System);
        var owner = new CloudSubject("test-issuer", Guid.NewGuid().ToString("N"));
        var tenant = await tenants.CreateTenantAsync(owner, "Analytics claim", default);
        var app = (await tenants.CreateApplicationAsync(owner, tenant.Id, "App", "Development", default)).Application!;
        var now = DateTimeOffset.UtcNow;
        var credential = Guid.NewGuid();
        var reservation = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var bucket = Guid.NewGuid();
        var deploymentId = Guid.NewGuid();
        var fingerprint = new byte[32];
        var reference = "analytics-" + Guid.NewGuid().ToString("N");
        var digest = "sha256:" + new string('a', 64);
        await using (var db = await factory.CreateDbContextAsync())
        {
            var definition = await db.Capabilities.SingleAsync(x => x.Id == "analytics.analyze");
            definition.PlatformAvailability = "available";
            definition.AllowedRegions = "[\"region-a\"]";
            definition.AllowedRegionsVerified = true;
            db.RegionPolicies.Add(new CloudRegionPolicy { TenantId = tenant.Id, AllowedRegions = "[\"region-a\"]", Revision = 1 });
            db.TenantEntitlements.Add(new CloudTenantEntitlement { TenantId = tenant.Id, CapabilityId = "analytics.analyze", PlanGranted = true, Revision = 1 });
            db.TenantCapabilitySelections.Add(new CloudTenantCapabilitySelection { TenantId = tenant.Id, CapabilityId = "analytics.analyze", Enabled = true, Revision = 1 });
            db.ApplicationCapabilitySelections.Add(new CloudApplicationCapabilitySelection { TenantId = tenant.Id, ApplicationId = app.Id, CapabilityId = "analytics.analyze", Enabled = true, Revision = 1 });
            db.Credentials.Add(new CloudCredential
            {
                Id = credential,
                TenantId = tenant.Id,
                ApplicationId = app.Id,
                Environment = "Development",
                KeyHash = new byte[32],
                Fingerprint = "claim-test",
                Scopes = "[\"analytics.analyze\"]",
                IssuedAt = now.AddMinutes(-1),
                ExpiresAt = now.AddHours(1)
            });
            db.QuotaBuckets.Add(new CloudQuotaBucket
            {
                Id = bucket,
                TenantId = tenant.Id,
                ApplicationId = app.Id,
                CapabilityId = "analytics.analyze",
                Unit = "request",
                PeriodStart = now.AddMinutes(-1),
                PeriodEnd = now.AddHours(1),
                Limit = 10,
                Reserved = 1
            });
            db.WorkerDeployments.Add(new CloudWorkerDeployment
            {
                Id = deploymentId,
                CapabilityId = "analytics.analyze",
                EndpointReference = reference,
                Transport = "https-mtls",
                Region = "region-a",
                ContractVersion = "openconnect.v1",
                ModelId = "model",
                ModelRevision = "revision",
                ModelDigest = digest,
                ImageDigest = digest,
                ConcurrencyLimit = 1,
                Enabled = true
            });
            await db.SaveChangesAsync();
            db.QuotaReservations.Add(new CloudQuotaReservation
            {
                Id = reservation,
                TenantId = tenant.Id,
                ApplicationId = app.Id,
                BucketId = bucket,
                RequestId = requestId,
                Amount = 1,
                ExpiresAt = now.AddMinutes(5),
                FingerprintKeyId = "active",
                RequestFingerprint = fingerprint
            });
            await db.SaveChangesAsync();
        }
        var deployment = new CloudClassificationDeploymentMatchRequest(reference, "region-a", "openconnect.v1", "revision", digest);
        var service = new CloudExecutionClaimService(new EfCloudExecutionClaimStore(factory, TimeProvider.System));
        Task<CloudExecutionClaimResult> Claim() => service.ClaimAsync(credential, tenant.Id, app.Id, reservation, "analytics.analyze", "active", fingerprint, deployment, default, new byte[32]);
        Assert.Equal("unavailable", (await Claim()).Status);
        var identity = new CloudCredentialIdentity(credential, tenant.Id, app.Id, "Development", new HashSet<string>());
        await new EfCloudAnalyticsPrivacyEvidenceStore(factory, TimeProvider.System)
            .AppendAsync(identity, requestId, "active", fingerprint, "request", "allowed", new string('a', 32), [], default);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Claim()));
        Assert.Single(results, x => x.Status == "claimed" && x.ClaimId is not null);
        Assert.Equal(7, results.Count(x => x.Status == "already_claimed"));
        var claim = results.Single(x => x.Status == "claimed").ClaimId!.Value;
        var consume = new EfCloudAnalyticsConsumptionStore(new EfCloudExecutionConsumptionStore(factory, TimeProvider.System));
        Assert.Equal("unavailable", await consume.ConsumeAsync(reservation, claim, Guid.NewGuid(), new byte[32], default));
        Assert.Equal("unavailable", await consume.ConsumeAsync(reservation, claim, deploymentId, Enumerable.Repeat((byte)1, 32).ToArray(), default));
        var evidence = new EfCloudAnalyticsPrivacyEvidenceStore(factory, TimeProvider.System);
        await evidence.AppendAsync(identity, requestId, "active", fingerprint, "request", "privacy_blocked", new string('b', 32), [new("EMAIL", 1)], default);
        Assert.Equal("unavailable", await consume.ConsumeAsync(reservation, claim, deploymentId, new byte[32], default));
        await evidence.AppendAsync(identity, requestId, "active", fingerprint, "request", "allowed", new string('c', 32), [], default);
        var consumptions = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => consume.ConsumeAsync(reservation, claim, deploymentId, new byte[32], default)));
        Assert.Single(consumptions, value => value == "consumed");
        Assert.Equal(7, consumptions.Count(value => value == "already_consumed"));
    }
    private sealed class Factory(DbContextOptions<CloudControlPlaneDbContext> options) : IDbContextFactory<CloudControlPlaneDbContext>
    {
        public CloudControlPlaneDbContext CreateDbContext() => new(options);
    }
}
