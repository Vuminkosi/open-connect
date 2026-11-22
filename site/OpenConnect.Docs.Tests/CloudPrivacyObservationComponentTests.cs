using System.Security.Claims;
using Bunit;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Docs.Cloud;
using OpenConnect.Docs.Components.Pages.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class CloudPrivacyObservationComponentTests : BunitContext
{
    [Fact]
    public void ShowsQualifiedCloudObservationsAndClearsThemWhenTenantChanges()
    {
        var tenant = Guid.NewGuid();
        var reader = new Reader(tenant);
        Services.AddSingleton<ICloudPrivacyObservationReader>(reader);
        Services.AddSingleton<CloudPrivacyObservationService>();
        Services.AddSingleton<AuthenticationStateProvider>(new TestAuthentication());
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["OpenConnect:Cloud:Identity:Enabled"] = "true", ["OpenConnect:Cloud:Identity:Authority"] = "https://issuer.example" }).Build();
        Services.AddSingleton<IConfiguration>(config);
        var component = Render<TenantSecurity>(p => p.Add(x => x.TenantId, tenant));
        Assert.Contains("Cloud privacy check blocked", component.Markup);
        Assert.Contains("These are Cloud-side classification checks", component.Markup);
        Assert.Contains("Host privacy reports are available separately", component.Markup);
        Assert.Contains("does not prove complete detection or worker execution", component.Markup);
        component.Find("#security-from").Change("2026-01-02");
        component.Find("#security-to").Change("2026-01-03");
        component.FindAll("button").Single(x => x.TextContent == "Apply dates").Click();
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero), reader.From);
        Assert.Equal(new DateTimeOffset(2026, 1, 4, 0, 0, 0, TimeSpan.Zero), reader.To);
        component.Render(p => p.Add(x => x.TenantId, Guid.NewGuid()));
        Assert.Contains("does not allow security access", component.Markup);
        Assert.DoesNotContain("Cloud privacy check blocked", component.Markup);
        config["OpenConnect:Cloud:Identity:Enabled"] = "false";
        component.Render(p => p.Add(x => x.TenantId, tenant));
        Assert.Contains("identity is no longer available", component.Markup);
        Assert.DoesNotContain("Cloud privacy check blocked", component.Markup);
    }

    private sealed class TestAuthentication : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(
            new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "analyst", ClaimValueTypes.String, "https://issuer.example")], "test"))));
    }

    private sealed class Reader(Guid tenant) : ICloudPrivacyObservationReader
    {
        public DateTimeOffset? From { get; private set; }
        public DateTimeOffset? To { get; private set; }
        public Task<CloudPrivacyObservationPage?> GetTenantAsync(CloudSubject subject, Guid tenantId, DateTimeOffset? from, DateTimeOffset? to,
            CloudPrivacyObservationCursor? before, int limit, CancellationToken cancellationToken)
        {
            From = from;
            To = to;
            if (tenantId != tenant)
                return Task.FromResult<CloudPrivacyObservationPage?>(null);
            var observation = new CloudPrivacyObservation(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "privacy_blocked", Guid.NewGuid().ToString("N"), DateTimeOffset.UnixEpoch);
            return Task.FromResult<CloudPrivacyObservationPage?>(new([observation], null));
        }
    }
}
