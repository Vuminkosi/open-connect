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
public sealed class CloudQuotaAdministrationPostgresTests
{
    [CloudPostgresFact]
    public async Task OperatorPoliciesAreScopedAuditedAndConcurrentSafe()
    {
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>().UseNpgsql(Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_TEST_DATABASE")).Options;
        IDbContextFactory<CloudControlPlaneDbContext> factory = new TestFactory(options);
        var tenant = Guid.NewGuid();
        var application = Guid.NewGuid();
        var bucket = Guid.NewGuid();
        var capability = "quota." + Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.MigrateAsync();
            db.Tenants.Add(new CloudTenant { Id = tenant, Name = "Quota test", CreatedAt = now });
            db.Applications.Add(new CloudApplication { Id = application, TenantId = tenant, Name = "App", Environment = "Development", CreatedAt = now });
            db.Capabilities.Add(new CloudCapabilityDefinition { Id = capability, Name = "Test", Description = "Test", ContractVersion = "1", BillingUnit = "request", PlatformAvailability = "available", Dependencies = "[]", Modalities = "[]", AllowedRegions = "[]" });
            await db.SaveChangesAsync();
        }
        var service = new CloudQuotaAdministration(new EfCloudQuotaAdministrationStore(factory, TimeProvider.System));
        var actor = new CloudSubject("https://operator.example", "operator");
        var trace = Guid.NewGuid().ToString("N");
        var policy = new CloudQuotaPolicy(capability, "request", now, now.AddDays(1), 10);
        Assert.Equal("succeeded", (await service.PutAsync(actor, trace, tenant, application, bucket, policy, 0, default)).Status);
        Assert.Equal("conflict", (await service.PutAsync(actor, trace, tenant, application, Guid.NewGuid(), policy, 0, default)).Status);
        Assert.Equal("conflict", (await service.PutAsync(actor, trace, Guid.NewGuid(), application, bucket, policy, 1, default)).Status);
        Assert.Equal("invalid_request", (await service.PutAsync(actor, trace, tenant, application, bucket, policy with { Unit = "other" }, 1, default)).Status);
        var classification = policy with { CapabilityId = "classification.classify", Unit = "request" };
        Assert.Equal("succeeded", (await service.PutAsync(actor, trace, tenant, application, Guid.NewGuid(), classification, 0, default)).Status);
        Assert.Equal("invalid_request", (await service.PutAsync(actor, trace, tenant, application, Guid.NewGuid(), classification with { Unit = "compute" }, 0, default)).Status);
        var voice = policy with { CapabilityId = "voice.synthesize", Unit = "character" };
        Assert.Equal("succeeded", (await service.PutAsync(actor, trace, tenant, application, Guid.NewGuid(), voice, 0, default)).Status);
        Assert.Equal("invalid_request", (await service.PutAsync(actor, trace, tenant, application, Guid.NewGuid(), voice with { Unit = "request" }, 0, default)).Status);
        var inference = policy with { CapabilityId = "inference.chat", Unit = "token" };
        Assert.Equal("succeeded", (await service.PutAsync(actor, trace, tenant, application, Guid.NewGuid(), inference, 0, default)).Status);
        Assert.Equal("invalid_request", (await service.PutAsync(actor, trace, tenant, application, Guid.NewGuid(), inference with { Unit = "request" }, 0, default)).Status);
        await using (var db = await factory.CreateDbContextAsync())
            await db.QuotaBuckets.Where(x => x.Id == bucket).ExecuteUpdateAsync(x => x.SetProperty(y => y.Consumed, 3L).SetProperty(y => y.Reserved, 4L));
        Assert.Equal("invalid_request", (await service.PutAsync(actor, trace, tenant, application, bucket, policy with { Limit = 6 }, 1, default)).Status);
        var competing = await Task.WhenAll(new[] { 12L, 13L }.Select(limit => service.PutAsync(actor, trace, tenant, application, bucket, policy with { Limit = limit }, 1, default)));
        Assert.Single(competing, x => x.Status == "succeeded");
        Assert.Single(competing, x => x.Status == "conflict");
        var current = await service.GetAsync(tenant, application, bucket, default);
        Assert.Equal(2, current!.Revision);
        Assert.Equal(3, current.Consumed);
        Assert.Equal(4, current.Reserved);
        var quota = new CloudQuotaService(new EfCloudQuotaStore(factory), TimeProvider.System);
        var admission = new CloudQuotaRequest(tenant, application, capability, "request", Guid.NewGuid(), "test-key", new byte[32], 5, now.AddHours(1));
        var race = await Task.WhenAll(
            Task.Run(async () => (await service.PutAsync(actor, trace, tenant, application, bucket, policy with { Limit = 7 }, 2, default)).Status),
            Task.Run(async () => (await quota.ReserveAsync(admission, default)).Status));
        Assert.Contains(race[0], new[] { "succeeded", "conflict", "invalid_request" });
        Assert.Contains(race[1], new[] { "reserved", "quota_exceeded" });
        current = await service.GetAsync(tenant, application, bucket, default);
        Assert.True(current!.Consumed + current.Reserved <= current.Limit);
        var concurrentBuckets = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => service.PutAsync(actor, trace, tenant, application, Guid.NewGuid(), policy with { PeriodStart = now.AddDays(1), PeriodEnd = now.AddDays(2) }, 0, default)));
        Assert.Single(concurrentBuckets, x => x.Status == "succeeded");
        Assert.Single(concurrentBuckets, x => x.Status == "conflict");
        await VerifyHttpAsync(factory, tenant, application, capability, now);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Contains(await verify.AuditEvents.Where(x => x.TraceId == trace).ToListAsync(), x => x.Action == "quota.write" && x.Outcome == "succeeded");
    }

    private static async Task VerifyHttpAsync(IDbContextFactory<CloudControlPlaneDbContext> factory, Guid tenant, Guid application, string capability, DateTimeOffset now)
    {
        const string issuer = "https://operator.example";
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["OpenConnect:Cloud:Identity:Enabled"] = "true", ["OpenConnect:Cloud:Identity:Authority"] = issuer, ["OpenConnect:Cloud:Operators:0:Issuer"] = issuer, ["OpenConnect:Cloud:Operators:0:Subject"] = "operator" });
        builder.Services.AddSingleton(factory);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<CloudOperatorAccess>();
        builder.Services.AddScoped<ICloudQuotaAdministrationStore, EfCloudQuotaAdministrationStore>();
        builder.Services.AddScoped<CloudQuotaAdministration>();
        builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
        {
            options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; };
            options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
        });
        builder.Services.AddAuthorization(options => options.AddPolicy("CloudApi", policy => policy.AddAuthenticationSchemes(CookieAuthenticationDefaults.AuthenticationScheme).RequireAuthenticatedUser()));
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/test-sign-in/{subject}", async (string subject, HttpContext context) => await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, subject, ClaimValueTypes.String, issuer)], CookieAuthenticationDefaults.AuthenticationScheme))));
        app.MapGet("/test-antiforgery", (HttpContext context, IAntiforgery antiforgery) => Results.Ok(new { token = antiforgery.GetAndStoreTokens(context).RequestToken }));
        app.MapGroup("/cloud/api/v1").RequireAuthorization("CloudApi").MapCloudQuotaAdministrationEndpoints();
        await app.StartAsync();
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        var bucket = Guid.NewGuid();
        var path = $"/cloud/api/v1/operator/tenants/{tenant}/applications/{application}/quotas/{bucket}";
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
            request.Headers.Add("If-Match", $"\"{revision}\"");
            request.Headers.Add("X-CSRF-TOKEN", token);
            return request;
        }
        var payload = new { capabilityId = capability, unit = "request", periodStart = now.AddDays(2), periodEnd = now.AddDays(3), limit = 20 };
        using (var request = Write("0", payload))
        { var response = await client.SendAsync(request); Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Equal("\"1\"", response.Headers.ETag?.ToString()); }
        using (var request = Write("0", payload))
            Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(request)).StatusCode);
        using (var request = Write("1", new { capabilityId = capability }))
            Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(request)).StatusCode);
        using (var request = Write("1", payload))
        {
            request.Content = new StringContent("{\"limit\":1,\"Limit\":2}", System.Text.Encoding.UTF8, "application/json");
            Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(request)).StatusCode);
        }
        var read = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal("\"1\"", read.Headers.ETag?.ToString());
        await using var db = await factory.CreateDbContextAsync();
        var audit = await db.AuditEvents.Where(x => x.ResourceId == $"{tenant:D}/{bucket:D}").ToListAsync();
        Assert.Contains(audit, x => x.Outcome == "forbidden" && x.ActorRole == "AuthenticatedUser");
        Assert.Contains(audit, x => x.Outcome == "invalid_request");
        Assert.Contains(audit, x => x.Outcome == "succeeded");
    }

    private sealed class TestFactory(DbContextOptions<CloudControlPlaneDbContext> options) : IDbContextFactory<CloudControlPlaneDbContext>
    {
        public CloudControlPlaneDbContext CreateDbContext() => new(options);
    }
}
