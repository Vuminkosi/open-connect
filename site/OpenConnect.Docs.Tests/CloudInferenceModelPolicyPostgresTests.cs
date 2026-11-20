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
public sealed class CloudInferenceModelPolicyPostgresTests
{
    [CloudPostgresFact]
    public async Task ModelPolicyIsScopedVersionedAndAudited()
    {
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>().UseNpgsql(Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_TEST_DATABASE")).Options;
        IDbContextFactory<CloudControlPlaneDbContext> factory = new TestFactory(options);
        await using (var db = await factory.CreateDbContextAsync()) await db.Database.MigrateAsync();
        const string issuer = "https://region-issuer.example";
        var owner = new CloudSubject(issuer, "owner-" + Guid.NewGuid().ToString("N"));
        var developer = new CloudSubject(issuer, "developer-" + Guid.NewGuid().ToString("N"));
        var outsider = new CloudSubject(issuer, "outsider-" + Guid.NewGuid().ToString("N"));
        var tenant = await new EfCloudTenantStore(factory, TimeProvider.System).CreateTenantAsync(owner, "Inference model policy", default);
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Memberships.Add(new CloudMembership { TenantId = tenant.Id, SubjectIssuer = developer.Issuer, SubjectId = developer.SubjectId, Role = "Developer", CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        var service = new CloudInferenceModelPolicyService(new EfCloudInferenceModelPolicyStore(factory, TimeProvider.System));
        var missing = await service.GetAsync(owner, tenant.Id, default);
        Assert.Equal(0, missing.Policy!.Revision); Assert.Empty(missing.Policy.AllowedModels);
        Assert.False(await service.IsAllowedAsync(tenant.Id, "registry/model-v1", default));
        Assert.False(await service.IsAllowedAsync(tenant.Id, "bad model name", default));
        Assert.Equal("not_found", (await service.GetAsync(outsider, tenant.Id, default)).Status);
        Assert.Equal("not_found", (await service.PutAsync(outsider, tenant.Id, ["region-a"], 0, default)).Status);
        Assert.Equal("forbidden", (await service.PutAsync(developer, tenant.Id, ["region-a"], 0, default)).Status);
        Assert.Equal("invalid_request", (await service.PutAsync(owner, tenant.Id, ["region-a", "region-a"], 0, default)).Status);
        Assert.Equal("invalid_request", (await service.PutAsync(owner, tenant.Id, ["bad model name"], 0, default)).Status);
        var created = await service.PutAsync(owner, tenant.Id, ["region-b", "region-a"], 0, default);
        Assert.Equal("succeeded", created.Status); Assert.Equal(new[] { "region-a", "region-b" }, created.Policy!.AllowedModels);
        Assert.True(await service.IsAllowedAsync(tenant.Id, "region-a", default));
        Assert.False(await service.IsAllowedAsync(tenant.Id, "other-model", default));
        Assert.False(await service.IsAllowedAsync(Guid.NewGuid(), "region-a", default));
        Assert.Equal("conflict", (await service.PutAsync(owner, tenant.Id, [], 0, default)).Status);
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 2).Select(i => service.PutAsync(owner, tenant.Id, ["region-" + i], 1, default)));
        Assert.Single(concurrent, x => x.Status == "succeeded"); Assert.Single(concurrent, x => x.Status == "conflict");
        var empty = await service.PutAsync(owner, tenant.Id, [], 2, default);
        Assert.Equal("succeeded", empty.Status); Assert.Empty(empty.Policy!.AllowedModels);
        Assert.False(await service.IsAllowedAsync(tenant.Id, "region-a", default));
        await using (var db = await factory.CreateDbContextAsync())
        {
            Assert.Equal(3, await db.AuditEvents.CountAsync(x => x.TenantId == tenant.Id && x.Action == "tenant.inference_models.changed"));
            var rejected = await db.AuditEvents.Where(x => x.TenantId == null && x.ResourceId == tenant.Id.ToString("D") && x.Action == "tenant.inference_models.changed").ToListAsync();
            Assert.Contains(rejected, x => x.Outcome == "forbidden" && x.SubjectId == developer.SubjectId);
            Assert.Contains(rejected, x => x.Outcome == "not_found" && x.SubjectId == outsider.SubjectId);
            Assert.Contains(rejected, x => x.Outcome == "conflict");
            Assert.Contains(rejected, x => x.Outcome == "invalid_request");
        }
        await using (var db = await factory.CreateDbContextAsync())
        {
            var raw = "[\"registry/model-v1\"]";
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE cloud.inference_model_policies SET \"AllowedModels\" = {raw}::jsonb WHERE \"TenantId\" = {tenant.Id}");
        }
        Assert.Equal(4, (await service.GetAsync(owner, tenant.Id, default)).Policy!.Revision);
        Assert.Equal("conflict", (await service.PutAsync(owner, tenant.Id, [], 3, default)).Status);
        Assert.True(await service.IsAllowedAsync(tenant.Id, "registry/model-v1", default));
        await VerifyHttpAsync(factory, owner, developer, outsider, tenant.Id);
        await using (var db = await factory.CreateDbContextAsync())
        {
            var malformed = "[\"registry/model-v1\", 42]";
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE cloud.inference_model_policies SET \"AllowedModels\" = {malformed}::jsonb WHERE \"TenantId\" = {tenant.Id}");
        }
        Assert.False(await service.IsAllowedAsync(tenant.Id, "registry/model-v1", default));
        Assert.Equal("unavailable", (await service.GetAsync(owner, tenant.Id, default)).Status);
    }

    private static async Task VerifyHttpAsync(IDbContextFactory<CloudControlPlaneDbContext> factory, CloudSubject owner, CloudSubject developer, CloudSubject outsider, Guid tenantId)
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["OpenConnect:Cloud:Identity:Authority"] = owner.Issuer });
        builder.Services.AddSingleton(factory); builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddScoped<ICloudInferenceModelPolicyStore, EfCloudInferenceModelPolicyStore>(); builder.Services.AddScoped<CloudInferenceModelPolicyService>();
        builder.Services.AddAntiforgery(x => x.HeaderName = "X-CSRF-TOKEN");
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(x =>
        {
            x.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; };
            x.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
        });
        builder.Services.AddAuthorization(x => x.AddPolicy("CloudApi", policy => policy.AddAuthenticationSchemes(CookieAuthenticationDefaults.AuthenticationScheme).RequireAuthenticatedUser()));
        await using var app = builder.Build(); app.UseAuthentication(); app.UseAuthorization();
        app.MapGet("/test-sign-in/{subject}", async (string subject, HttpContext context) => await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, subject, ClaimValueTypes.String, owner.Issuer)], CookieAuthenticationDefaults.AuthenticationScheme))));
        app.MapGet("/test-antiforgery", (HttpContext context, IAntiforgery antiforgery) => Results.Ok(new { token = antiforgery.GetAndStoreTokens(context).RequestToken }));
        app.MapGroup("/cloud/api/v1").RequireAuthorization("CloudApi").MapCloudInferenceModelPolicyEndpoints(owner.Issuer);
        await app.StartAsync();
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        var path = $"/cloud/api/v1/tenants/{tenantId:D}/inference-model-policy";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
        await client.GetAsync("/test-sign-in/" + outsider.SubjectId);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path)).StatusCode);
        await client.GetAsync("/test-sign-in/" + developer.SubjectId);
        using var developerTokenJson = JsonDocument.Parse(await client.GetStringAsync("/test-antiforgery"));
        var developerToken = developerTokenJson.RootElement.GetProperty("token").GetString()!;
        using (var request = Write(path, developerToken, "4", new { allowedModels = new[] { "region-a" } })) Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(request)).StatusCode);
        await client.GetAsync("/test-sign-in/" + owner.SubjectId);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsync(path, null)).StatusCode);
        using var tokenJson = JsonDocument.Parse(await client.GetStringAsync("/test-antiforgery"));
        var token = tokenJson.RootElement.GetProperty("token").GetString()!;
        using (var request = Write(path, token, "4", new { allowedModels = new[] { "region-a" } }))
        {
            var response = await client.SendAsync(request); Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Equal("\"5\"", response.Headers.ETag?.ToString());
        }
        using (var request = Write(path, token, "4", new { allowedModels = Array.Empty<string>() })) Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(request)).StatusCode);
        using (var request = Write(path, token, "5", new { allowedModels = new[] { "region-a" } }))
        {
            request.Content = new StringContent("{\"allowedModels\":[],\"AllowedModels\":[]}", System.Text.Encoding.UTF8, "application/json");
            Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(request)).StatusCode);
        }
        var read = await client.GetAsync(path); Assert.Equal("\"5\"", read.Headers.ETag?.ToString()); Assert.Equal("no-store", read.Headers.CacheControl?.ToString());
    }

    private static HttpRequestMessage Write(string path, string token, string revision, object payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, path) { Content = JsonContent.Create(payload) };
        request.Headers.Add("X-CSRF-TOKEN", token); request.Headers.Add("If-Match", $"\"{revision}\""); return request;
    }

    private sealed class TestFactory(DbContextOptions<CloudControlPlaneDbContext> options) : IDbContextFactory<CloudControlPlaneDbContext>
    {
        public CloudControlPlaneDbContext CreateDbContext() => new(options);
    }
}
