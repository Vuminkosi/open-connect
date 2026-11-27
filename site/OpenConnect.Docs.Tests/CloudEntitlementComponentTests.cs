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
public sealed class CloudEntitlementComponentTests : BunitContext
{
    [CloudPostgresFact]
    public async Task OperatorGrantsRevokesAndDetectsStaleChanges()
    {
        var factory = Factory();
        var tenant = Guid.NewGuid();
        var capability = "entitlementui." + Guid.NewGuid().ToString("N");
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.MigrateAsync();
            db.Tenants.Add(new CloudTenant { Id = tenant, Name = "Entitlement UI", CreatedAt = DateTimeOffset.UtcNow });
            db.Capabilities.Add(new CloudCapabilityDefinition { Id = capability, Name = "Entitlement UI test", Description = "Test", ContractVersion = "1", BillingUnit = "request", PlatformAvailability = "available", Dependencies = "[]", Modalities = "[]", AllowedRegions = "[]" });
            await db.SaveChangesAsync();
        }
        var config = ConfigureCommon(factory, "operator", true);
        Services.AddSingleton<CloudOperatorAccess>();
        Services.AddSingleton<ICloudEntitlementAdministrationStore, EfCloudEntitlementAdministrationStore>();
        Services.AddSingleton<CloudEntitlementAdministration>();
        Services.AddSingleton<CloudOperatorEntitlementAdministration>();
        var component = Render<OperatorEntitlements>();
        component.WaitForAssertion(() => Assert.Contains(capability, component.Markup));
        component.Find("#entitlement-tenant").Change(tenant.ToString());
        component.Find("#entitlement-service").Change(capability);
        component.FindAll("button").Single(x => x.TextContent == "Load entitlement").Click();
        component.WaitForAssertion(() => Assert.Contains("No entitlement is recorded", component.Markup));
        component.Find("#entitlement-granted").Change(true);
        component.FindAll("button").Single(x => x.TextContent == "Save entitlement").Click();
        component.WaitForAssertion(() => Assert.Contains("Entitlement saved.", component.Markup));
        await using (var db = await factory.CreateDbContextAsync())
        {
            Assert.True((await db.TenantEntitlements.SingleAsync(x => x.TenantId == tenant && x.CapabilityId == capability)).PlanGranted);
            await db.TenantEntitlements.Where(x => x.TenantId == tenant && x.CapabilityId == capability).ExecuteUpdateAsync(x => x.SetProperty(y => y.PlanGranted, false));
        }
        component.FindAll("button").Single(x => x.TextContent == "Save entitlement").Click();
        component.WaitForAssertion(() => Assert.Contains("entitlement changed", component.Markup));
        component.FindAll("button").Single(x => x.TextContent == "Load entitlement").Click();
        component.WaitForAssertion(() => Assert.False(component.Find("#entitlement-granted").HasAttribute("checked")));
        config["OpenConnect:Cloud:Operators:0:Subject"] = "different";
        component.Find("#entitlement-granted").Change(true);
        component.FindAll("button").Single(x => x.TextContent == "Save entitlement").Click();
        component.WaitForAssertion(() => Assert.Contains("not saved", component.Markup));
        await using var verify = await factory.CreateDbContextAsync();
        Assert.False((await verify.TenantEntitlements.SingleAsync(x => x.TenantId == tenant && x.CapabilityId == capability)).PlanGranted);
    }

    private IConfiguration ConfigureCommon(IDbContextFactory<CloudControlPlaneDbContext> factory, string subjectId, bool operatorAccess = false)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OpenConnect:Cloud:Identity:Enabled"] = "true",
            ["OpenConnect:Cloud:Identity:Authority"] = "https://issuer.example",
            ["OpenConnect:Cloud:Operators:0:Issuer"] = "https://issuer.example",
            ["OpenConnect:Cloud:Operators:0:Subject"] = operatorAccess ? subjectId : "other"
        }).Build();
        Services.AddSingleton<IConfiguration>(config);
        Services.AddSingleton(factory);
        Services.AddSingleton(TimeProvider.System);
        Services.AddSingleton<ICloudCapabilityReader, EfCloudCapabilityReader>();
        Services.AddSingleton<ICloudCapabilitySelectionStore, EfCloudCapabilitySelectionStore>();
        Services.AddSingleton<CloudCapabilityService>();
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
