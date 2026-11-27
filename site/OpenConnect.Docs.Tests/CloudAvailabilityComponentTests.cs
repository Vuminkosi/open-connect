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
public sealed class CloudAvailabilityComponentTests : BunitContext
{
    [CloudPostgresFact]
    public async Task OperatorCanDeclareAvailabilityAndReceivesStaleAndDeploymentFeedback()
    {
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>().UseNpgsql(Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_TEST_DATABASE")).Options;
        IDbContextFactory<CloudControlPlaneDbContext> factory = new Factory(options);
        var capability = "availabilityui." + Guid.NewGuid().ToString("N");
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.MigrateAsync();
            db.Capabilities.Add(new CloudCapabilityDefinition
            {
                Id = capability,
                Name = "Availability UI",
                Description = "Test",
                ContractVersion = "v1",
                BillingUnit = "request",
                PlatformAvailability = "not_configured",
                Dependencies = "[]",
                Modalities = "[]",
                AllowedRegions = "[]"
            });
            await db.SaveChangesAsync();
        }
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OpenConnect:Cloud:Identity:Enabled"] = "true",
            ["OpenConnect:Cloud:Identity:Authority"] = "https://issuer.example",
            ["OpenConnect:Cloud:Operators:0:Issuer"] = "https://issuer.example",
            ["OpenConnect:Cloud:Operators:0:Subject"] = "operator"
        }).Build();
        Services.AddSingleton<IConfiguration>(config);
        Services.AddSingleton(factory);
        Services.AddSingleton(TimeProvider.System);
        Services.AddSingleton<AuthenticationStateProvider>(new TestAuthentication());
        Services.AddSingleton<CloudOperatorAccess>();
        Services.AddSingleton<ICloudCapabilityReader, EfCloudCapabilityReader>();
        Services.AddSingleton<ICloudCapabilitySelectionStore, EfCloudCapabilitySelectionStore>();
        Services.AddSingleton<CloudCapabilityService>();
        Services.AddSingleton<ICloudCapabilityAvailabilityStore, EfCloudCapabilityAvailabilityStore>();
        Services.AddSingleton<CloudCapabilityAvailabilityService>();
        Services.AddSingleton<CloudOperatorCapabilityAvailabilityService>();
        var component = Render<OperatorAvailability>();
        component.WaitForAssertion(() => Assert.Contains(capability, component.Markup));
        component.Find("#availability-service").Change(capability);
        component.WaitForAssertion(() => Assert.Contains("Revision 1", component.Markup));
        component.Find("#availability-state").Change("available");
        component.FindAll("button").Single(x => x.TextContent == "Save availability").Click();
        component.WaitForAssertion(() => Assert.Contains("Enable a deployment", component.Markup));
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.WorkerDeployments.Add(new CloudWorkerDeployment
            {
                Id = Guid.NewGuid(),
                CapabilityId = capability,
                EndpointReference = "test-" + Guid.NewGuid().ToString("N"),
                Transport = "https-mtls",
                Region = "test",
                ContractVersion = "v1",
                ImageDigest = "sha256:" + new string('a', 64),
                Enabled = true,
                ConcurrencyLimit = 1
            });
            await db.SaveChangesAsync();
        }
        component.FindAll("button").Single(x => x.TextContent == "Save availability").Click();
        component.WaitForAssertion(() => Assert.Contains("Availability saved.", component.Markup));
        await using (var db = await factory.CreateDbContextAsync())
            await db.Capabilities.Where(x => x.Id == capability).ExecuteUpdateAsync(x => x.SetProperty(y => y.PlatformAvailability, "not_configured"));
        component.FindAll("button").Single(x => x.TextContent == "Save availability").Click();
        component.WaitForAssertion(() => Assert.Contains("Availability changed", component.Markup));
        component.FindAll("button").Single(x => x.TextContent == "Reload saved availability").Click();
        component.WaitForAssertion(() => Assert.Contains("Revision 3", component.Markup));
        config["OpenConnect:Cloud:Operators:0:Subject"] = "other";
        component.Find("#availability-state").Change("available");
        component.FindAll("button").Single(x => x.TextContent == "Save availability").Click();
        component.WaitForAssertion(() => Assert.Contains("not saved", component.Markup));
    }

    private sealed class TestAuthentication : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "operator", ClaimValueTypes.String, "https://issuer.example")], "test"))));
    }
    private sealed class Factory(DbContextOptions<CloudControlPlaneDbContext> options) : IDbContextFactory<CloudControlPlaneDbContext>
    {
        public CloudControlPlaneDbContext CreateDbContext() => new(options);
    }
}
