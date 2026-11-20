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
public sealed class CloudInferenceModelPolicyComponentTests : BunitContext
{
    [CloudPostgresFact]
    public async Task OwnerEditsModelsAndTenantChangesClearTheSelection()
    {
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>().UseNpgsql(Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_TEST_DATABASE")).Options;
        IDbContextFactory<CloudControlPlaneDbContext> factory = new TestFactory(options);
        await using (var db = await factory.CreateDbContextAsync()) await db.Database.MigrateAsync();
        var owner = new CloudSubject("https://issuer.example", "owner-" + Guid.NewGuid().ToString("N"));
        var tenants = new EfCloudTenantStore(factory, TimeProvider.System);
        var tenant = await tenants.CreateTenantAsync(owner, "Inference UI", default);
        var policies = new CloudInferenceModelPolicyService(new EfCloudInferenceModelPolicyStore(factory, TimeProvider.System));
        Services.AddSingleton(factory); Services.AddSingleton(TimeProvider.System);
        Services.AddSingleton<ICloudTenantStore>(tenants); Services.AddSingleton<CloudControlPlaneService>();
        Services.AddSingleton<ICloudInferenceModelPolicyStore, EfCloudInferenceModelPolicyStore>(); Services.AddSingleton<CloudInferenceModelPolicyService>();
        Services.AddSingleton<AuthenticationStateProvider>(new TestAuthentication(owner.SubjectId));
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["OpenConnect:Cloud:Identity:Enabled"] = "true", ["OpenConnect:Cloud:Identity:Authority"] = owner.Issuer }).Build());
        var component = Render<TenantInferenceModels>(p => p.Add(x => x.TenantId, tenant.Id));
        component.WaitForAssertion(() => Assert.Contains("No models are allowed", component.Markup));
        Assert.Contains("does not make inference available", component.Markup);
        component.Find("#model-id").Change("registry/model-v1");
        component.FindAll("button").Single(x => x.TextContent == "Add model").Click();
        component.FindAll("button").Single(x => x.TextContent == "Save model policy").Click();
        component.WaitForAssertion(() => Assert.Contains("Model policy saved", component.Markup));
        Assert.True(await policies.IsAllowedAsync(tenant.Id, "registry/model-v1", default));
        Assert.Equal("succeeded", (await policies.PutAsync(owner, tenant.Id, ["other-model"], 1, default)).Status);
        component.FindAll("button").Single(x => x.TextContent == "Save model policy").Click();
        component.WaitForAssertion(() => Assert.Contains("policy changed", component.Markup));
        component.Render(p => p.Add(x => x.TenantId, Guid.NewGuid()));
        component.WaitForAssertion(() => Assert.Contains("not found or the policy is unavailable", component.Markup));
        Assert.DoesNotContain("registry/model-v1", component.Markup);
    }

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
