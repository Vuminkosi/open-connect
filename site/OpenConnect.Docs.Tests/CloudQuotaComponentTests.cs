using System.Security.Claims;
using Bunit;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Docs.Cloud;
using OpenConnect.Docs.Components.Pages.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class CloudQuotaComponentTests : BunitContext
{
    [Fact]
    public void ShowsQuotaCountersAndPagesWithoutShowingPriorTenant()
    {
        var tenant = Guid.NewGuid();
        var other = Guid.NewGuid();
        var reader = new Reader(tenant);
        Services.AddSingleton<ICloudQuotaReader>(reader);
        Services.AddSingleton<CloudQuotaQueryService>();
        Services.AddSingleton<AuthenticationStateProvider>(new TestAuthentication());
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["OpenConnect:Cloud:Identity:Authority"] = "https://issuer.example" }).Build());
        var component = Render<TenantQuotas>(p => p.Add(x => x.TenantId, tenant));
        Assert.Contains("inference.chat", component.Markup);
        Assert.Contains("not invoices", component.Markup);
        component.FindAll("button").Single(x => x.TextContent == "Next").Click();
        Assert.Contains("classification.classify", component.Markup);
        Assert.DoesNotContain("inference.chat", component.Markup);
        component.Render(p => p.Add(x => x.TenantId, other));
        Assert.Contains("does not allow quota access", component.Markup);
        Assert.DoesNotContain("classification.classify", component.Markup);
    }

    private sealed class TestAuthentication : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(
            new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "owner")], "test"))));
    }

    private sealed class Reader(Guid tenant) : ICloudQuotaReader
    {
        public Task<CloudQuotaPage?> GetTenantAsync(CloudSubject subject, Guid tenantId, int offset, int limit, CancellationToken cancellationToken)
        {
            if (tenantId != tenant)
                return Task.FromResult<CloudQuotaPage?>(null);
            var quota = new CloudQuotaView(Guid.NewGuid(), Guid.NewGuid(), offset == 0 ? "inference.chat" : "classification.classify",
                "request", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddDays(1), 10, 2, 3);
            return Task.FromResult<CloudQuotaPage?>(new([quota], offset, offset == 0 ? limit : null));
        }
    }
}
