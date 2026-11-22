using Microsoft.EntityFrameworkCore;
using OpenConnect.Docs.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

[Collection("Cloud PostgreSQL")]
public sealed class CloudCapabilitySelectionPostgresTests
{
    [CloudPostgresFact]
    public async Task SelectionGatesAndConcurrentRevisionChecksRemainTransactional()
    {
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_TEST_DATABASE")).Options;
        IDbContextFactory<CloudControlPlaneDbContext> factory = new TestFactory(options);
        await using (var db = await factory.CreateDbContextAsync())
            await db.Database.MigrateAsync();
        var owner = new CloudSubject("https://issuer.example", Guid.NewGuid().ToString("N"));
        var developer = new CloudSubject(owner.Issuer, Guid.NewGuid().ToString("N"));
        var stranger = new CloudSubject(owner.Issuer, Guid.NewGuid().ToString("N"));
        var tenants = new EfCloudTenantStore(factory, TimeProvider.System);
        var tenant = await tenants.CreateTenantAsync(owner, "Selection test", CancellationToken.None);
        var app = (await tenants.CreateApplicationAsync(owner, tenant.Id, "App", "Development", CancellationToken.None)).Application!;
        var dependency = "test." + Guid.NewGuid().ToString("N");
        var capability = "test." + Guid.NewGuid().ToString("N");
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Memberships.Add(new CloudMembership { TenantId = tenant.Id, SubjectIssuer = developer.Issuer, SubjectId = developer.SubjectId, Role = "Developer", CreatedAt = DateTimeOffset.UtcNow });
            db.Capabilities.AddRange(Definition(dependency, "[]"), Definition(capability, $"[\"{dependency}\"]"));
            await db.SaveChangesAsync();
        }
        var service = new CloudCapabilityService(new EfCloudCapabilityReader(factory), new EfCloudCapabilitySelectionStore(factory, TimeProvider.System), TimeProvider.System);
        async Task<CloudCapabilitySelectionResult> SetTenant(CloudSubject actor, string id, bool enabled, int revision) =>
            await service.SetTenantSelectionAsync(actor, tenant.Id, id, enabled, revision, CancellationToken.None);
        async Task<CloudCapabilitySelectionResult> SetApp(string id, bool enabled, int revision) =>
            await service.SetApplicationSelectionAsync(owner, tenant.Id, app.Id, id, enabled, revision, CancellationToken.None);

        Assert.Equal("not_found", (await SetTenant(stranger, capability, true, 0)).Status);
        Assert.Equal("forbidden", (await SetTenant(developer, capability, true, 0)).Status);
        Assert.Equal("capability_not_entitled", (await SetTenant(owner, capability, true, 0)).Status);
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.TenantEntitlements.AddRange(
                new CloudTenantEntitlement { TenantId = tenant.Id, CapabilityId = dependency, PlanGranted = true, Revision = 1 },
                new CloudTenantEntitlement { TenantId = tenant.Id, CapabilityId = capability, PlanGranted = true, Revision = 1 });
            await db.SaveChangesAsync();
        }
        await using (var db = await factory.CreateDbContextAsync())
            await db.Capabilities.Where(x => x.Id == capability).ExecuteUpdateAsync(x => x.SetProperty(y => y.PlatformAvailability, "not_configured"));
        Assert.Equal("capability_unavailable", (await SetTenant(owner, capability, true, 0)).Status);
        await using (var db = await factory.CreateDbContextAsync())
            await db.Capabilities.Where(x => x.Id == capability).ExecuteUpdateAsync(x => x.SetProperty(y => y.PlatformAvailability, "available"));
        Assert.Equal("dependency_not_enabled", (await SetTenant(owner, capability, true, 0)).Status);
        Assert.Equal(new CloudCapabilitySelectionResult("succeeded", 1), await SetTenant(owner, dependency, true, 0));
        Assert.Equal("dependency_not_enabled", (await SetApp(capability, true, 0)).Status);
        Assert.Equal(new CloudCapabilitySelectionResult("succeeded", 1), await SetApp(dependency, true, 0));
        Assert.Equal("capability_disabled", (await SetApp(capability, true, 0)).Status);
        Assert.Equal(new CloudCapabilitySelectionResult("succeeded", 1), await SetTenant(owner, capability, true, 0));
        Assert.Equal(new CloudCapabilitySelectionResult("succeeded", 1), await SetApp(capability, true, 0));
        Assert.Equal(new CloudCapabilitySelectionResult("conflict", 1), await SetApp(capability, false, 0));
        Assert.Equal(new CloudCapabilitySelectionResult("succeeded", 2), await SetApp(capability, false, 1));

        var race = await Task.WhenAll(SetApp(capability, true, 2), SetApp(capability, true, 2));
        Assert.Single(race, x => x.Status == "succeeded");
        Assert.Single(race, x => x.Status == "conflict");
        await using (var db = await factory.CreateDbContextAsync())
            await db.TenantEntitlements.Where(x => x.TenantId == tenant.Id && x.CapabilityId == capability)
                .ExecuteUpdateAsync(x => x.SetProperty(y => y.PlanGranted, false));
        Assert.Equal(new CloudCapabilitySelectionResult("succeeded", 4), await SetApp(capability, false, 3));
        Assert.Equal("capability_not_entitled", (await SetApp(capability, true, 4)).Status);
        await using var verify = await factory.CreateDbContextAsync();
        var stored = await verify.ApplicationCapabilitySelections.SingleAsync(x => x.TenantId == tenant.Id && x.ApplicationId == app.Id && x.CapabilityId == capability);
        Assert.False(stored.Enabled);
        Assert.Equal(4, stored.Revision);
        Assert.Equal(7, await verify.AuditEvents.CountAsync(x => x.TenantId == tenant.Id && x.Action.EndsWith("capability.selection_changed")));
    }

    private static CloudCapabilityDefinition Definition(string id, string dependencies) => new()
    {
        Id = id,
        Name = id,
        Description = "Test",
        ContractVersion = "1",
        BillingUnit = "request",
        PlatformAvailability = "available",
        Dependencies = dependencies,
        Modalities = "[]",
        AllowedRegions = "[]"
    };

    private sealed class TestFactory(DbContextOptions<CloudControlPlaneDbContext> options) : IDbContextFactory<CloudControlPlaneDbContext>
    {
        public CloudControlPlaneDbContext CreateDbContext() => new(options);
    }
}
