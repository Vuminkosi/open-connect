using System.Security.Claims;
using Bunit;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Docs.Cloud;
using OpenConnect.Docs.Components.Pages.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

[Collection("Cloud PostgreSQL")]
public sealed class CloudRegionPolicyComponentTests : BunitContext
{
    [CloudPostgresFact]
    public async Task TenantOwnerSelectsRegionsAndDoesNotLeakAcrossTenants()
    {
        var factory = Factory();
        await using (var db = await factory.CreateDbContextAsync()) await db.Database.MigrateAsync();
        var owner = new CloudSubject("https://issuer.example", "owner-" + Guid.NewGuid().ToString("N"));
        var tenantStore = new EfCloudTenantStore(factory, TimeProvider.System);
        var tenant = await tenantStore.CreateTenantAsync(owner, "Region UI", default);
        var capability = "regionui." + Guid.NewGuid().ToString("N");
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Capabilities.Add(new CloudCapabilityDefinition { Id = capability, Name = "Region UI test", Description = "Test", ContractVersion = "1", BillingUnit = "request", PlatformAvailability = "available", Dependencies = "[]", Modalities = "[]", AllowedRegions = "[\"region-a\",\"region-b\"]", AllowedRegionsVerified = true });
            await db.SaveChangesAsync();
        }
        var policies = new CloudRegionPolicyService(new EfCloudRegionPolicyStore(factory, TimeProvider.System));
        Assert.Equal("succeeded", (await policies.PutAsync(owner, tenant.Id, ["old-region"], 0, default)).Status);
        ConfigureCommon(factory, owner.SubjectId);
        Services.AddSingleton<ICloudTenantStore>(tenantStore); Services.AddSingleton<CloudControlPlaneService>();
        Services.AddSingleton<ICloudRegionPolicyStore, EfCloudRegionPolicyStore>(); Services.AddSingleton<CloudRegionPolicyService>();
        var component = Render<TenantRegions>(p => p.Add(x => x.TenantId, tenant.Id));
        component.WaitForAssertion(() => Assert.Contains("old-region", component.Markup));
        Assert.Contains("not currently declared", component.Markup);
        component.FindAll("label.region-option").Single(x => x.TextContent.Contains("region-a")).QuerySelector("input")!.Change(true);
        component.FindAll("label.region-option").Single(x => x.TextContent.Contains("old-region")).QuerySelector("input")!.Change(false);
        component.FindAll("button").Single(x => x.TextContent == "Save allowed regions").Click();
        component.WaitForAssertion(() => Assert.Contains("Allowed regions saved.", component.Markup));
        var saved = await policies.GetAsync(owner, tenant.Id, default);
        Assert.Equal(new[] { "region-a" }, saved.Policy!.AllowedRegions);
        Assert.Equal("succeeded", (await policies.PutAsync(owner, tenant.Id, ["region-b"], saved.Policy.Revision, default)).Status);
        component.FindAll("button").Single(x => x.TextContent == "Save allowed regions").Click();
        component.WaitForAssertion(() => Assert.Contains("policy changed", component.Markup));
        Assert.True(component.FindAll("label.region-option").Single(x => x.TextContent.Contains("region-a")).QuerySelector("input")!.HasAttribute("checked"));
        component.FindAll("button").Single(x => x.TextContent == "Reload saved policy").Click();
        component.WaitForAssertion(() => Assert.True(component.FindAll("label.region-option").Single(x => x.TextContent.Contains("region-b")).QuerySelector("input")!.HasAttribute("checked")));
        component.Render(p => p.Add(x => x.TenantId, Guid.NewGuid()));
        component.WaitForAssertion(() => Assert.Contains("not found or you do not have access", component.Markup));
        Assert.DoesNotContain("old-region", component.Markup);
        Assert.Empty(component.FindAll("label.region-option"));
    }

    [CloudPostgresFact]
    public async Task TenantViewerCannotEditRegions()
    {
        var factory = Factory();
        await using (var db = await factory.CreateDbContextAsync()) await db.Database.MigrateAsync();
        var owner = new CloudSubject("https://issuer.example", "owner-" + Guid.NewGuid().ToString("N"));
        var viewer = new CloudSubject(owner.Issuer, "viewer-" + Guid.NewGuid().ToString("N"));
        var tenantStore = new EfCloudTenantStore(factory, TimeProvider.System);
        var tenant = await tenantStore.CreateTenantAsync(owner, "Region viewer", default);
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Memberships.Add(new CloudMembership { TenantId = tenant.Id, SubjectIssuer = viewer.Issuer, SubjectId = viewer.SubjectId, Role = "Developer", CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        ConfigureCommon(factory, viewer.SubjectId);
        Services.AddSingleton<ICloudTenantStore>(tenantStore); Services.AddSingleton<CloudControlPlaneService>();
        Services.AddSingleton<ICloudRegionPolicyStore, EfCloudRegionPolicyStore>(); Services.AddSingleton<CloudRegionPolicyService>();
        var component = Render<TenantRegions>(p => p.Add(x => x.TenantId, tenant.Id));
        component.WaitForAssertion(() => Assert.Contains("can view this policy", component.Markup));
        Assert.DoesNotContain(component.FindAll("button"), x => x.TextContent == "Save allowed regions");
        Assert.True(component.Find("fieldset").HasAttribute("disabled"));
    }

    [CloudPostgresFact]
    public async Task OperatorDeclaresRegionsAndRechecksAccessOnSave()
    {
        var factory = Factory();
        await using (var db = await factory.CreateDbContextAsync()) await db.Database.MigrateAsync();
        var capability = "operatorui." + Guid.NewGuid().ToString("N");
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Capabilities.Add(new CloudCapabilityDefinition { Id = capability, Name = "Operator region test", Description = "Test", ContractVersion = "1", BillingUnit = "request", PlatformAvailability = "available", Dependencies = "[]", Modalities = "[]", AllowedRegions = "[]" });
            await db.SaveChangesAsync();
        }
        var config = ConfigureCommon(factory, "operator", operatorAccess: true);
        Services.AddSingleton<CloudOperatorAccess>(); Services.AddSingleton<ICloudCatalogRegionsStore, EfCloudCatalogRegionsStore>();
        Services.AddSingleton<CloudCatalogRegionsService>(); Services.AddSingleton<CloudOperatorCatalogRegionsService>();
        var component = Render<OperatorRegions>();
        component.WaitForAssertion(() => Assert.Contains(capability, component.Markup));
        component.Find("#catalog-region-service").Change(capability);
        component.WaitForAssertion(() => Assert.Contains("Unverified", component.Markup));
        component.Find("#catalog-region-list").Change("region-b\nregion-a");
        component.Find("#catalog-region-verified").Change(true);
        component.FindAll("button").Single(x => x.TextContent == "Save declaration").Click();
        component.WaitForAssertion(() => Assert.Contains("Region declaration saved.", component.Markup));
        await using (var db = await factory.CreateDbContextAsync())
        {
            var saved = await db.Capabilities.SingleAsync(x => x.Id == capability);
            Assert.True(saved.AllowedRegionsVerified);
            Assert.Equal(new[] { "region-a", "region-b" }, System.Text.Json.JsonSerializer.Deserialize<string[]>(saved.AllowedRegions));
            await db.Capabilities.Where(x => x.Id == capability).ExecuteUpdateAsync(x => x.SetProperty(y => y.AllowedRegions, "[\"region-c\"]"));
        }
        component.FindAll("button").Single(x => x.TextContent == "Save declaration").Click();
        component.WaitForAssertion(() => Assert.Contains("declaration changed", component.Markup));
        Assert.Contains("region-a", component.Find("#catalog-region-list").GetAttribute("value"));
        component.FindAll("button").Single(x => x.TextContent == "Reload saved declaration").Click();
        component.WaitForAssertion(() => Assert.Contains("region-c", component.Find("#catalog-region-list").GetAttribute("value")));
        config["OpenConnect:Cloud:Operators:0:Subject"] = "different";
        component.Find("#catalog-region-verified").Change(false);
        component.FindAll("button").Single(x => x.TextContent == "Save declaration").Click();
        component.WaitForAssertion(() => Assert.Contains("not saved", component.Markup));
        await using (var db = await factory.CreateDbContextAsync())
            Assert.True((await db.Capabilities.SingleAsync(x => x.Id == capability)).AllowedRegionsVerified);
    }

    private IConfiguration ConfigureCommon(IDbContextFactory<CloudControlPlaneDbContext> factory, string subjectId, bool operatorAccess = false)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OpenConnect:Cloud:Identity:Enabled"] = "true", ["OpenConnect:Cloud:Identity:Authority"] = "https://issuer.example",
            ["OpenConnect:Cloud:Operators:0:Issuer"] = "https://issuer.example", ["OpenConnect:Cloud:Operators:0:Subject"] = operatorAccess ? subjectId : "other"
        }).Build();
        Services.AddSingleton<IConfiguration>(config); Services.AddSingleton(factory); Services.AddSingleton(TimeProvider.System);
        Services.AddSingleton<ICloudCapabilityReader, EfCloudCapabilityReader>(); Services.AddSingleton<ICloudCapabilitySelectionStore, EfCloudCapabilitySelectionStore>(); Services.AddSingleton<CloudCapabilityService>();
        Services.AddSingleton<AuthenticationStateProvider>(new TestAuthentication(subjectId));
        return config;
    }

    private static IDbContextFactory<CloudControlPlaneDbContext> Factory() => new TestFactory(new DbContextOptionsBuilder<CloudControlPlaneDbContext>()
        .UseNpgsql(Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_TEST_DATABASE")).Options);

    private sealed class TestAuthentication(string subjectId) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, subjectId, ClaimValueTypes.String, "https://issuer.example")], "test"))));
    }

    private sealed class TestFactory(DbContextOptions<CloudControlPlaneDbContext> options) : IDbContextFactory<CloudControlPlaneDbContext>
    {
        public CloudControlPlaneDbContext CreateDbContext() => new(options);
    }
}
