using System.Security.Claims;
using Bunit;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Docs.Cloud;
using OpenConnect.Docs.Components.Pages.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class CloudHostPrivacyDashboardTests : BunitContext
{
    [Fact]
    public void ReportsAndCaseClearWhenIdentityOrTenantChanges()
    {
        var tenant = Guid.NewGuid(); var eventId = Guid.NewGuid();
        var auth = new MutableAuthentication();
        var reader = new Reader(tenant, eventId);
        Services.AddSingleton<ICloudHostPrivacyObservationReader>(reader);
        Services.AddSingleton<ICloudHostPrivacyCaseReader>(reader);
        Services.AddSingleton<AuthenticationStateProvider>(auth);
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["OpenConnect:Cloud:Identity:Enabled"] = "true", ["OpenConnect:Cloud:Identity:Authority"] = "https://issuer.example" }).Build());
        var reports = Render<TenantHostPrivacyReports>(p => p.Add(x => x.TenantId, tenant));
        Assert.Contains("PHONE: 3", reports.Markup);
        Assert.NotNull(reports.Find($"a[href='/cloud/tenants/{tenant}/security/host-cases/{eventId}']"));
        Assert.True(reports.Find("nav[aria-label='Host privacy report pages'] button").HasAttribute("disabled"));
        var detail = Render<TenantHostPrivacyCase>(p => p.Add(x => x.TenantId, tenant).Add(x => x.CaseId, eventId));
        Assert.Contains("PHONE: 3", detail.Markup);
        reports.Render(p => p.Add(x => x.TenantId, Guid.NewGuid()));
        Assert.DoesNotContain("PHONE: 3", reports.Markup);
        reports.Render(p => p.Add(x => x.TenantId, tenant));
        Assert.Contains("PHONE: 3", reports.Markup);
        auth.SignOut();
        reports.WaitForAssertion(() => Assert.Contains("Your identity changed", reports.Markup));
        detail.WaitForAssertion(() => Assert.Contains("Your identity changed", detail.Markup));
        Assert.DoesNotContain("PHONE: 3", reports.Markup);
        Assert.DoesNotContain("PHONE: 3", detail.Markup);
    }

    private sealed class MutableAuthentication : AuthenticationStateProvider
    {
        private ClaimsPrincipal _user = new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "analyst", ClaimValueTypes.String, "https://issuer.example")], "test"));
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(_user));
        public void SignOut() { _user = new ClaimsPrincipal(new ClaimsIdentity()); NotifyAuthenticationStateChanged(GetAuthenticationStateAsync()); }
    }
    private sealed class Reader(Guid tenant, Guid eventId) : ICloudHostPrivacyObservationReader, ICloudHostPrivacyCaseReader
    {
        private CloudHostPrivacyObservation Observation => new(eventId, Guid.NewGuid(), "Output", false, "Block", DateTimeOffset.UnixEpoch, [new("PHONE", 3)]);
        public Task<CloudHostPrivacyObservationPage?> GetTenantAsync(CloudSubject subject, Guid id, CloudPrivacyObservationCursor? before, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<CloudHostPrivacyObservationPage?>(id == tenant ? new([Observation], null) : null);
        public Task<CloudHostPrivacyObservation?> GetAsync(CloudSubject subject, Guid id, Guid caseId, CancellationToken cancellationToken) =>
            Task.FromResult<CloudHostPrivacyObservation?>(id == tenant && caseId == eventId ? Observation : null);
    }
}
