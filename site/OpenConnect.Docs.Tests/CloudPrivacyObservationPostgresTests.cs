using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Docs.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

[Collection("Cloud PostgreSQL")]
public sealed class CloudPrivacyObservationPostgresTests
{
    [CloudPostgresFact]
    public async Task ReadsAreTenantRoleAndIssuerScopedWithStableCursorAndTimeBounds()
    {
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>().UseNpgsql(Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_TEST_DATABASE")).Options;
        IDbContextFactory<CloudControlPlaneDbContext> factory = new TestFactory(options);
        await using (var db = await factory.CreateDbContextAsync()) await db.Database.MigrateAsync();
        var owner = new CloudSubject("https://issuer.example", Guid.NewGuid().ToString("N"));
        var analyst = new CloudSubject(owner.Issuer, Guid.NewGuid().ToString("N"));
        var developer = new CloudSubject(owner.Issuer, Guid.NewGuid().ToString("N"));
        var store = new EfCloudTenantStore(factory, TimeProvider.System);
        var tenant = await store.CreateTenantAsync(owner, "Security observations", CancellationToken.None);
        var app = (await store.CreateApplicationAsync(owner, tenant.Id, "App", "Development", CancellationToken.None)).Application!;
        var other = await store.CreateTenantAsync(owner, "Other observations", CancellationToken.None);
        var otherApp = (await store.CreateApplicationAsync(owner, other.Id, "App", "Development", CancellationToken.None)).Application!;
        var now = DateTimeOffset.UtcNow;
        now = new DateTimeOffset(now.Ticks - now.Ticks % 10, TimeSpan.Zero);
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Memberships.AddRange(new CloudMembership { TenantId = tenant.Id, SubjectIssuer = analyst.Issuer, SubjectId = analyst.SubjectId, Role = "SecurityAnalyst", CreatedAt = now },
                new CloudMembership { TenantId = tenant.Id, SubjectIssuer = developer.Issuer, SubjectId = developer.SubjectId, Role = "Developer", CreatedAt = now });
            for (var i = 0; i < 3; i++) db.PrivacyEvidence.Add(new CloudPrivacyEvidence { Id = Guid.NewGuid(), TenantId = tenant.Id, ApplicationId = app.Id,
                RequestId = Guid.NewGuid(), Outcome = i == 0 ? "privacy_blocked" : "allowed", TraceId = Guid.NewGuid().ToString("N"), OccurredAt = now,
                CategoryCounts = i == 0 ? "{\"EMAIL\":2}" : null });
            db.PrivacyEvidence.Add(new CloudPrivacyEvidence { Id = Guid.NewGuid(), TenantId = other.Id, ApplicationId = otherApp.Id,
                RequestId = Guid.NewGuid(), Outcome = "allowed", TraceId = Guid.NewGuid().ToString("N"), OccurredAt = now });
            await db.SaveChangesAsync();
        }
        var service = new CloudPrivacyObservationService(new EfCloudPrivacyObservationReader(factory, TimeProvider.System));
        var first = await service.GetTenantAsync(analyst, tenant.Id, null, null, null, 2, CancellationToken.None);
        Assert.Equal(2, first!.Observations.Count);
        Assert.NotNull(first.Next);
        var second = await service.GetTenantAsync(analyst, tenant.Id, null, null, first.Next, 2, CancellationToken.None);
        Assert.Single(second!.Observations);
        Assert.Null(second.Next);
        Assert.Empty(first.Observations.Select(x => x.Id).Intersect(second.Observations.Select(x => x.Id)));
        Assert.All(first.Observations.Concat(second.Observations), x => Assert.Equal(app.Id, x.ApplicationId));
        var blocked = Assert.Single(first.Observations.Concat(second.Observations), x => x.Outcome == "privacy_blocked");
        Assert.Equal(new CloudPrivacyCategoryCount("EMAIL", 2), Assert.Single(blocked.Categories!));
        var caseEvidence = await CloudPrivacyCaseReader.GetAsync(factory, TimeProvider.System, analyst, tenant.Id, blocked.Id, CancellationToken.None);
        Assert.Equal(blocked.Id, caseEvidence!.Id);
        Assert.Equal("privacy_blocked", caseEvidence.Outcome);
        Assert.Equal(new CloudPrivacyCategoryCount("EMAIL", 2), Assert.Single(caseEvidence.Categories!));
        Assert.Null(await CloudPrivacyCaseReader.GetAsync(factory, TimeProvider.System, developer, tenant.Id, blocked.Id, CancellationToken.None));
        Assert.Null(await CloudPrivacyCaseReader.GetAsync(factory, TimeProvider.System, analyst, other.Id, blocked.Id, CancellationToken.None));
        Assert.Null(await CloudPrivacyCaseReader.GetAsync(factory, TimeProvider.System,
            new CloudSubject("https://other.example", analyst.SubjectId), tenant.Id, blocked.Id, CancellationToken.None));
        Assert.All(first.Observations.Concat(second.Observations).Where(x => x.Outcome == "allowed"), x => Assert.Null(x.Categories));
        Assert.Empty((await service.GetTenantAsync(analyst, tenant.Id, null, now, null, 10, CancellationToken.None))!.Observations);
        Assert.Equal(3, (await service.GetTenantAsync(analyst, tenant.Id, now, now.AddSeconds(1), null, 10, CancellationToken.None))!.Observations.Count);
        Assert.Null(await service.GetTenantAsync(developer, tenant.Id, null, null, null, 10, CancellationToken.None));
        Assert.Null(await service.GetTenantAsync(new CloudSubject("https://other.example", analyst.SubjectId), tenant.Id, null, null, null, 10, CancellationToken.None));
        Assert.Null(await service.GetTenantAsync(analyst, other.Id, null, null, null, 10, CancellationToken.None));
        var statistics = new EfCloudPrivacyStatisticsReader(factory, TimeProvider.System);
        var totals = (await statistics.GetTenantAsync(analyst, tenant.Id, now, now.AddSeconds(1), CancellationToken.None))!;
        Assert.Equal((2, 1, 0, 2), (totals.Allowed, totals.Blocked, totals.DetectorUnavailable, totals.CategoryEvidenceUnavailable));
        Assert.Equal(new CloudPrivacyCategoryTotal("EMAIL", 2), Assert.Single(totals.Categories));
        var emptyTotals = (await statistics.GetTenantAsync(analyst, tenant.Id, null, now, CancellationToken.None))!;
        Assert.Equal((0, 0, 0, 0), (emptyTotals.Allowed, emptyTotals.Blocked, emptyTotals.DetectorUnavailable, emptyTotals.CategoryEvidenceUnavailable));
        Assert.Empty(emptyTotals.Categories);
        Assert.Null(await statistics.GetTenantAsync(developer, tenant.Id, null, null, CancellationToken.None));
        Assert.Null(await statistics.GetTenantAsync(analyst, other.Id, null, null, CancellationToken.None));
        Assert.Null(await statistics.GetTenantAsync(new CloudSubject("https://other.example", analyst.SubjectId), tenant.Id, null, null, CancellationToken.None));
        var auditFailure = new CloudPrivacyObservationService(new EfCloudPrivacyObservationReader(factory, new FailedClock()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => auditFailure.GetTenantAsync(analyst, tenant.Id, null, null, null, 10, CancellationToken.None));
        await VerifyHttpAsync(factory, tenant.Id, other.Id, blocked.Id, analyst, developer);
        await using var verifyAudit = await factory.CreateDbContextAsync();
        var audit = await verifyAudit.AuditEvents.Where(x => x.Action == "privacy.observations.read" && x.TenantId == tenant.Id).ToListAsync();
        Assert.Contains(audit, x => x.SubjectId == analyst.SubjectId && x.ActorRole == "SecurityAnalyst" && x.Outcome == "succeeded");
        Assert.Contains(audit, x => x.SubjectId == developer.SubjectId && x.ActorRole == "Developer" && x.Outcome == "forbidden");
        var caseAudit = await verifyAudit.AuditEvents.Where(x => x.Action == "privacy.case.read" && x.TenantId == tenant.Id &&
            x.ResourceId == blocked.Id.ToString("D")).ToListAsync();
        Assert.Contains(caseAudit, x => x.SubjectId == analyst.SubjectId && x.Outcome == "succeeded");
        Assert.Contains(caseAudit, x => x.SubjectId == developer.SubjectId && x.Outcome == "forbidden");
    }

    private static async Task VerifyHttpAsync(IDbContextFactory<CloudControlPlaneDbContext> factory, Guid tenantId, Guid otherTenantId,
        Guid caseId, CloudSubject analyst, CloudSubject developer)
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(factory); builder.Services.AddSingleton(TimeProvider.System); builder.Services.AddScoped<ICloudPrivacyObservationReader, EfCloudPrivacyObservationReader>();
        builder.Services.AddScoped<CloudPrivacyObservationService>();
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
            options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; });
        builder.Services.AddAuthorization(options => options.AddPolicy("CloudApi", policy => { policy.AddAuthenticationSchemes(CookieAuthenticationDefaults.AuthenticationScheme); policy.RequireAuthenticatedUser(); }));
        await using var app = builder.Build(); app.UseAuthentication(); app.UseAuthorization();
        app.MapGet("/test-sign-in/{role}", async (string role, HttpContext context) =>
        {
            var subject = role == "analyst" ? analyst : developer;
            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, subject.SubjectId)], CookieAuthenticationDefaults.AuthenticationScheme)));
        });
        app.MapGroup("/cloud/api/v1").RequireAuthorization("CloudApi").MapCloudPrivacyObservationEndpoints(analyst.Issuer);
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(address) };
        var path = $"/cloud/api/v1/tenants/{tenantId}/security/observations";
        var casePath = $"/cloud/api/v1/tenants/{tenantId}/security/cases/{caseId}";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(casePath)).StatusCode);
        await client.GetAsync("/test-sign-in/analyst");
        var caseResponse = await client.GetAsync(casePath);
        Assert.Equal(HttpStatusCode.OK, caseResponse.StatusCode);
        Assert.Equal("no-store", caseResponse.Headers.CacheControl?.ToString());
        var caseEvidence = await caseResponse.Content.ReadFromJsonAsync<CloudPrivacyObservation>();
        Assert.Equal(caseId, caseEvidence!.Id);
        Assert.Equal("privacy_blocked", caseEvidence.Outcome);
        Assert.Equal(new CloudPrivacyCategoryCount("EMAIL", 2), Assert.Single(caseEvidence.Categories!));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/cloud/api/v1/tenants/{otherTenantId}/security/cases/{caseId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/cloud/api/v1/tenants/{tenantId}/security/cases/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/cloud/api/v1/tenants/{tenantId}/security/cases/{Guid.Empty}")).StatusCode);
        var response = await client.GetAsync(path + "?limit=2");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var page = await response.Content.ReadFromJsonAsync<CloudPrivacyObservationPage>();
        Assert.Equal(2, page!.Observations.Count);
        Assert.NotNull(page.Next);
        var cursor = $"?limit=2&beforeAt={Uri.EscapeDataString(page.Next.OccurredAt.ToString("O"))}&beforeId={page.Next.Id:D}";
        var next = await client.GetFromJsonAsync<CloudPrivacyObservationPage>(path + cursor);
        Assert.Single(next!.Observations);
        Assert.Empty(page.Observations.Select(x => x.Id).Intersect(next.Observations.Select(x => x.Id)));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(path + "?beforeId=" + Guid.NewGuid())).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/cloud/api/v1/tenants/{Guid.Empty:D}/security/observations")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/cloud/api/v1/tenants/{otherTenantId}/security/observations")).StatusCode);
        await client.GetAsync("/test-sign-in/developer");
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(casePath)).StatusCode);
    }

    private sealed class TestFactory(DbContextOptions<CloudControlPlaneDbContext> options) : IDbContextFactory<CloudControlPlaneDbContext>
    {
        public CloudControlPlaneDbContext CreateDbContext() => new(options);
    }

    private sealed class FailedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => throw new InvalidOperationException("Audit clock unavailable.");
    }
}
