using Microsoft.EntityFrameworkCore;
using OpenConnect.Docs.Cloud;
using Xunit;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Security.Claims;

namespace OpenConnect.Docs.Tests;

[Collection("Cloud PostgreSQL")]
public sealed class CloudDeploymentPostgresTests
{
    [CloudPostgresFact]
    public async Task InventorySupportsMultipleDeploymentsAndRevisionConflicts()
    {
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_TEST_DATABASE")).Options;
        IDbContextFactory<CloudControlPlaneDbContext> factory = new TestFactory(options);
        await using (var db = await factory.CreateDbContextAsync()) await db.Database.MigrateAsync();
        var capability = "test." + Guid.NewGuid().ToString("N");
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Capabilities.Add(new CloudCapabilityDefinition { Id = capability, Name = "Test", Description = "Test", ContractVersion = "1", BillingUnit = "request", PlatformAvailability = "unavailable", Dependencies = "[]", Modalities = "[]", AllowedRegions = "[]" });
            await db.SaveChangesAsync();
        }
        var registry = new CloudDeploymentRegistry(new EfCloudDeploymentStore(factory));
        CloudWorkerDeployment Deployment(string endpoint) => new() { Id = Guid.NewGuid(), CapabilityId = capability,
            EndpointReference = endpoint, Transport = "https-mtls", Region = "region-a", ContractVersion = "v1",
            ImageDigest = "sha256:" + new string('a', 64), ConcurrencyLimit = 10, Enabled = false };
        var first = Deployment("worker-a-" + Guid.NewGuid().ToString("N"));
        var second = Deployment("worker-b-" + Guid.NewGuid().ToString("N"));
        Assert.Equal("succeeded", await registry.PutAsync(first, 0, CancellationToken.None));
        Assert.Equal("succeeded", await registry.PutAsync(second, 0, CancellationToken.None));
        Assert.Equal(2, (await registry.ListAsync(capability, 0, 10, CancellationToken.None)).Count);
        Assert.Equal("conflict", await registry.PutAsync(first, 0, CancellationToken.None));
        first.Enabled = true;
        Assert.Equal("succeeded", await registry.PutAsync(first, 1, CancellationToken.None));
        Assert.Equal("conflict", await registry.PutAsync(first, 1, CancellationToken.None));
        Assert.Equal(2, (await registry.ListAsync(capability, 0, 10, CancellationToken.None)).Single(x => x.Id == first.Id).Revision);
        var duplicate = Deployment(first.EndpointReference);
        Assert.Equal("conflict", await registry.PutAsync(duplicate, 0, CancellationToken.None));
        var invalid = Deployment("https://attacker.example");
        Assert.Equal("invalid_request", await registry.PutAsync(invalid, 0, CancellationToken.None));
        invalid.EndpointReference = "worker-c";
        invalid.ModelId = "model-without-digest";
        Assert.Equal("invalid_request", await registry.PutAsync(invalid, 0, CancellationToken.None));
        await VerifyOperatorHttpAsync(factory, capability);
    }

    private static async Task VerifyOperatorHttpAsync(IDbContextFactory<CloudControlPlaneDbContext> factory, string capability)
    {
        const string issuer = "https://operator-issuer.example";
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OpenConnect:Cloud:Identity:Enabled"] = "true",
            ["OpenConnect:Cloud:Identity:Authority"] = issuer,
            ["OpenConnect:Cloud:Operators:0:Issuer"] = issuer,
            ["OpenConnect:Cloud:Operators:0:Subject"] = "operator"
        });
        builder.Services.AddSingleton(factory);
        builder.Services.AddSingleton<CloudOperatorAccess>();
        builder.Services.AddScoped<ICloudDeploymentStore, EfCloudDeploymentStore>();
        builder.Services.AddScoped<CloudDeploymentRegistry>();
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
        {
            options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; };
            options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
        });
        builder.Services.AddAuthorization(options => options.AddPolicy("CloudApi", policy =>
        {
            policy.AddAuthenticationSchemes(CookieAuthenticationDefaults.AuthenticationScheme);
            policy.RequireAuthenticatedUser();
        }));
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/test-sign-in/{subject}", async (string subject, HttpContext context) =>
        {
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, subject, ClaimValueTypes.String, issuer)], CookieAuthenticationDefaults.AuthenticationScheme);
            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
        });
        app.MapGroup("/cloud/api/v1").RequireAuthorization("CloudApi").MapCloudDeploymentEndpoints();
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(address) };
        var path = $"/cloud/api/v1/operator/deployments?capability={capability}&limit=1";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
        await client.GetAsync("/test-sign-in/ordinary-user");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(path)).StatusCode);
        await client.GetAsync("/test-sign-in/operator");
        var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("nextOffset", json);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/cloud/api/v1/operator/deployments?capability=https://example.com")).StatusCode);
    }

    private sealed class TestFactory(DbContextOptions<CloudControlPlaneDbContext> options) : IDbContextFactory<CloudControlPlaneDbContext>
    {
        public CloudControlPlaneDbContext CreateDbContext() => new(options);
    }
}
