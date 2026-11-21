using System.Security.Claims;
using Bunit;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Docs.Cloud;
using OpenConnect.Docs.Components.Pages.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class CloudUsageComponentTests : BunitContext
{
    [Fact]
    public void ShowsQualifiedUsageAndClearsPreviousTenant()
    {
        var tenant = Guid.NewGuid();
        var reader = new Reader(tenant);
        Services.AddSingleton<ICloudUsageReader>(reader);
        Services.AddSingleton<CloudUsageQueryService>();
        Services.AddSingleton<ICloudUsageSummaryReader>(new SummaryReader(tenant));
        Services.AddSingleton<CloudUsageSummaryService>();
        Services.AddSingleton<ICloudAnalyticsComputeUsageReader>(new ComputeReader(tenant));
        Services.AddSingleton<AuthenticationStateProvider>(new TestAuthentication());
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["OpenConnect:Cloud:Identity:Enabled"] = "true", ["OpenConnect:Cloud:Identity:Authority"] = "https://issuer.example" }).Build());
        var component = Render<TenantUsage>(p => p.Add(x => x.TenantId, tenant));
        Assert.Contains("classification.classify", component.Markup);
        Assert.Contains("not invoices or prices", component.Markup);
        component.Find("#usage-from").Change("2026-01-02");
        component.Find("#usage-to").Change("2026-01-03");
        component.FindAll("button").Single(x => x.TextContent == "Apply dates").Click();
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero), reader.From);
        Assert.Equal(new DateTimeOffset(2026, 1, 4, 0, 0, 0, TimeSpan.Zero), reader.To);
        Assert.Contains($"/cloud/api/v1/tenants/{tenant:D}/usage/export?", component.Find("a").GetAttribute("href"));
        Assert.Contains("Daily breakdown", component.Markup);
        Assert.Contains("Analytics calculation time", component.Markup);
        Assert.Contains("1234 ms", component.Markup);
        Assert.Contains("Ledger records", component.Markup);
        component.Render(p => p.Add(x => x.TenantId, Guid.NewGuid()));
        Assert.Contains("does not allow usage access", component.Markup);
        Assert.DoesNotContain("classification.classify", component.Markup);
        Assert.DoesNotContain("1234 ms", component.Markup);
    }

    private sealed class TestAuthentication : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(
            new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "billing", ClaimValueTypes.String, "https://issuer.example")], "test"))));
    }

    private sealed class SummaryReader(Guid tenant) : ICloudUsageSummaryReader
    {
        public Task<CloudUsageSummary?> GetTenantAsync(CloudSubject subject, Guid tenantId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken) =>
            Task.FromResult<CloudUsageSummary?>(tenantId == tenant ? new([new CloudUsageDailyTotal(DateTime.UnixEpoch, Guid.NewGuid(), "classification.classify", "request", 1, 1)], false) : null);
    }

    private sealed class ComputeReader(Guid tenant) : ICloudAnalyticsComputeUsageReader
    {
        public Task<CloudAnalyticsComputeSummary?> GetTenantAsync(CloudSubject subject, Guid tenantId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken) =>
            Task.FromResult<CloudAnalyticsComputeSummary?>(tenantId == tenant ? new([new CloudAnalyticsComputeDailyTotal(DateTime.UnixEpoch, Guid.NewGuid(), 1234, 2)], false) : null);
    }

    private sealed class Reader(Guid tenant) : ICloudUsageReader
    {
        public Task<CloudUsagePage?> ExportTenantAsync(CloudSubject subject, Guid tenantId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken) => throw new NotSupportedException();
        public DateTimeOffset? From { get; private set; }
        public DateTimeOffset? To { get; private set; }
        public Task<CloudUsagePage?> GetTenantAsync(CloudSubject subject, Guid tenantId, DateTimeOffset? from, DateTimeOffset? to,
            CloudUsageCursor? before, int limit, CancellationToken cancellationToken)
        {
            From = from; To = to;
            return Task.FromResult<CloudUsagePage?>(tenantId != tenant ? null : new([new CloudUsageEntry(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                "classification.classify", "request", 1, "reservation", DateTimeOffset.UnixEpoch)], null));
        }
    }
}
