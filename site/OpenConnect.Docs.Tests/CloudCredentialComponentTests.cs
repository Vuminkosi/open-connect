using System.Security.Claims;
using Bunit;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using OpenConnect.Docs.Cloud;
using OpenConnect.Docs.Components.Pages.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

[Collection("Cloud PostgreSQL")]
public sealed class CloudCredentialComponentTests : BunitContext
{
    [CloudPostgresFact]
    public async Task OwnerCanCreateRotateDismissAndRevokeCredential()
    {
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_TEST_DATABASE")).Options;
        IDbContextFactory<CloudControlPlaneDbContext> factory = new TestFactory(options);
        await using (var db = await factory.CreateDbContextAsync()) await db.Database.MigrateAsync();
        var subject = new CloudSubject("https://test.example", Guid.NewGuid().ToString("N"));
        var tenantStore = new EfCloudTenantStore(factory, TimeProvider.System);
        var tenant = await tenantStore.CreateTenantAsync(subject, "Portal test", CancellationToken.None);
        var application = (await tenantStore.CreateApplicationAsync(subject, tenant.Id, "App", "Development", CancellationToken.None)).Application!;
        var capabilityId = "test." + Guid.NewGuid().ToString("N");
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Capabilities.Add(new CloudCapabilityDefinition { Id = capabilityId, Name = "Portal service", Description = "Test", ContractVersion = "1", BillingUnit = "request", PlatformAvailability = "available", Dependencies = "[]", Modalities = "[]", AllowedRegions = "[]" });
            db.TenantEntitlements.Add(new CloudTenantEntitlement { TenantId = tenant.Id, CapabilityId = capabilityId, PlanGranted = true, Revision = 1 });
            db.TenantCapabilitySelections.Add(new CloudTenantCapabilitySelection { TenantId = tenant.Id, CapabilityId = capabilityId, Enabled = true, Revision = 1 });
            db.ApplicationCapabilitySelections.Add(new CloudApplicationCapabilitySelection { TenantId = tenant.Id, ApplicationId = application.Id, CapabilityId = capabilityId, Enabled = true, Revision = 1 });
            await db.SaveChangesAsync();
        }
        Services.AddSingleton(factory);
        Services.AddSingleton(TimeProvider.System);
        Services.AddSingleton<ICloudTenantStore>(tenantStore);
        Services.AddScoped<CloudControlPlaneService>();
        Services.AddScoped<ICloudCapabilityReader, EfCloudCapabilityReader>();
        Services.AddScoped<CloudCapabilityService>();
        Services.AddScoped<CloudCredentialService>();
        Services.AddSingleton<AuthenticationStateProvider>(new TestAuthenticationStateProvider(subject.SubjectId));
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["OpenConnect:Cloud:Identity:Authority"] = subject.Issuer }).Build());
        var component = Render<ApplicationCredentials>(parameters => parameters.Add(x => x.TenantId, tenant.Id).Add(x => x.ApplicationId, application.Id));
        component.WaitForAssertion(() => Assert.Contains("Portal service", component.Markup));
        component.Find("input[type=checkbox]").Change(true);
        component.Find("#credential-expiry").Change(DateTime.UtcNow.AddDays(1).ToString("yyyy-MM-ddTHH:mm"));
        component.FindAll("button").Single(x => x.TextContent.Contains("Create credential")).Click();
        component.WaitForAssertion(() => Assert.True(component.Markup.Contains("Save this key now", StringComparison.Ordinal), component.Markup));
        var secret = component.Find("code").TextContent;
        Assert.StartsWith("oc_dev_", secret);
        component.FindAll("button").Single(x => x.TextContent == "Done").Click();
        Assert.DoesNotContain(secret, component.Markup);
        component.FindAll("button").Single(x => x.TextContent.Contains("Rotate using expiration")).Click();
        component.WaitForAssertion(() => Assert.Contains("Save this key now", component.Markup));
        var replacement = component.Find("code").TextContent;
        Assert.NotEqual(secret, replacement);
        var validator = new CloudCredentialService(factory, new CloudCapabilityService(factory, new EfCloudCapabilityReader(factory), TimeProvider.System), TimeProvider.System);
        Assert.Null(await validator.ValidateAsync(secret, CancellationToken.None));
        Assert.NotNull(await validator.ValidateAsync(replacement, CancellationToken.None));
        component.Render(parameters => parameters.Add(x => x.ApplicationId, Guid.NewGuid()));
        component.WaitForAssertion(() => Assert.Contains("The application was not found or you do not have access.", component.Markup));
        Assert.DoesNotContain(replacement, component.Markup);
        component.Render(parameters => parameters.Add(x => x.ApplicationId, application.Id));
        component.WaitForAssertion(() => Assert.Contains("Portal service", component.Markup));
        Assert.DoesNotContain(replacement, component.Markup);
        component.FindAll("button").Single(x => x.TextContent == "Revoke").Click();
        component.WaitForAssertion(() => Assert.DoesNotContain(component.FindAll("button"), x => x.TextContent == "Revoke"));
        Assert.Null(await validator.ValidateAsync(replacement, CancellationToken.None));
    }

    [Fact]
    public void UnconfiguredControlPlaneDoesNotPresentEmptyCredentialListOrActions()
    {
        Services.AddSingleton<AuthenticationStateProvider>(new TestAuthenticationStateProvider());
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        var component = Render<ApplicationCredentials>(parameters => parameters
            .Add(x => x.TenantId, Guid.NewGuid())
            .Add(x => x.ApplicationId, Guid.NewGuid()));

        Assert.Contains("Credential management is unavailable.", component.Markup);
        Assert.DoesNotContain("No credentials are available.", component.Markup);
        Assert.DoesNotContain("Create credential", component.Markup);
    }

    private sealed class TestAuthenticationStateProvider(string? subjectId = null) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(subjectId is null
                ? new ClaimsIdentity() : new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, subjectId)], "test"))));
    }

    private sealed class TestFactory(DbContextOptions<CloudControlPlaneDbContext> options) : IDbContextFactory<CloudControlPlaneDbContext>
    {
        public CloudControlPlaneDbContext CreateDbContext() => new(options);
    }
}
