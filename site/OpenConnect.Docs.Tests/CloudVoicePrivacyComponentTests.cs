using System.Security.Claims;
using Bunit;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Docs.Cloud;
using OpenConnect.Docs.Components.Pages.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class CloudVoicePrivacyComponentTests : BunitContext
{
    [Fact]
    public async Task DisplaysSeparateBoundariesAndClearsObservationsWhenIdentityChanges()
    {
        var tenant = Guid.NewGuid();
        var authentication = new MutableAuthentication();
        Services.AddSingleton<ICloudVoicePrivacyReader>(new Reader(tenant));
        Services.AddSingleton<AuthenticationStateProvider>(authentication);
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["OpenConnect:Cloud:Identity:Enabled"] = "true", ["OpenConnect:Cloud:Identity:Authority"] = "https://issuer.example" }).Build());
        var component = Render<VoicePrivacyObservations>(parameters => parameters.Add(x => x.TenantId, tenant));
        Assert.Contains("Synthesis input", component.Markup);
        Assert.Contains("Recognized output transcript", component.Markup);
        Assert.Contains("does not confirm audio delivery or complete recognition", component.Markup);
        Assert.Contains("check blocked", component.Markup);
        component.Find("nav[aria-label='Voice observation pages'] button:nth-child(2)").Click();
        component.WaitForAssertion(() => Assert.Contains("detector unavailable", component.Markup));
        component.Find("nav[aria-label='Voice observation pages'] button:nth-child(1)").Click();
        component.WaitForAssertion(() => Assert.Contains("Recognized output transcript", component.Markup));
        await component.InvokeAsync(authentication.SignOut);
        component.WaitForAssertion(() => Assert.DoesNotContain("Recognized output transcript", component.Markup));
        Assert.Contains("Your identity changed", component.Markup);
    }

    private sealed class Reader(Guid tenant) : ICloudVoicePrivacyReader
    {
        public Task<CloudVoicePrivacyPage?> GetTenantAsync(CloudSubject subject, Guid tenantId, CloudVoicePrivacyCursor? before, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<CloudVoicePrivacyPage?>(tenantId != tenant ? null : before is not null
                ? new([new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "input", "detector_unavailable", new string('d', 32), DateTimeOffset.UnixEpoch, null)], null)
                : new([
                    new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "input", "privacy_blocked", new string('a', 32), DateTimeOffset.UnixEpoch, null),
                    new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "output", "allowed", new string('b', 32), DateTimeOffset.UnixEpoch, new string('c', 64))],
                    new(DateTimeOffset.UnixEpoch, Guid.NewGuid(), "output")));
    }

    private sealed class MutableAuthentication : AuthenticationStateProvider
    {
        private bool _signedIn = true;
        public void SignOut() { _signedIn = false; NotifyAuthenticationStateChanged(GetAuthenticationStateAsync()); }
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(_signedIn
            ? new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "analyst", ClaimValueTypes.String, "https://issuer.example")], "test"))
            : new ClaimsPrincipal(new ClaimsIdentity())));
    }
}
