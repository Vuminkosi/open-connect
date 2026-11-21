using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OpenConnect.Docs.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

[Collection("Cloud PostgreSQL")]
public sealed class CloudAvailabilityUpgradePostgresTests
{
    [CloudUpgradePostgresFact]
    public async Task UpgradeBackfillsExistingStateAfterAnOlderWriterCommits()
    {
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_UPGRADE_TEST_DATABASE")).Options;
        IDbContextFactory<CloudControlPlaneDbContext> factory = new Factory(options);
        var capability = "availability-upgrade." + Guid.NewGuid().ToString("N");
        await using (var db = await factory.CreateDbContextAsync())
        {
            Assert.Empty(await db.Database.GetAppliedMigrationsAsync());
            await db.GetService<IMigrator>().MigrateAsync("20261123000500_IndexVoicePrivacyObservations");
            db.Capabilities.Add(new CloudCapabilityDefinition { Id = capability, Name = "Upgrade test", Description = "Test",
                ContractVersion = "v1", BillingUnit = "request", PlatformAvailability = "not_configured",
                Dependencies = "[]", Modalities = "[]", AllowedRegions = "[]" });
            await db.SaveChangesAsync();
        }

        await using (var oldWriter = await factory.CreateDbContextAsync())
        await using (var transaction = await oldWriter.Database.BeginTransactionAsync())
        {
            await oldWriter.Capabilities.Where(x => x.Id == capability)
                .ExecuteUpdateAsync(x => x.SetProperty(y => y.PlatformAvailability, "available"));
            var upgrade = Task.Run(async () =>
            {
                await using var db = await factory.CreateDbContextAsync();
                await db.Database.MigrateAsync();
            });
            var waiting = false;
            await using (var observer = await factory.CreateDbContextAsync())
            {
                for (var attempt = 0; attempt < 150 && !waiting; attempt++)
                {
                    waiting = await observer.Database.SqlQueryRaw<bool>("SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE wait_event_type = 'Lock' AND query LIKE '%LOCK TABLE cloud.capabilities%') AS \"Value\"").SingleAsync();
                    if (!waiting) await Task.Delay(20);
                }
            }
            Assert.True(waiting, "The migration did not wait for the older writer's capability update.");
            await transaction.CommitAsync();
            await upgrade;
        }

        var service = new CloudCapabilityAvailabilityService(new EfCloudCapabilityAvailabilityStore(factory, TimeProvider.System));
        var upgraded = await service.GetAsync(capability, default);
        Assert.NotNull(upgraded);
        Assert.Equal("available", upgraded.Availability);
        Assert.Equal(1, upgraded.Revision);
        await using (var oldWriter = await factory.CreateDbContextAsync())
            await oldWriter.Capabilities.Where(x => x.Id == capability)
                .ExecuteUpdateAsync(x => x.SetProperty(y => y.PlatformAvailability, "not_configured"));
        var afterOldWriter = await service.GetAsync(capability, default);
        Assert.Equal("not_configured", afterOldWriter!.Availability);
        Assert.Equal(2, afterOldWriter.Revision);
    }

    private sealed class Factory(DbContextOptions<CloudControlPlaneDbContext> options) : IDbContextFactory<CloudControlPlaneDbContext>
    {
        public CloudControlPlaneDbContext CreateDbContext() => new(options);
    }
}
