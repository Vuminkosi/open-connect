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
public sealed class CloudCatalogRegionsPostgresTests
{
    [CloudPostgresFact]
    public async Task OperatorRegionDeclarationsAreVersionedAcrossOlderWriters()
    {
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>().UseNpgsql(Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_TEST_DATABASE")).Options;
        IDbContextFactory<CloudControlPlaneDbContext> factory = new TestFactory(options);
        await using (var db = await factory.CreateDbContextAsync())
            await db.Database.MigrateAsync();
        var capability = "region." + Guid.NewGuid().ToString("N");
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Capabilities.Add(new CloudCapabilityDefinition { Id = capability, Name = "Test", Description = "Test", ContractVersion = "1", BillingUnit = "request", PlatformAvailability = "available", Dependencies = "[]", Modalities = "[]", AllowedRegions = "[]" });
            await db.SaveChangesAsync();
        }
        var service = new CloudCatalogRegionsService(new EfCloudCatalogRegionsStore(factory, TimeProvider.System));
        var actor = new CloudSubject("https://operator.example", "operator");
        var trace = Guid.NewGuid().ToString("N");
        var initial = await service.GetAsync(capability, default);
        Assert.Equal(1, initial!.Revision);
        Assert.False(initial.Verified);
        Assert.Empty(initial.AllowedRegions);
        var first = await service.PutAsync(actor, trace, capability, ["region-b", "region-a"], true, 1, default);
        Assert.Equal("succeeded", first.Status);
        Assert.Equal(2, first.Regions!.Revision);
        Assert.Equal(new[] { "region-a", "region-b" }, first.Regions.AllowedRegions);
        Assert.Equal("conflict", (await service.PutAsync(actor, trace, capability, [], false, 1, default)).Status);
        await using (var db = await factory.CreateDbContextAsync())
            await db.Capabilities.Where(x => x.Id == capability).ExecuteUpdateAsync(x => x.SetProperty(y => y.AllowedRegions, "[\"legacy-region\"]"));
        Assert.Equal(3, (await service.GetAsync(capability, default))!.Revision);
        Assert.Equal("conflict", (await service.PutAsync(actor, trace, capability, [], false, 2, default)).Status);
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 2).Select(i => service.PutAsync(actor, trace, capability, ["region-" + i], true, 3, default)));
        Assert.Single(concurrent, x => x.Status == "succeeded");
        Assert.Single(concurrent, x => x.Status == "conflict");
        var after = await service.GetAsync(capability, default);
        Assert.Equal(4, after!.Revision);
        var noChange = await service.PutAsync(actor, trace, capability, after.AllowedRegions.ToArray(), after.Verified, after.Revision, default);
        Assert.Equal(4, noChange.Regions!.Revision);
        Assert.Equal("invalid_request", (await service.PutAsync(actor, trace, capability, ["region", "region"], true, 4, default)).Status);
        await using (var db = await factory.CreateDbContextAsync())
        {
            var events = await db.AuditEvents.Where(x => x.TraceId == trace && x.Action == "catalog.regions.changed").ToListAsync();
            Assert.Contains(events, x => x.Outcome == "succeeded");
            Assert.Contains(events, x => x.Outcome == "conflict");
            Assert.Contains(events, x => x.Outcome == "invalid_request");
        }
        await VerifyHttpAsync(factory, capability);
    }

    private static async Task VerifyHttpAsync(IDbContextFactory<CloudControlPlaneDbContext> factory, string capability)
    {
        const string issuer = "https://operator.example";
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["OpenConnect:Cloud:Identity:Enabled"] = "true", ["OpenConnect:Cloud:Identity:Authority"] = issuer, ["OpenConnect:Cloud:Operators:0:Issuer"] = issuer, ["OpenConnect:Cloud:Operators:0:Subject"] = "operator" });
        builder.Services.AddSingleton(factory);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<CloudOperatorAccess>();
        builder.Services.AddScoped<ICloudCatalogRegionsStore, EfCloudCatalogRegionsStore>();
        builder.Services.AddScoped<CloudCatalogRegionsService>();
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
        app.MapGroup("/cloud/api/v1").RequireAuthorization("CloudApi").MapCloudCatalogRegionsEndpoints();
        await app.StartAsync();
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        var path = $"/cloud/api/v1/operator/capabilities/{capability}/regions";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
        await client.GetAsync("/test-sign-in/ordinary-user");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsync(path, null)).StatusCode);
        await client.GetAsync("/test-sign-in/operator");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsync(path, null)).StatusCode);
        using var tokenJson = JsonDocument.Parse(await client.GetStringAsync("/test-antiforgery"));
        var token = tokenJson.RootElement.GetProperty("token").GetString()!;
        using (var request = Write(path, token, "4", new { allowedRegions = new[] { "region-a" }, verified = true }))
        {
            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("\"5\"", response.Headers.ETag?.ToString());
        }
        using (var request = Write(path, token, "4", new { allowedRegions = Array.Empty<string>(), verified = false }))
            Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(request)).StatusCode);
        using (var request = Write(path, token, "5", new { allowedRegions = new[] { "region-a" } }))
            Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(request)).StatusCode);
        using (var request = Write(path, token, "5", new { allowedRegions = Array.Empty<string>(), verified = false }))
        {
            request.Content = new StringContent("{\"verified\":false,\"Verified\":true,\"allowedRegions\":[]}", System.Text.Encoding.UTF8, "application/json");
            Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(request)).StatusCode);
        }
        var read = await client.GetAsync(path);
        Assert.Equal("\"5\"", read.Headers.ETag?.ToString());
        Assert.Equal("no-store", read.Headers.CacheControl?.ToString());
        await using var db = await factory.CreateDbContextAsync();
        Assert.Contains(await db.AuditEvents.Where(x => x.Action == "catalog.regions.changed" && x.ResourceId == capability).ToListAsync(), x => x.Outcome == "forbidden");
    }

    private static HttpRequestMessage Write(string path, string token, string revision, object payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, path) { Content = JsonContent.Create(payload) };
        request.Headers.Add("X-CSRF-TOKEN", token);
        request.Headers.Add("If-Match", $"\"{revision}\"");
        return request;
    }

    private sealed class TestFactory(DbContextOptions<CloudControlPlaneDbContext> options) : IDbContextFactory<CloudControlPlaneDbContext>
    {
        public CloudControlPlaneDbContext CreateDbContext() => new(options);
    }
}
