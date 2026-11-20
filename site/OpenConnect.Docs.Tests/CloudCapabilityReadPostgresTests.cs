using Microsoft.EntityFrameworkCore;
using OpenConnect.Docs.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

[Collection("Cloud PostgreSQL")]
public sealed class CloudCapabilityReadPostgresTests
{
    [CloudPostgresFact]
    public async Task ProjectionsEnforceTenantMembershipAndApplicationSelection()
    {
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_TEST_DATABASE")).Options;
        IDbContextFactory<CloudControlPlaneDbContext> factory = new TestFactory(options);
        await using (var db = await factory.CreateDbContextAsync()) await db.Database.MigrateAsync();
        var owner = new CloudSubject("https://identity.example", Guid.NewGuid().ToString("N"));
        var stranger = new CloudSubject("https://identity.example", Guid.NewGuid().ToString("N"));
        var tenantStore = new EfCloudTenantStore(factory, TimeProvider.System);
        var tenant = await tenantStore.CreateTenantAsync(owner, "Read test", CancellationToken.None);
        var app = (await tenantStore.CreateApplicationAsync(owner, tenant.Id, "App", "Development", CancellationToken.None)).Application!;
        var capabilityId = "test." + Guid.NewGuid().ToString("N");
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Capabilities.Add(new CloudCapabilityDefinition { Id = capabilityId, Name = "Read test", Description = "Test", ContractVersion = "1", BillingUnit = "request", PlatformAvailability = "available", Dependencies = "[]", Modalities = "[]", AllowedRegions = "[]" });
            db.TenantEntitlements.Add(new CloudTenantEntitlement { TenantId = tenant.Id, CapabilityId = capabilityId, PlanGranted = true, Revision = 1 });
            db.TenantCapabilitySelections.Add(new CloudTenantCapabilitySelection { TenantId = tenant.Id, CapabilityId = capabilityId, Enabled = true, Revision = 1 });
            db.ApplicationCapabilitySelections.Add(new CloudApplicationCapabilitySelection { TenantId = tenant.Id, ApplicationId = app.Id, CapabilityId = capabilityId, Enabled = false, Revision = 3 });
            await db.SaveChangesAsync();
        }
        var service = new CloudCapabilityService(factory, new EfCloudCapabilityReader(factory), TimeProvider.System);

        Assert.Contains(await service.GetCatalogAsync(CancellationToken.None), x => x.Id == capabilityId);
        Assert.Null(await service.GetTenantCapabilitiesAsync(stranger, tenant.Id, CancellationToken.None));
        Assert.Null(await service.GetApplicationCapabilitiesAsync(stranger, tenant.Id, app.Id, CancellationToken.None));
        Assert.Null(await service.GetApplicationCapabilitiesAsync(owner, tenant.Id, Guid.NewGuid(), CancellationToken.None));
        var tenantCapability = (await service.GetTenantCapabilitiesAsync(owner, tenant.Id, CancellationToken.None))!.Single(x => x.Id == capabilityId);
        Assert.True(tenantCapability.Entitled);
        Assert.True(tenantCapability.TenantEnabled);
        Assert.Null(tenantCapability.ReasonCode);
        var appCapability = (await service.GetApplicationCapabilitiesAsync(owner, tenant.Id, app.Id, CancellationToken.None))!.Single(x => x.Id == capabilityId);
        Assert.False(appCapability.ApplicationEnabled);
        Assert.Equal(3, appCapability.Revision);
        Assert.Equal("capability_disabled", appCapability.ReasonCode);
    }

    private sealed class TestFactory(DbContextOptions<CloudControlPlaneDbContext> options) : IDbContextFactory<CloudControlPlaneDbContext>
    {
        public CloudControlPlaneDbContext CreateDbContext() => new(options);
    }
}
