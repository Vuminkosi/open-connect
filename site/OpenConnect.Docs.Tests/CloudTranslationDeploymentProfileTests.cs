using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Docs.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

[Collection("Cloud PostgreSQL")]
public sealed class CloudTranslationDeploymentProfileTests
{
    [CloudPostgresFact]
    public async Task ProfileUpdatesAreRevisionFencedAndBoundToDeploymentRevision()
    {
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_TEST_DATABASE")).Options;
        IDbContextFactory<CloudControlPlaneDbContext> factory = new TestFactory(options);
        await using (var db = await factory.CreateDbContextAsync())
            await db.Database.MigrateAsync();
        var deploymentId = Guid.NewGuid();
        var tenant = await new EfCloudTenantStore(factory, TimeProvider.System)
            .CreateTenantAsync(new CloudSubject("https://issuer.example", Guid.NewGuid().ToString("N")), "Translation matching", default);
        await using (var db = await factory.CreateDbContextAsync())
        {
            var capability = await db.Capabilities.SingleAsync(x => x.Id == "translation.translate");
            capability.AllowedRegions = "[\"region-a\"]";
            capability.AllowedRegionsVerified = true;
            db.RegionPolicies.Add(new CloudRegionPolicy { TenantId = tenant.Id, AllowedRegions = "[\"region-a\"]", Revision = 1 });
            db.WorkerDeployments.Add(new CloudWorkerDeployment
            {
                Id = deploymentId,
                CapabilityId = "translation.translate",
                EndpointReference = "nllb-" + Guid.NewGuid().ToString("N"),
                Transport = "https-mtls",
                Region = "region-a",
                ContractVersion = "v1",
                ModelId = "nllb",
                ModelRevision = "revision-1",
                ModelDigest = "sha256:" + new string('a', 64),
                ImageDigest = "sha256:" + new string('b', 64),
                ConcurrencyLimit = 1,
                Enabled = true,
                Revision = 1
            });
            await db.SaveChangesAsync();
        }
        var service = new CloudTranslationDeploymentProfiles(new EfCloudTranslationDeploymentProfileStore(factory, TimeProvider.System));
        var actor = new CloudSubject("https://issuer.example", Guid.NewGuid().ToString("N"));
        var profile = new CloudTranslationDeploymentProfile(deploymentId, 1, 0, new string('a', 64), [new("eng_Latn", "fra_Latn")]);
        Assert.Equal("invalid_request", await service.PutAsync(actor, "trace", profile with { TokenizerHash = "invalid" }, 0, default));
        Assert.Equal("invalid_request", await service.PutAsync(actor, "trace", profile with { LanguagePairs = [new("eng_Latn", "eng_Latn")] }, 0, default));
        Assert.Equal("succeeded", await service.PutAsync(actor, "trace", profile, 0, default));
        var stored = await service.GetAsync(deploymentId, default);
        Assert.NotNull(stored);
        Assert.Equal(1, stored.Revision);
        Assert.Equal(1, stored.DeploymentRevision);
        Assert.Equal(profile.LanguagePairs, stored.LanguagePairs);
        Assert.Equal("conflict", await service.PutAsync(actor, "trace", profile, 0, default));
        var changed = profile with { TokenizerHash = new string('b', 64), LanguagePairs = [new("fra_Latn", "eng_Latn")] };
        var results = await Task.WhenAll(service.PutAsync(actor, "trace", changed, 1, default), service.PutAsync(actor, "trace", changed, 1, default));
        Assert.Single(results, result => result == "succeeded");
        Assert.Single(results, result => result == "conflict");
        stored = await service.GetAsync(deploymentId, default);
        Assert.NotNull(stored);
        Assert.Equal(2, stored.Revision);
        Assert.Equal(changed.TokenizerHash, stored.TokenizerHash);
        Assert.Equal(changed.LanguagePairs, stored.LanguagePairs);
        var matcher = new CloudTranslationDeploymentMatch(new EfCloudTranslationDeploymentReader(factory));
        var match = new CloudTranslationDeploymentMatchRequest(deploymentId, await EndpointAsync(factory, deploymentId), "region-a", "v1", "nllb", "revision-1",
            "sha256:" + new string('a', 64), "sha256:" + new string('b', 64), changed.TokenizerHash, "fra_Latn", "eng_Latn");
        Assert.True(await matcher.MatchesAsync(tenant.Id, match, default));
        Assert.False(await matcher.MatchesAsync(Guid.NewGuid(), match, default));
        Assert.False(await matcher.MatchesAsync(tenant.Id, match with { SourceLang = "eng_Latn", TargetLang = "fra_Latn" }, default));
        Assert.False(await matcher.MatchesAsync(tenant.Id, match with { TokenizerHash = new string('a', 64) }, default));
        Assert.False(await matcher.MatchesAsync(tenant.Id, match with { ImageDigest = "sha256:" + new string('c', 64) }, default));
        await using (var db = await factory.CreateDbContextAsync())
        {
            var deployment = await db.WorkerDeployments.SingleAsync(x => x.Id == deploymentId);
            deployment.Revision = 2;
            await db.SaveChangesAsync();
        }
        Assert.False(await matcher.MatchesAsync(tenant.Id, match, default));
        Assert.Equal("conflict", await service.PutAsync(actor, "trace", changed, 2, default));
        Assert.Equal("succeeded", await service.PutAsync(actor, "trace", changed with { DeploymentRevision = 2 }, 2, default));
        Assert.True(await matcher.MatchesAsync(tenant.Id, match, default));
        await VerifyHttpAsync(factory, deploymentId);
    }

    private static async Task<string> EndpointAsync(IDbContextFactory<CloudControlPlaneDbContext> factory, Guid deploymentId)
    {
        await using var db = await factory.CreateDbContextAsync();
        return (await db.WorkerDeployments.SingleAsync(x => x.Id == deploymentId)).EndpointReference;
    }

    private static async Task VerifyHttpAsync(IDbContextFactory<CloudControlPlaneDbContext> factory, Guid deploymentId)
    {
        const string issuer = "https://operator.example";
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
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<CloudOperatorAccess>();
        builder.Services.AddScoped<ICloudTranslationDeploymentProfileStore, EfCloudTranslationDeploymentProfileStore>();
        builder.Services.AddScoped<CloudTranslationDeploymentProfiles>();
        builder.Services.AddAntiforgery(x => x.HeaderName = "X-CSRF-TOKEN");
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(x =>
        {
            x.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; };
            x.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
        });
        builder.Services.AddAuthorization(x => x.AddPolicy("CloudApi", policy => policy.AddAuthenticationSchemes(CookieAuthenticationDefaults.AuthenticationScheme).RequireAuthenticatedUser()));
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/test-sign-in/{subject}", async (string subject, HttpContext context) => await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, subject, ClaimValueTypes.String, issuer)], CookieAuthenticationDefaults.AuthenticationScheme))));
        app.MapGet("/test-antiforgery", (HttpContext context, IAntiforgery antiforgery) => Results.Ok(new { token = antiforgery.GetAndStoreTokens(context).RequestToken }));
        app.MapGroup("/cloud/api/v1").RequireAuthorization("CloudApi").MapCloudTranslationDeploymentProfileEndpoints();
        await app.StartAsync();
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        var path = $"/cloud/api/v1/operator/deployments/{deploymentId:D}/translation-profile";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
        await client.GetAsync("/test-sign-in/ordinary-user");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsync(path, null)).StatusCode);
        await client.GetAsync("/test-sign-in/operator");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsync(path, null)).StatusCode);
        using var tokenJson = JsonDocument.Parse(await client.GetStringAsync("/test-antiforgery"));
        var token = tokenJson.RootElement.GetProperty("token").GetString()!;
        using (var request = Write(path, token, 3))
        {
            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            Assert.Equal("\"4\"", response.Headers.ETag?.ToString());
        }
        using (var request = Write(path, token, 3))
            Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(request)).StatusCode);
        using (var request = Write(path, token, 4))
        {
            request.Content = new StringContent("{\"deploymentRevision\":2,\"tokenizerHash\":\"" + new string('a', 64) +
                "\",\"languagePairs\":[{\"sourceLang\":\"eng_Latn\",\"SourceLang\":\"fra_Latn\",\"targetLang\":\"fra_Latn\"}]}", System.Text.Encoding.UTF8, "application/json");
            Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(request)).StatusCode);
        }
        var read = await client.GetAsync(path);
        Assert.Equal("\"4\"", read.Headers.ETag?.ToString());
        Assert.Equal("no-store", read.Headers.CacheControl?.ToString());
        await using var db = await factory.CreateDbContextAsync();
        var events = await db.AuditEvents.Where(x => x.Action == "translation.deployment.profile.write" && x.ResourceId == deploymentId.ToString("D")).ToListAsync();
        Assert.Contains(events, x => x.Outcome == "forbidden");
        Assert.Contains(events, x => x.Outcome == "conflict");
    }

    private static HttpRequestMessage Write(string path, string token, long revision)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, path)
        {
            Content = JsonContent.Create(new
            {
                deploymentRevision = 2,
                tokenizerHash = new string('c', 64),
                languagePairs = new[] { new { sourceLang = "eng_Latn", targetLang = "fra_Latn" } }
            })
        };
        request.Headers.Add("X-CSRF-TOKEN", token);
        request.Headers.Add("If-Match", $"\"{revision}\"");
        return request;
    }

    private sealed class TestFactory(DbContextOptions<CloudControlPlaneDbContext> options) : IDbContextFactory<CloudControlPlaneDbContext>
    {
        public CloudControlPlaneDbContext CreateDbContext() => new(options);
    }
}
