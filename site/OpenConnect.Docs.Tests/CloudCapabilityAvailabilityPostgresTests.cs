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
public sealed class CloudCapabilityAvailabilityPostgresTests
{
    [CloudPostgresFact]
    public async Task AvailabilityDeclarationWaitsForConcurrentDeploymentDisable()
    {
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_TEST_DATABASE")).Options;
        IDbContextFactory<CloudControlPlaneDbContext> factory = new Factory(options);
        var capability = "availability-race." + Guid.NewGuid().ToString("N");
        var deploymentId = Guid.NewGuid();
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.MigrateAsync();
            db.Capabilities.Add(new CloudCapabilityDefinition
            {
                Id = capability,
                Name = "Test",
                Description = "Test",
                ContractVersion = "v1",
                BillingUnit = "request",
                PlatformAvailability = "not_configured",
                Dependencies = "[]",
                Modalities = "[]",
                AllowedRegions = "[]"
            });
            await db.SaveChangesAsync();
            db.WorkerDeployments.Add(new CloudWorkerDeployment
            {
                Id = deploymentId,
                CapabilityId = capability,
                EndpointReference = "race-" + Guid.NewGuid().ToString("N"),
                Transport = "https-mtls",
                Region = "test",
                ContractVersion = "v1",
                ImageDigest = "sha256:" + new string('a', 64),
                Enabled = true,
                ConcurrencyLimit = 1
            });
            await db.SaveChangesAsync();
        }
        var service = new CloudCapabilityAvailabilityService(new EfCloudCapabilityAvailabilityStore(factory, TimeProvider.System));
        var actor = new CloudSubject("https://operator.example", "operator");
        await using (var writer = await factory.CreateDbContextAsync())
        await using (var transaction = await writer.Database.BeginTransactionAsync())
        {
            await writer.WorkerDeployments.Where(x => x.Id == deploymentId).ExecuteUpdateAsync(x => x.SetProperty(y => y.Enabled, false));
            var declaration = Task.Run(() => service.PutAsync(actor, Guid.NewGuid().ToString("N"), capability, "available", 1, default));
            var waiting = false;
            await using (var observer = await factory.CreateDbContextAsync())
            {
                for (var attempt = 0; attempt < 150 && !waiting; attempt++)
                {
                    waiting = await observer.Database.SqlQueryRaw<bool>("SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE wait_event_type = 'Lock' AND query LIKE '%cloud.worker_deployments%') AS \"Value\"").SingleAsync();
                    if (!waiting)
                        await Task.Delay(20);
                }
            }
            Assert.True(waiting, "The declaration did not wait for the deployment update.");
            await transaction.CommitAsync();
            Assert.Equal("deployment_required", (await declaration).Status);
        }
        var current = await service.GetAsync(capability, default);
        Assert.Equal("not_configured", current!.Availability);
        Assert.Equal(1, current.Revision);
    }

    [CloudPostgresFact]
    public async Task AvailabilityRequiresDeploymentAndFencesOlderAndConcurrentWriters()
    {
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_TEST_DATABASE")).Options;
        IDbContextFactory<CloudControlPlaneDbContext> factory = new Factory(options);
        var capability = "availability." + Guid.NewGuid().ToString("N");
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.MigrateAsync();
            db.Capabilities.Add(new CloudCapabilityDefinition
            {
                Id = capability,
                Name = "Test",
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
        var service = new CloudCapabilityAvailabilityService(new EfCloudCapabilityAvailabilityStore(factory, TimeProvider.System));
        var actor = new CloudSubject("https://operator.example", "operator");
        var trace = Guid.NewGuid().ToString("N");
        Assert.Equal(1, (await service.GetAsync(capability, default))!.Revision);
        Assert.Equal("deployment_required", (await service.PutAsync(actor, trace, capability, "available", 1, default)).Status);
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
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 2)
            .Select(_ => service.PutAsync(actor, trace, capability, "available", 1, default)));
        Assert.Single(concurrent, x => x.Status == "succeeded");
        Assert.Single(concurrent, x => x.Status == "conflict");
        Assert.Equal(2, (await service.GetAsync(capability, default))!.Revision);
        await using (var db = await factory.CreateDbContextAsync())
            await db.Capabilities.Where(x => x.Id == capability).ExecuteUpdateAsync(x => x.SetProperty(y => y.PlatformAvailability, "not_configured"));
        Assert.Equal(3, (await service.GetAsync(capability, default))!.Revision);
        Assert.Equal("conflict", (await service.PutAsync(actor, trace, capability, "available", 2, default)).Status);
        Assert.Equal("invalid_request", (await service.PutAsync(actor, trace, capability, "unknown", 3, default)).Status);
        var unchanged = await service.PutAsync(actor, trace, capability, "not_configured", 3, default);
        Assert.Equal("succeeded", unchanged.Status);
        Assert.Equal(3, unchanged.Availability!.Revision);
        await using (var db = await factory.CreateDbContextAsync())
        {
            var events = await db.AuditEvents.Where(x => x.TraceId == trace && x.Action == "catalog.availability.changed").ToListAsync();
            Assert.Contains(events, x => x.Outcome == "deployment_required");
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
        builder.Services.AddScoped<ICloudCapabilityAvailabilityStore, EfCloudCapabilityAvailabilityStore>();
        builder.Services.AddScoped<CloudCapabilityAvailabilityService>();
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
        app.MapGroup("/cloud/api/v1").RequireAuthorization("CloudApi").MapCloudCapabilityAvailabilityEndpoints();
        await app.StartAsync();
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        var path = $"/cloud/api/v1/operator/capabilities/{capability}/availability";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
        await client.GetAsync("/test-sign-in/ordinary-user");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsync(path, null)).StatusCode);
        await client.GetAsync("/test-sign-in/operator");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsync(path, null)).StatusCode);
        using var tokenJson = JsonDocument.Parse(await client.GetStringAsync("/test-antiforgery"));
        var token = tokenJson.RootElement.GetProperty("token").GetString()!;
        using (var request = Write(path, token, "3", "available"))
        {
            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("\"4\"", response.Headers.ETag?.ToString());
        }
        using (var request = Write(path, token, "3", "not_configured"))
            Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(request)).StatusCode);
        using (var request = Write(path, token, "4", "invalid"))
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.SendAsync(request)).StatusCode);
        using (var request = Write(path, token, "4", "available"))
        {
            request.Content = new StringContent("{\"availability\":\"available\",\"Availability\":\"not_configured\"}", System.Text.Encoding.UTF8, "application/json");
            Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(request)).StatusCode);
        }
        var read = await client.GetAsync(path);
        Assert.Equal("\"4\"", read.Headers.ETag?.ToString());
        Assert.Equal("no-store", read.Headers.CacheControl?.ToString());
        await using var db = await factory.CreateDbContextAsync();
        Assert.Contains(await db.AuditEvents.Where(x => x.Action == "catalog.availability.changed" && x.ResourceId == capability).ToListAsync(), x => x.Outcome == "forbidden");
    }

    private static HttpRequestMessage Write(string path, string token, string revision, string availability)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, path) { Content = JsonContent.Create(new { availability }) };
        request.Headers.Add("X-CSRF-TOKEN", token);
        request.Headers.Add("If-Match", $"\"{revision}\"");
        return request;
    }

    private sealed class Factory(DbContextOptions<CloudControlPlaneDbContext> options) : IDbContextFactory<CloudControlPlaneDbContext>
    {
        public CloudControlPlaneDbContext CreateDbContext() => new(options);
    }
}
