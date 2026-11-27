using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Docs.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class CloudLiveUIReconciliationTests
{
    [Fact]
    public async Task HttpEndpointRequiresAnAuthorizedOperatorAndValidCursor()
    {
        const string issuer = "https://issuer.example";
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OpenConnect:Cloud:Identity:Enabled"] = "true",
            ["OpenConnect:Cloud:Identity:Authority"] = issuer,
            ["OpenConnect:Cloud:Operators:0:Issuer"] = issuer,
            ["OpenConnect:Cloud:Operators:0:Subject"] = "operator"
        });
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<CloudOperatorAccess>();
        builder.Services.AddSingleton<ICloudLiveUIReconciliationReader, Reader>();
        builder.Services.AddScoped<CloudLiveUIReconciliation>();
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
        {
            options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; };
            options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
        });
        builder.Services.AddAuthorization(options => options.AddPolicy("CloudApi", policy =>
            policy.AddAuthenticationSchemes(CookieAuthenticationDefaults.AuthenticationScheme).RequireAuthenticatedUser()));
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/test-sign-in/{subject}", async (string subject, HttpContext context) =>
            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, subject, ClaimValueTypes.String, issuer)], CookieAuthenticationDefaults.AuthenticationScheme))));
        app.MapCloudLiveUIReconciliation(true);
        await app.StartAsync();
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        const string path = "/cloud/api/v1/operator/liveui/unknown-usage";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
        await client.GetAsync("/test-sign-in/other");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(path)).StatusCode);
        await client.GetAsync("/test-sign-in/operator");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(path + "?afterId=" + Guid.NewGuid())).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(path + "?limit=101")).StatusCode);
        var result = await client.GetAsync(path + "?limit=1");
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal("no-store", result.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task OnlyConfiguredOperatorCanReadAndPaginationIsBounded()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OpenConnect:Cloud:Identity:Enabled"] = "true",
            ["OpenConnect:Cloud:Identity:Authority"] = "https://issuer.example",
            ["OpenConnect:Cloud:Operators:0:Issuer"] = "https://issuer.example",
            ["OpenConnect:Cloud:Operators:0:Subject"] = "operator"
        }).Build();
        var reader = new Reader();
        var service = new CloudLiveUIReconciliation(new CloudOperatorAccess(configuration), reader, TimeProvider.System);
        ClaimsPrincipal Principal(string subject) => new(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, subject, ClaimValueTypes.String, "https://issuer.example")], "test"));
        Assert.Equal("forbidden", (await service.ListUnknownAsync(Principal("other"), null, 20, default)).Status);
        Assert.Equal(0, reader.Calls);
        Assert.Equal("invalid_request", (await service.ListUnknownAsync(Principal("operator"), null, 101, default)).Status);
        Assert.Equal("invalid_request", (await service.ListUnknownAsync(Principal("operator"), new(DateTimeOffset.UtcNow, Guid.Empty), 2, default)).Status);
        var page = await service.ListUnknownAsync(Principal("operator"), null, 1, default);
        Assert.Equal("succeeded", page.Status);
        Assert.Single(page.Cases!);
        Assert.NotNull(page.Next);
        Assert.Equal(page.Cases![0].ReservationId, page.Next!.ReservationId);
        Assert.Equal(1, reader.Calls);
        Assert.Equal(2, reader.Count);
    }

    private sealed class Reader : ICloudLiveUIReconciliationReader
    {
        public int Calls { get; private set; }
        public int Count { get; private set; }
        public Task<IReadOnlyList<CloudLiveUIUnknownUsage>> ReadUnknownAsync(CloudLiveUIReconciliationCursor? after, int count,
            DateTimeOffset cutoff, CancellationToken cancellationToken)
        {
            Calls++;
            Count = count;
            IReadOnlyList<CloudLiveUIUnknownUsage> rows = Enumerable.Range(0, count).Select(_ => new CloudLiveUIUnknownUsage(
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), cutoff, 2048)).ToArray();
            return Task.FromResult(rows);
        }
    }
}
