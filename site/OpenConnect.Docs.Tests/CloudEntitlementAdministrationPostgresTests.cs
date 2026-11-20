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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using OpenConnect.Docs.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

[Collection("Cloud PostgreSQL")]
public sealed class CloudEntitlementAdministrationPostgresTests
{
    [CloudPostgresFact]
    public async Task GrantsAreRevisionFencedAndAudited()
    {
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>().UseNpgsql(Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_TEST_DATABASE")).Options;
        IDbContextFactory<CloudControlPlaneDbContext> factory = new TestFactory(options);
        var tenant = Guid.NewGuid();
        var capability = "entitlement." + Guid.NewGuid().ToString("N");
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.MigrateAsync();
            db.Tenants.Add(new CloudTenant { Id = tenant, Name = "Entitlement test", CreatedAt = DateTimeOffset.UtcNow });
            db.Capabilities.Add(new CloudCapabilityDefinition { Id = capability, Name = "Test", Description = "Test", ContractVersion = "1", BillingUnit = "request", PlatformAvailability = "available", Dependencies = "[]", Modalities = "[]", AllowedRegions = "[]" });
            await db.SaveChangesAsync();
        }
        await VerifyHttpAsync(factory, tenant, "classification.classify");
        var service = new CloudEntitlementAdministration(new EfCloudEntitlementAdministrationStore(factory, TimeProvider.System));
        var actor = new CloudSubject("https://operator.example", "operator");
        var trace = Guid.NewGuid().ToString("N");
        var granted = new CloudEntitlementChange(true, null);
        Assert.Equal("invalid_request", (await service.PutAsync(actor, trace, Guid.NewGuid(), capability, granted, 0, default)).Status);
        var created = await service.PutAsync(actor, trace, tenant, capability, granted, 0, default);
        Assert.Equal("succeeded", created.Status);
        Assert.Equal(1, created.Entitlement!.Revision);
        Assert.Equal("conflict", (await service.PutAsync(actor, trace, tenant, capability, granted, 0, default)).Status);
        var competing = await Task.WhenAll(Enumerable.Range(0, 2).Select(i => service.PutAsync(actor, trace, tenant, capability, new(i == 0, null), 1, default)));
        Assert.Single(competing, x => x.Status == "succeeded");
        Assert.Single(competing, x => x.Status == "conflict");
        await using (var db = await factory.CreateDbContextAsync())
            await db.TenantEntitlements.Where(x => x.TenantId == tenant && x.CapabilityId == capability).ExecuteUpdateAsync(x => x.SetProperty(y => y.PlanGranted, false));
        var current = await service.GetAsync(tenant, capability, default);
        Assert.Equal(3, current!.Revision);
        Assert.False(current.PlanGranted);
        Assert.Equal("conflict", (await service.PutAsync(actor, trace, tenant, capability, granted, 2, default)).Status);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Contains(await verify.AuditEvents.Where(x => x.TraceId == trace).ToListAsync(), x => x.Action == "entitlement.write" && x.Outcome == "succeeded");
        await Assert.ThrowsAnyAsync<Exception>(() => verify.TenantEntitlements.Where(x => x.TenantId == tenant && x.CapabilityId == capability).ExecuteDeleteAsync());
    }

    private static async Task VerifyHttpAsync(IDbContextFactory<CloudControlPlaneDbContext> factory, Guid tenant, string capability)
    {
        const string issuer = "https://operator.example";
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["OpenConnect:Cloud:Identity:Enabled"] = "true", ["OpenConnect:Cloud:Identity:Authority"] = issuer, ["OpenConnect:Cloud:Operators:0:Issuer"] = issuer, ["OpenConnect:Cloud:Operators:0:Subject"] = "operator" });
        builder.Services.AddSingleton(factory); builder.Services.AddSingleton(TimeProvider.System); builder.Services.AddSingleton<CloudOperatorAccess>();
        builder.Services.AddScoped<ICloudEntitlementAdministrationStore, EfCloudEntitlementAdministrationStore>(); builder.Services.AddScoped<CloudEntitlementAdministration>();
        builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
        {
            options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; };
            options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
        });
        builder.Services.AddAuthorization(options => options.AddPolicy("CloudApi", policy => policy.AddAuthenticationSchemes(CookieAuthenticationDefaults.AuthenticationScheme).RequireAuthenticatedUser()));
        await using var app = builder.Build(); app.UseAuthentication(); app.UseAuthorization();
        app.MapGet("/test-sign-in/{subject}", async (string subject, HttpContext context) => await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, subject, ClaimValueTypes.String, issuer)], CookieAuthenticationDefaults.AuthenticationScheme))));
        app.MapGet("/test-antiforgery", (HttpContext context, IAntiforgery antiforgery) => Results.Ok(new { token = antiforgery.GetAndStoreTokens(context).RequestToken }));
        app.MapGroup("/cloud/api/v1").RequireAuthorization("CloudApi").MapCloudEntitlementAdministrationEndpoints();
        await app.StartAsync();
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        var path = $"/cloud/api/v1/operator/tenants/{tenant}/entitlements/{capability}";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
        await client.GetAsync("/test-sign-in/ordinary-user");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsync(path, null)).StatusCode);
        await client.GetAsync("/test-sign-in/operator");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsync(path, null)).StatusCode);
        using var tokenJson = JsonDocument.Parse(await client.GetStringAsync("/test-antiforgery"));
        var token = tokenJson.RootElement.GetProperty("token").GetString()!;
        HttpRequestMessage Write(string revision, object body)
        {
            var request = new HttpRequestMessage(HttpMethod.Put, path) { Content = JsonContent.Create(body) };
            request.Headers.Add("If-Match", $"\"{revision}\""); request.Headers.Add("X-CSRF-TOKEN", token); return request;
        }
        var payload = new { planGranted = true, expiresAt = (DateTimeOffset?)null };
        using (var request = Write("0", payload)) { var response = await client.SendAsync(request); Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Equal("\"1\"", response.Headers.ETag?.ToString()); }
        using (var request = Write("0", payload)) Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(request)).StatusCode);
        using (var request = Write("1", new { expiresAt = (DateTimeOffset?)null })) Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(request)).StatusCode);
        using (var request = Write("1", payload))
        {
            request.Content = new StringContent("{\"limit\":1,\"Limit\":2}", System.Text.Encoding.UTF8, "application/json");
            Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(request)).StatusCode);
        }
        var read = await client.GetAsync(path); Assert.Equal(HttpStatusCode.OK, read.StatusCode); Assert.Equal("\"1\"", read.Headers.ETag?.ToString());
        await using var db = await factory.CreateDbContextAsync();
        var audit = await db.AuditEvents.Where(x => x.ResourceId == $"{tenant:D}/{capability}").ToListAsync();
        Assert.Contains(audit, x => x.Outcome == "forbidden" && x.ActorRole == "AuthenticatedUser");
        Assert.Contains(audit, x => x.Outcome == "invalid_request");
        Assert.Contains(audit, x => x.Outcome == "succeeded");
    }

    private sealed class TestFactory(DbContextOptions<CloudControlPlaneDbContext> options) : IDbContextFactory<CloudControlPlaneDbContext>
    {
        public CloudControlPlaneDbContext CreateDbContext() => new(options);
    }
}
