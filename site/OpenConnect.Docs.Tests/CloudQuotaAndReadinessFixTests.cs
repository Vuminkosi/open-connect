using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OpenConnect.Docs.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

[Collection("Cloud PostgreSQL")]
public sealed class CloudQuotaAndReadinessFixTests
{
    [Theory]
    [InlineData("translation.translate", "characters or tokens", "character", true)]
    [InlineData("translation.translate", "not configured", "character", true)]
    [InlineData("translation.translate", "characters or tokens", "token", false)]
    [InlineData("translation.translate", "characters or tokens", "request", false)]
    [InlineData("translation.translate", "characters or tokens", "characters or tokens", false)]
    [InlineData("voice.synthesize", "audio duration or characters", "character", true)]
    [InlineData("voice.synthesize", "audio duration or characters", "token", false)]
    [InlineData("inference.chat", "input/output tokens", "token", true)]
    [InlineData("inference.chat", "input/output tokens", "request", false)]
    [InlineData("inference.liveui", "plans and tokens", "plan", true)]
    [InlineData("classification.classify", "requests and model compute", "request", true)]
    [InlineData("analytics.analyze", "requests", "request", true)]
    [InlineData("knowledge.embed", "requests", "request", true)]
    public void CloudQuotaUnitsEnforcesExpectedUnitsPerCapability(string capabilityId, string billingDescription, string unit, bool expected)
    {
        Assert.Equal(expected, CloudQuotaUnits.Supports(capabilityId, billingDescription, unit));
    }

    [Theory]
    [InlineData("classification.classify", true)]
    [InlineData("inference.chat", true)]
    [InlineData("inference.liveui", true)]
    [InlineData("analytics.analyze", true)]
    [InlineData("knowledge.embed", true)]
    [InlineData("voice.synthesize", true)]
    [InlineData("translation.translate", true)]
    [InlineData("voice.realtime", false)]
    [InlineData("unknown.capability", false)]
    public void WorkerReadinessSupportsVoiceSynthesisAndTranslationCapabilities(string capabilityId, bool expected)
    {
        Assert.Equal(expected, EfCloudWorkerReadinessStore.IsSupportedCapability(capabilityId));
        Assert.Equal(expected, EfCloudWorkerReadinessStore.SupportedCapabilities.Contains(capabilityId, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("classification.classify", "request", 1, true)]
    [InlineData("classification.classify", "request", 2, false)]
    [InlineData("analytics.analyze", "request", 1, true)]
    [InlineData("knowledge.embed", "request", 1, true)]
    [InlineData("inference.chat", "token", 1, true)]
    [InlineData("inference.chat", "token", 4096, true)]
    [InlineData("inference.chat", "request", 1, false)]
    [InlineData("inference.chat", "token", 0, false)]
    [InlineData("voice.synthesize", "character", 1, true)]
    [InlineData("voice.synthesize", "character", 2000, true)]
    [InlineData("voice.synthesize", "token", 500, false)]
    [InlineData("translation.translate", "character", 1, true)]
    [InlineData("translation.translate", "character", 5000, true)]
    [InlineData("translation.translate", "request", 1, false)]
    [InlineData("translation.translate", "character", 0, false)]
    [InlineData("inference.liveui", "plan", 1, false)]
    public void RecoveryStoresAcceptExpectedCapabilityUnitAndAmountCombinations(string capabilityId, string unit, long amount, bool expected)
    {
        Assert.Equal(expected, EfCloudQuotaRecoveryStore.SupportsRecovery(capabilityId, unit, amount));
        Assert.Equal(expected, EfCloudUnconsumedClaimRecoveryStore.SupportsRecovery(capabilityId, unit, amount));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    public async Task RecoveryStoresRejectOutOfRangeLimits(int invalidLimit)
    {
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused").Options;
        IDbContextFactory<CloudControlPlaneDbContext> factory = new Factory(options);
        var unclaimedStore = new EfCloudQuotaRecoveryStore(factory, TimeProvider.System);
        var claimStore = new EfCloudUnconsumedClaimRecoveryStore(factory, TimeProvider.System);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => unclaimedStore.ReleaseUnclaimedAsync(DateTimeOffset.UtcNow, invalidLimit, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => claimStore.ReleaseAsync(DateTimeOffset.UtcNow, invalidLimit, CancellationToken.None));
    }

    [Fact]
    public void MeteredClaimRecoveryMigrationUpdatesAbandonmentGuardPreservingAllFences()
    {
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused").Options;
        using var db = new CloudControlPlaneDbContext(options);
        var script = db.GetService<IMigrator>().GenerateScript(
            "20261125000200_AddTranslationExecutionClaims",
            "20261125000300_RecoverUnconsumedMeteredClaims",
            MigrationsSqlGenerationOptions.Idempotent);
        Assert.Contains("CREATE OR REPLACE FUNCTION cloud.validate_execution_abandonment()", script, StringComparison.Ordinal);
        Assert.Contains("r.\"Amount\" > 0", script, StringComparison.Ordinal);
        Assert.Contains("b.\"CapabilityId\" IN ('classification.classify', 'analytics.analyze', 'knowledge.embed') AND b.\"Unit\" = 'request' AND r.\"Amount\" = 1", script, StringComparison.Ordinal);
        Assert.Contains("b.\"CapabilityId\" = 'inference.chat' AND b.\"Unit\" = 'token'", script, StringComparison.Ordinal);
        Assert.Contains("b.\"CapabilityId\" IN ('voice.synthesize', 'translation.translate') AND b.\"Unit\" = 'character'", script, StringComparison.Ordinal);
        Assert.Contains("b.\"CapabilityId\" = 'inference.liveui' AND b.\"Unit\" = 'plan' AND r.\"Amount\" = 1", script, StringComparison.Ordinal);
        Assert.Contains("cloud.liveui_token_reservations", script, StringComparison.Ordinal);
        Assert.Contains("cloud.execution_consumptions", script, StringComparison.Ordinal);
        Assert.Contains("cloud.execution_completions", script, StringComparison.Ordinal);
        Assert.Contains("FOR UPDATE OF r", script, StringComparison.Ordinal);
        Assert.Contains("__EFMigrationsHistory", script, StringComparison.Ordinal);
    }

    [CloudPostgresFact]
    public async Task EnabledVoiceAndTranslationDeploymentsAreReadyAndDisabledOnesAreNot()
    {
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_TEST_DATABASE")).Options;
        IDbContextFactory<CloudControlPlaneDbContext> factory = new Factory(options);
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.MigrateAsync();
            foreach (var (id, capability) in ids.Zip(new[] { "voice.synthesize", "translation.translate" }))
                db.WorkerDeployments.Add(new CloudWorkerDeployment
                {
                    Id = id,
                    CapabilityId = capability,
                    EndpointReference = "readiness-" + id.ToString("N"),
                    Transport = "https-mtls",
                    Region = "region-a",
                    ContractVersion = "openconnect.v1",
                    ImageDigest = "sha256:" + new string('a', 64),
                    Enabled = true,
                    ConcurrencyLimit = 1,
                });
            await db.SaveChangesAsync();
        }
        var readiness = new EfCloudWorkerReadinessStore(factory);
        foreach (var id in ids) Assert.True(await readiness.IsReadyAsync(id, CancellationToken.None));
        await using (var db = await factory.CreateDbContextAsync())
            await db.WorkerDeployments.Where(x => ids.Contains(x.Id)).ExecuteUpdateAsync(x => x.SetProperty(y => y.Enabled, false));
        foreach (var id in ids) Assert.False(await readiness.IsReadyAsync(id, CancellationToken.None));
    }

    [CloudPostgresFact]
    public async Task ExpiredUnclaimedAndUnconsumedMeteredReservationsRecoverFullAmount()
    {
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_TEST_DATABASE")).Options;
        IDbContextFactory<CloudControlPlaneDbContext> factory = new Factory(options);
        await using (var db = await factory.CreateDbContextAsync()) await db.Database.MigrateAsync();
        var owner = new CloudSubject("test-issuer", Guid.NewGuid().ToString("N"));
        var tenants = new EfCloudTenantStore(factory, TimeProvider.System);
        var tenant = await tenants.CreateTenantAsync(owner, "Metered recovery", default);
        var app = (await tenants.CreateApplicationAsync(owner, tenant.Id, "App", "Development", default)).Application!;
        var now = DateTimeOffset.UtcNow;

        var cases = new[]
        {
            (Capability: "inference.chat", Unit: "token", UnclaimedAmount: 1500L, ClaimedAmount: 2500L),
            (Capability: "voice.synthesize", Unit: "character", UnclaimedAmount: 400L, ClaimedAmount: 850L),
            (Capability: "translation.translate", Unit: "character", UnclaimedAmount: 600L, ClaimedAmount: 1200L),
        };

        var records = new List<(Guid BucketId, Guid DeploymentId, Guid UnclaimedId, Guid ClaimedId, Guid ClaimId)>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            foreach (var item in cases)
            {
                var bucketId = Guid.NewGuid();
                var deploymentId = Guid.NewGuid();
                var unclaimedId = Guid.NewGuid();
                var claimedId = Guid.NewGuid();
                var claimId = Guid.NewGuid();
                records.Add((bucketId, deploymentId, unclaimedId, claimedId, claimId));

                db.QuotaBuckets.Add(new CloudQuotaBucket
                {
                    Id = bucketId,
                    TenantId = tenant.Id,
                    ApplicationId = app.Id,
                    CapabilityId = item.Capability,
                    Unit = item.Unit,
                    PeriodStart = now.AddHours(-1),
                    PeriodEnd = now.AddHours(1),
                    Limit = 10000,
                    Reserved = item.UnclaimedAmount + item.ClaimedAmount,
                });
                db.WorkerDeployments.Add(new CloudWorkerDeployment
                {
                    Id = deploymentId,
                    CapabilityId = item.Capability,
                    EndpointReference = "recovery-" + deploymentId.ToString("N"),
                    Transport = "https-mtls",
                    Region = "region-a",
                    ContractVersion = "openconnect.v1",
                    ImageDigest = "sha256:" + new string('b', 64),
                    Enabled = true,
                    ConcurrencyLimit = 5,
                });
                await db.SaveChangesAsync();

                db.QuotaReservations.AddRange(
                    new CloudQuotaReservation
                    {
                        Id = unclaimedId,
                        BucketId = bucketId,
                        TenantId = tenant.Id,
                        ApplicationId = app.Id,
                        RequestId = Guid.NewGuid(),
                        Amount = item.UnclaimedAmount,
                        ExpiresAt = now.AddMinutes(-10),
                    },
                    new CloudQuotaReservation
                    {
                        Id = claimedId,
                        BucketId = bucketId,
                        TenantId = tenant.Id,
                        ApplicationId = app.Id,
                        RequestId = Guid.NewGuid(),
                        Amount = item.ClaimedAmount,
                        ExpiresAt = now.AddMinutes(-10),
                    });
                await db.SaveChangesAsync();

                db.ExecutionClaims.Add(new CloudExecutionClaim
                {
                    ReservationId = claimedId,
                    ClaimId = claimId,
                    TenantId = tenant.Id,
                    ApplicationId = app.Id,
                    DeploymentId = deploymentId,
                    Region = "region-a",
                    WorkerPayloadFingerprint = new byte[32],
                    ClaimedAt = now.AddMinutes(-20),
                });
                await db.SaveChangesAsync();
            }
        }

        var unclaimedRecovery = new EfCloudQuotaRecoveryStore(factory, TimeProvider.System);
        var claimRecovery = new EfCloudUnconsumedClaimRecoveryStore(factory, TimeProvider.System);
        Assert.True(await unclaimedRecovery.ReleaseUnclaimedAsync(now.AddMinutes(-5), 100, default) >= cases.Length);
        Assert.True(await claimRecovery.ReleaseAsync(now.AddMinutes(-5), 100, default) >= cases.Length);

        await using var verify = await factory.CreateDbContextAsync();
        foreach (var record in records)
        {
            var unclaimed = await verify.QuotaReservations.SingleAsync(x => x.Id == record.UnclaimedId);
            Assert.Equal(0, unclaimed.Actual);
            Assert.NotNull(unclaimed.FinalizedAt);

            var claimed = await verify.QuotaReservations.SingleAsync(x => x.Id == record.ClaimedId);
            Assert.Equal(0, claimed.Actual);
            Assert.NotNull(claimed.FinalizedAt);

            Assert.Equal(0, (await verify.QuotaBuckets.SingleAsync(x => x.Id == record.BucketId)).Reserved);
            Assert.True(await verify.Database.SqlQuery<bool>(
                $"SELECT EXISTS (SELECT 1 FROM cloud.execution_abandonments WHERE \"ReservationId\" = {record.ClaimedId}) AS \"Value\"").SingleAsync());

            verify.ExecutionConsumptions.Add(new CloudExecutionConsumption
            {
                ReservationId = record.ClaimedId,
                ClaimId = record.ClaimId,
                DeploymentId = record.DeploymentId,
                ConsumedAt = now,
            });
            await Assert.ThrowsAsync<DbUpdateException>(() => verify.SaveChangesAsync());
            verify.ChangeTracker.Clear();
        }
    }

    private sealed class Factory(DbContextOptions<CloudControlPlaneDbContext> options) : IDbContextFactory<CloudControlPlaneDbContext>
    {
        public CloudControlPlaneDbContext CreateDbContext() => new(options);
    }
}
