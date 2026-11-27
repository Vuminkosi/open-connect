using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OpenConnect.Docs.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

[Collection("Cloud PostgreSQL")]
public sealed class CloudInferencePrivacyUpgradePostgresTests
{
    [CloudUpgradePostgresFact]
    public async Task ExistingObservationsRemainUnboundAfterUpgrade()
    {
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>().UseNpgsql(Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_UPGRADE_TEST_DATABASE")).Options;
        IDbContextFactory<CloudControlPlaneDbContext> factory = new TestFactory(options);
        await using (var db = await factory.CreateDbContextAsync())
            await db.GetService<IMigrator>().MigrateAsync("20261121010000_AddInferencePrivacyEvidence");
        var owner = new CloudSubject("test-issuer", Guid.NewGuid().ToString("N"));
        var tenants = new EfCloudTenantStore(factory, TimeProvider.System);
        var tenant = await tenants.CreateTenantAsync(owner, "Privacy upgrade", default);
        var app = (await tenants.CreateApplicationAsync(owner, tenant.Id, "App", "Development", default)).Application!;
        var id = Guid.NewGuid();
        var request = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var trace = new string('a', 32);
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO cloud.inference_privacy_evidence ("Id", "TenantId", "ApplicationId", "RequestId", "Outcome", "TraceId", "OccurredAt")
                VALUES ({id}, {tenant.Id}, {app.Id}, {request}, {"allowed"}, {trace}, {now})
                """);
            await db.Database.MigrateAsync();
            var historical = await db.Database.SqlQuery<int>($"""
                SELECT count(*)::int AS "Value" FROM cloud.inference_privacy_evidence
                WHERE "Id" = {id} AND "Sequence" > 0 AND "FingerprintKeyId" IS NULL AND "RequestFingerprint" IS NULL
                """).SingleAsync();
            Assert.Equal(1, historical);
            await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM cloud.inference_privacy_evidence WHERE \"Id\" = {id}"));
        }
    }
    private sealed class TestFactory(DbContextOptions<CloudControlPlaneDbContext> options) : IDbContextFactory<CloudControlPlaneDbContext>
    {
        public CloudControlPlaneDbContext CreateDbContext() => new(options);
    }
}
