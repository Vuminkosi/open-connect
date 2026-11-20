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
public sealed class CloudUsageReadPostgresTests
{
    [CloudPostgresFact]
    public async Task UsageIsTenantScopedAuditedAndStableAcrossEqualTimestamps()
    {
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("OPENCONNECT_CLOUD_TEST_DATABASE")).Options;
        IDbContextFactory<CloudControlPlaneDbContext> factory = new TestFactory(options);
        await using (var db = await factory.CreateDbContextAsync()) await db.Database.MigrateAsync();
        var owner = new CloudSubject("issuer-a", Guid.NewGuid().ToString("N"));
        var billing = new CloudSubject("issuer-a", Guid.NewGuid().ToString("N"));
        var developer = new CloudSubject("issuer-a", Guid.NewGuid().ToString("N"));
        var tenantStore = new EfCloudTenantStore(factory, TimeProvider.System);
        var tenant = await tenantStore.CreateTenantAsync(owner, "Usage", default);
        var other = await tenantStore.CreateTenantAsync(owner, "Other usage", default);
        var app = (await tenantStore.CreateApplicationAsync(owner, tenant.Id, "App", "Development", default)).Application!;
        var otherApp = (await tenantStore.CreateApplicationAsync(owner, other.Id, "Other", "Development", default)).Application!;
        var now = new DateTimeOffset(2026, 1, 2, 12, 0, 0, TimeSpan.Zero);
        var bucket = Guid.NewGuid(); var otherBucket = Guid.NewGuid();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Memberships.AddRange(
                new CloudMembership { TenantId = tenant.Id, SubjectIssuer = billing.Issuer, SubjectId = billing.SubjectId, Role = "BillingReader", CreatedAt = now },
                new CloudMembership { TenantId = tenant.Id, SubjectIssuer = developer.Issuer, SubjectId = developer.SubjectId, Role = "Developer", CreatedAt = now });
            db.QuotaBuckets.AddRange(
                new CloudQuotaBucket { Id = bucket, TenantId = tenant.Id, ApplicationId = app.Id, CapabilityId = "classification.classify", Unit = "request", PeriodStart = now.AddDays(-1), PeriodEnd = now.AddDays(1), Limit = 10 },
                new CloudQuotaBucket { Id = otherBucket, TenantId = other.Id, ApplicationId = otherApp.Id, CapabilityId = "classification.classify", Unit = "request", PeriodStart = now.AddDays(-1), PeriodEnd = now.AddDays(1), Limit = 10 });
            await db.SaveChangesAsync();
            for (var i = 0; i < 3; i++)
                db.QuotaReservations.Add(new CloudQuotaReservation { Id = Guid.NewGuid(), BucketId = bucket, TenantId = tenant.Id, ApplicationId = app.Id,
                    RequestId = Guid.NewGuid(), Amount = 1, Actual = i == 0 ? 0 : 1, ExpiresAt = now.AddHours(1), FinalizedAt = now });
            db.QuotaReservations.Add(new CloudQuotaReservation { Id = Guid.NewGuid(), BucketId = otherBucket, TenantId = other.Id, ApplicationId = otherApp.Id,
                RequestId = Guid.NewGuid(), Amount = 1, Actual = 1, ExpiresAt = now.AddHours(1), FinalizedAt = now });
            await db.SaveChangesAsync();
        }
        var service = new CloudUsageQueryService(new EfCloudUsageReader(factory, TimeProvider.System));
        var first = await service.GetTenantAsync(billing, tenant.Id, now.AddDays(-1), now.AddDays(1), null, 2, default);
        Assert.NotNull(first); Assert.Equal(2, first.Entries.Count); Assert.NotNull(first.Next);
        var second = await service.GetTenantAsync(billing, tenant.Id, now.AddDays(-1), now.AddDays(1), first.Next, 2, default);
        Assert.Single(second!.Entries); Assert.Null(second.Next);
        var all = first.Entries.Concat(second.Entries).ToArray();
        Assert.Equal(3, all.Select(x => x.ReservationId).Distinct().Count());
        Assert.All(all, x => { Assert.Equal(app.Id, x.ApplicationId); Assert.Equal("request", x.Unit); });
        Assert.Contains(all, x => x.Quantity == 0);
        Assert.Empty((await service.GetTenantAsync(billing, tenant.Id, now.AddDays(1), now.AddDays(2), null, 2, default))!.Entries);
        Assert.Null(await service.GetTenantAsync(developer, tenant.Id, null, null, null, 2, default));
        Assert.Null(await service.GetTenantAsync(new CloudSubject("issuer-b", billing.SubjectId), tenant.Id, null, null, null, 2, default));
        Assert.Null(await service.GetTenantAsync(billing, other.Id, null, null, null, 2, default));
        Assert.NotNull(await service.GetTenantAsync(owner, tenant.Id, null, null, null, 2, default));
        await using (var db = await factory.CreateDbContextAsync())
        {
            Assert.True(await db.Database.SqlQueryRaw<bool>("SELECT EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname = 'cloud' AND indexname = 'IX_usage_records_TenantId_RecordedAt_ReservationId') AS \"Value\"").SingleAsync());
            Assert.True(await db.AuditEvents.AnyAsync(x => x.TenantId == tenant.Id && x.Action == "usage.records.read" && x.Outcome == "succeeded"));
            Assert.True(await db.AuditEvents.AnyAsync(x => x.TenantId == tenant.Id && x.Action == "usage.records.read" && x.Outcome == "forbidden"));
        }
        await VerifyHttpAsync(factory, tenant.Id, other.Id, app.Id, bucket, billing, developer);
    }

    private static async Task VerifyHttpAsync(IDbContextFactory<CloudControlPlaneDbContext> factory, Guid tenantId, Guid otherTenant,
        Guid applicationId, Guid bucketId, CloudSubject billing, CloudSubject developer)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(factory);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddScoped<ICloudUsageReader, EfCloudUsageReader>();
        builder.Services.AddScoped<CloudUsageQueryService>();
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
            options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; });
        builder.Services.AddAuthorization(options => options.AddPolicy("CloudApi", policy =>
        { policy.AddAuthenticationSchemes(CookieAuthenticationDefaults.AuthenticationScheme); policy.RequireAuthenticatedUser(); }));
        await using var app = builder.Build(); app.UseAuthentication(); app.UseAuthorization();
        app.MapGet("/test-sign-in/{role}", async (string role, HttpContext context) =>
        {
            var subject = role == "billing" ? billing : developer;
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, subject.SubjectId)], CookieAuthenticationDefaults.AuthenticationScheme);
            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
        });
        app.MapGroup("/cloud/api/v1").RequireAuthorization("CloudApi").MapCloudUsageEndpoints(billing.Issuer);
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(address) };
        var path = $"/cloud/api/v1/tenants/{tenantId}/usage";
        var export = path + "/export?from=2026-01-01T00%3A00%3A00Z&to=2026-01-04T00%3A00%3A00Z";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(export)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/test-sign-in/billing")).StatusCode);
        var response = await client.GetAsync(path + "?limit=2");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var page = await response.Content.ReadFromJsonAsync<CloudUsagePage>(); Assert.Equal(2, page!.Entries.Count); Assert.NotNull(page.Next);
        var nextUrl = path + "?limit=2&beforeAt=" + Uri.EscapeDataString(page.Next!.RecordedAt.ToString("O")) + "&beforeId=" + page.Next.ReservationId;
        var next = await client.GetFromJsonAsync<CloudUsagePage>(nextUrl);
        Assert.Single(next!.Entries); Assert.DoesNotContain(next.Entries[0].ReservationId, page.Entries.Select(x => x.ReservationId));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(path + "?limit=101")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(path + "?beforeId=" + Guid.NewGuid())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/cloud/api/v1/tenants/{otherTenant}/usage")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(path + "/export")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(path + "/export?from=2026-01-01T00%3A00%3A00Z&to=2027-01-01T00%3A00%3A00Z")).StatusCode);
        var csv = await client.GetAsync(export);
        Assert.Equal(HttpStatusCode.OK, csv.StatusCode);
        Assert.Equal("no-store", csv.Headers.CacheControl?.ToString());
        Assert.Equal("text/csv", csv.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", csv.Headers.GetValues("X-Content-Type-Options").Single());
        var text = await csv.Content.ReadAsStringAsync();
        Assert.Equal(4, text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Contains("classification.classify", text);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(export.Replace(tenantId.ToString(), otherTenant.ToString(), StringComparison.Ordinal))).StatusCode);
        await using (var db = await factory.CreateDbContextAsync())
            Assert.True(await db.AuditEvents.AnyAsync(x => x.TenantId == tenantId && x.Action == "usage.records.export" && x.Outcome == "succeeded"));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/test-sign-in/developer")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(export)).StatusCode);
        await using (var db = await factory.CreateDbContextAsync())
        {
            Assert.True(await db.AuditEvents.AnyAsync(x => x.TenantId == tenantId && x.Action == "usage.records.export" && x.Outcome == "forbidden"));
            var recorded = new DateTimeOffset(2026, 1, 2, 12, 0, 0, TimeSpan.Zero);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO cloud.quota_reservations ("Id", "BucketId", "TenantId", "ApplicationId", "RequestId", "Amount", "Actual", "ExpiresAt", "FinalizedAt")
                SELECT gen_random_uuid(), {bucketId}, {tenantId}, {applicationId}, gen_random_uuid(), 1, 1, {recorded.AddHours(1)}, {recorded}
                FROM generate_series(1, 10000)
                """);
        }
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/test-sign-in/billing")).StatusCode);
        var tooLarge = await client.GetAsync(export);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);
        Assert.DoesNotContain("classification.classify", await tooLarge.Content.ReadAsStringAsync());
        await using (var db = await factory.CreateDbContextAsync())
            Assert.True(await db.AuditEvents.AnyAsync(x => x.TenantId == tenantId && x.Action == "usage.records.export" && x.Outcome == "limit_exceeded"));
    }

    private sealed class TestFactory(DbContextOptions<CloudControlPlaneDbContext> options) : IDbContextFactory<CloudControlPlaneDbContext>
    {
        public CloudControlPlaneDbContext CreateDbContext() => new(options);
    }
}
