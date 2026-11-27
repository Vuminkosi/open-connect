using Microsoft.EntityFrameworkCore;
using OpenConnect.Docs.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

[Collection("Cloud PostgreSQL")]
public sealed class CloudKnowledgeRecoveryPostgresTests
{
    [CloudPostgresFact]
    public async Task ExpiredUnconsumedKnowledgeClaimRecoversOnceAndRejectsLateConsumption()
    {
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_TEST_DATABASE")).Options;
        IDbContextFactory<CloudControlPlaneDbContext> factory = new Factory(options);
        await using (var db = await factory.CreateDbContextAsync())
            await db.Database.MigrateAsync();
        var owner = new CloudSubject("test-issuer", Guid.NewGuid().ToString("N"));
        var tenants = new EfCloudTenantStore(factory, TimeProvider.System);
        var tenant = await tenants.CreateTenantAsync(owner, "Knowledge recovery", default);
        var app = (await tenants.CreateApplicationAsync(owner, tenant.Id, "App", "Development", default)).Application!;
        var now = DateTimeOffset.UtcNow;
        var bucket = Guid.NewGuid();
        var deployment = Guid.NewGuid();
        var reservation = Guid.NewGuid();
        var claim = Guid.NewGuid();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.QuotaBuckets.Add(new CloudQuotaBucket
            {
                Id = bucket,
                TenantId = tenant.Id,
                ApplicationId = app.Id,
                CapabilityId = "knowledge.embed",
                Unit = "request",
                PeriodStart = now.AddHours(-1),
                PeriodEnd = now.AddHours(1),
                Limit = 5,
                Reserved = 1
            });
            db.WorkerDeployments.Add(new CloudWorkerDeployment
            {
                Id = deployment,
                CapabilityId = "knowledge.embed",
                EndpointReference = "recovery-" + deployment.ToString("N"),
                Transport = "https-mtls",
                Region = "region-a",
                ContractVersion = "openconnect.v1",
                ImageDigest = "sha256:" + new string('a', 64),
                Enabled = true,
                ConcurrencyLimit = 1
            });
            await db.SaveChangesAsync();
            db.QuotaReservations.Add(new CloudQuotaReservation
            {
                Id = reservation,
                BucketId = bucket,
                TenantId = tenant.Id,
                ApplicationId = app.Id,
                RequestId = Guid.NewGuid(),
                Amount = 1,
                ExpiresAt = now.AddMinutes(-10)
            });
            await db.SaveChangesAsync();
            db.ExecutionClaims.Add(new CloudExecutionClaim
            {
                ReservationId = reservation,
                ClaimId = claim,
                TenantId = tenant.Id,
                ApplicationId = app.Id,
                DeploymentId = deployment,
                Region = "region-a",
                WorkerPayloadFingerprint = new byte[32],
                ClaimedAt = now.AddMinutes(-20)
            });
            await db.SaveChangesAsync();
        }
        var recovery = new EfCloudUnconsumedClaimRecoveryStore(factory, TimeProvider.System);
        var counts = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => recovery.ReleaseAsync(now.AddMinutes(-5), 100, default)));
        Assert.Equal(1, counts.Sum());
        Assert.Equal(0, await recovery.ReleaseAsync(now.AddMinutes(-5), 100, default));
        await using var verify = await factory.CreateDbContextAsync();
        var saved = await verify.QuotaReservations.SingleAsync(x => x.Id == reservation);
        Assert.Equal(0, saved.Actual);
        Assert.NotNull(saved.FinalizedAt);
        Assert.Equal(0, (await verify.QuotaBuckets.SingleAsync(x => x.Id == bucket)).Reserved);
        Assert.True(await verify.Database.SqlQuery<bool>($"SELECT EXISTS (SELECT 1 FROM cloud.execution_abandonments WHERE \"ReservationId\" = {reservation}) AS \"Value\"").SingleAsync());
        verify.ExecutionConsumptions.Add(new CloudExecutionConsumption { ReservationId = reservation, ClaimId = claim, DeploymentId = deployment, ConsumedAt = now });
        await Assert.ThrowsAsync<DbUpdateException>(() => verify.SaveChangesAsync());
    }
    private sealed class Factory(DbContextOptions<CloudControlPlaneDbContext> options) : IDbContextFactory<CloudControlPlaneDbContext>
    {
        public CloudControlPlaneDbContext CreateDbContext() => new(options);
    }
}
