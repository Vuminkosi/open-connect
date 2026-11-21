using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OpenConnect.Docs.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

[Collection("Cloud PostgreSQL")]
public sealed class CloudAnalyticsUpgradePostgresTests
{
    [CloudUpgradePostgresFact]
    public async Task UpgradeRetainsExistingTenantAndInstallsUsageGuards()
    {
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_UPGRADE_TEST_DATABASE")).Options;
        IDbContextFactory<CloudControlPlaneDbContext> factory = new Factory(options);
        await using (var db = await factory.CreateDbContextAsync())
        {
            Assert.Empty(await db.Database.GetAppliedMigrationsAsync());
            await db.GetService<IMigrator>().MigrateAsync("20261122000000_AddHostPrivacyReason");
        }
        var owner = new CloudSubject("test-issuer", Guid.NewGuid().ToString("N"));
        var tenants = new EfCloudTenantStore(factory, TimeProvider.System);
        var tenant = await tenants.CreateTenantAsync(owner, "Analytics upgrade", default);
        var application = (await tenants.CreateApplicationAsync(owner, tenant.Id, "Existing application", "Development", default)).Application!;

        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.GetService<IMigrator>().MigrateAsync("20261122000002_AddAnalyticsComputeUsage");
            Assert.True(await db.Applications.AnyAsync(x => x.Id == application.Id && x.TenantId == tenant.Id));
            var triggers = await db.Database.SqlQuery<int>($"""
                SELECT count(*)::int AS "Value" FROM pg_trigger t
                JOIN pg_class c ON c.oid = t.tgrelid JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = 'cloud' AND c.relname = 'analytics_compute_usage' AND NOT t.tgisinternal
                """).SingleAsync();
            Assert.Equal(3, triggers);
        }
    }

    private sealed class Factory(DbContextOptions<CloudControlPlaneDbContext> options) : IDbContextFactory<CloudControlPlaneDbContext>
    {
        public CloudControlPlaneDbContext CreateDbContext() => new(options);
    }
}
