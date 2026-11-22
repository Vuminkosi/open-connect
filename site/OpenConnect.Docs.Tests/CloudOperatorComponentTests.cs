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
public sealed class CloudOperatorComponentTests : BunitContext
{
    [Fact]
    public void NonOperatorCannotSeeInventoryControls()
    {
        const string issuer = "https://issuer.example";
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OpenConnect:Cloud:Identity:Enabled"] = "true",
            ["OpenConnect:Cloud:Identity:Authority"] = issuer,
            ["OpenConnect:Cloud:Operators:0:Issuer"] = issuer,
            ["OpenConnect:Cloud:Operators:0:Subject"] = "operator"
        }).Build();
        Services.AddSingleton<IConfiguration>(config);
        Services.AddSingleton<CloudOperatorAccess>();
        Services.AddSingleton<AuthenticationStateProvider>(new TestAuthentication(issuer, "other"));
        var component = Render<OperatorDeployments>();
        Assert.Contains("Operator access is required.", component.Markup);
        Assert.Empty(component.FindAll("form"));
    }

    [CloudPostgresFact]
    public async Task OperatorRegistersAndEnablesDeploymentWithAudit()
    {
        const string issuer = "https://issuer.example";
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_TEST_DATABASE")).Options;
        IDbContextFactory<CloudControlPlaneDbContext> factory = new TestFactory(options);
        var capability = "test." + Guid.NewGuid().ToString("N");
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.MigrateAsync();
            db.Capabilities.Add(new CloudCapabilityDefinition { Id = capability, Name = "Operator test", Description = "Test", ContractVersion = "1", BillingUnit = "request", PlatformAvailability = "not_configured", Dependencies = "[]", Modalities = "[]", AllowedRegions = "[]" });
            await db.SaveChangesAsync();
        }
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OpenConnect:Cloud:Identity:Enabled"] = "true",
            ["OpenConnect:Cloud:Identity:Authority"] = issuer,
            ["OpenConnect:Cloud:Operators:0:Issuer"] = issuer,
            ["OpenConnect:Cloud:Operators:0:Subject"] = "operator"
        }).Build();
        Services.AddSingleton<IConfiguration>(config);
        Services.AddSingleton(factory);
        Services.AddSingleton(TimeProvider.System);
        Services.AddSingleton<CloudOperatorAccess>();
        Services.AddSingleton<ICloudCapabilityReader, EfCloudCapabilityReader>();
        Services.AddSingleton<ICloudCapabilitySelectionStore, EfCloudCapabilitySelectionStore>();
        Services.AddSingleton<CloudCapabilityService>();
        Services.AddSingleton<ICloudDeploymentStore, EfCloudDeploymentStore>();
        Services.AddSingleton<CloudDeploymentRegistry>();
        Services.AddSingleton<CloudOperatorDeploymentService>();
        Services.AddSingleton<AuthenticationStateProvider>(new TestAuthentication(issuer, "operator"));
        var component = Render<OperatorDeployments>();
        component.WaitForAssertion(() => Assert.Contains(capability, component.Markup));
        component.Find("#operator-capability").Change(capability);
        component.Find("#deployment-endpoint").Change("deployment-" + Guid.NewGuid().ToString("N"));
        component.Find("#deployment-region").Change("region-a");
        component.Find("#deployment-contract").Change("v1");
        component.Find("#deployment-image").Change("sha256:" + new string('a', 64));
        component.Find("#deployment-limit").Change("2");
        component.Find("form").Submit();
        component.WaitForAssertion(() => Assert.Contains("Deployment saved.", component.Markup));
        component.WaitForAssertion(() => Assert.Contains("Disabled", component.Markup));
        component.FindAll("button").Single(x => x.TextContent == "Enable").Click();
        component.WaitForAssertion(() => Assert.Contains("Enabled", component.Markup));
        await using var verify = await factory.CreateDbContextAsync();
        var deployment = await verify.WorkerDeployments.SingleAsync(x => x.CapabilityId == capability);
        Assert.True(deployment.Enabled);
        Assert.Equal(2, deployment.Revision);
        Assert.Equal(2, await verify.AuditEvents.CountAsync(x => x.ResourceId == deployment.Id.ToString() && x.Outcome == "succeeded"));
        config["OpenConnect:Cloud:Operators:0:Subject"] = "different-operator";
        component.FindAll("button").Single(x => x.TextContent == "Disable").Click();
        component.WaitForAssertion(() => Assert.Contains("Deployment change was not authorized.", component.Markup));
        await using var afterRevocation = await factory.CreateDbContextAsync();
        Assert.True((await afterRevocation.WorkerDeployments.SingleAsync(x => x.Id == deployment.Id)).Enabled);
        Assert.Contains(await afterRevocation.AuditEvents.Where(x => x.ResourceId == deployment.Id.ToString()).ToListAsync(), x => x.Outcome == "forbidden");
    }

    private sealed class TestAuthentication(string issuer, string subject) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, subject, ClaimValueTypes.String, issuer)], "test"))));
    }

    private sealed class TestFactory(DbContextOptions<CloudControlPlaneDbContext> options) : IDbContextFactory<CloudControlPlaneDbContext>
    {
        public CloudControlPlaneDbContext CreateDbContext() => new(options);
    }
}
