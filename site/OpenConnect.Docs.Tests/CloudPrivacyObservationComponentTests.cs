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
        Services.AddSingleton<ICloudPrivacyStatisticsReader>(new StatisticsReader(tenant));
        Services.AddSingleton<ICloudHostPrivacyObservationReader>(new HostReader(tenant));
        Services.AddSingleton<ICloudAnalyticsPrivacyReader>(new AnalyticsReader(tenant));
        Services.AddSingleton<ICloudKnowledgeEmbeddingPrivacyReader>(new EmbeddingReader(tenant));
        Services.AddSingleton<CloudPrivacyObservationService>();
        Services.AddSingleton<AuthenticationStateProvider>(new TestAuthentication());
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["OpenConnect:Cloud:Identity:Enabled"] = "true", ["OpenConnect:Cloud:Identity:Authority"] = "https://issuer.example" }).Build();
        Services.AddSingleton<IConfiguration>(config);
        var component = Render<TenantSecurity>(p => p.Add(x => x.TenantId, tenant));
        Assert.Contains("Cloud privacy check blocked", component.Markup);
        Assert.Contains("blocked the request before worker dispatch", component.Markup);
        Assert.Contains("Cloud action", component.Markup);
        Assert.Contains("EMAIL: 2", component.Markup);
        Assert.Contains("Recorded Cloud checks", component.Markup);
        Assert.Contains("Detected sensitive information", component.Markup);
        Assert.Contains("Category evidence was unavailable for 3 checks", component.Markup);
        Assert.Contains("Observation ID", component.Markup);
        Assert.Contains("Host privacy reports", component.Markup);
        Assert.Contains("Analytics privacy cases", component.Markup);
        Assert.Contains("Embedding privacy cases", component.Markup);
        Assert.Contains("PAYMENT_CARD: 19", component.Markup);
        Assert.Contains("SECRET: 37", component.Markup);
        Assert.Contains("Review host case evidence", component.Markup);
        Assert.Contains("Host firewall blocked", component.Markup);
        Assert.Contains("a caller may apply additional checks", component.Markup);
        Assert.Contains("All recorded host decisions", component.Markup);
        Assert.Contains("Recorded policy actions", component.Markup);
        Assert.Contains("Mask: 4", component.Markup);
        Assert.Contains("Inspection failed; the host blocked the decision: 1", component.Markup);
        Assert.Contains("Detected categories at the output boundary", component.Markup);
        Assert.Contains("PHONE: 8", component.Markup);
        Assert.Contains("EMAIL: 9", component.Markup);
        Assert.Contains("PHONE: 3", component.Markup);
        Assert.Contains("Cloud classification date filters do not apply", component.Markup);
        Assert.Contains("Review case evidence", component.Markup);
        Assert.Contains("include repeat requests", component.Markup);
        Assert.Contains("Cloud classification checks and host privacy reports are shown separately", component.Markup);
        Assert.Contains("counts do not represent a complete inventory of sensitive information", component.Markup);
        Assert.Contains("does not prove complete detection or worker execution", component.Markup);
        component.Find("#security-from").Change("2026-01-02");
        component.Find("#security-to").Change("2026-01-03");
        component.FindAll("button").Single(x => x.TextContent == "Apply dates").Click();
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero), reader.From);
        Assert.Equal(new DateTimeOffset(2026, 1, 4, 0, 0, 0, TimeSpan.Zero), reader.To);
        component.Render(p => p.Add(x => x.TenantId, Guid.NewGuid()));
        Assert.Contains("does not allow security access", component.Markup);
        Assert.DoesNotContain("Cloud privacy check blocked", component.Markup);
        Assert.DoesNotContain("PHONE: 3", component.Markup);
        Assert.DoesNotContain("SECRET: 37", component.Markup);
        Assert.DoesNotContain("PAYMENT_CARD: 19", component.Markup);
        config["OpenConnect:Cloud:Identity:Enabled"] = "false";
        component.Render(p => p.Add(x => x.TenantId, tenant));
        Assert.Contains("identity is no longer available", component.Markup);
        Assert.DoesNotContain("Cloud privacy check blocked", component.Markup);
    }

    private sealed class AnalyticsReader(Guid tenant) : ICloudAnalyticsPrivacyReader
    {
        public Task<CloudAnalyticsPrivacyDashboard?> GetTenantAsync(CloudSubject subject, Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<CloudAnalyticsPrivacyDashboard?>(tenantId != tenant ? null : new(
                [new CloudAnalyticsPrivacyCase(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "response", "privacy_blocked", new string('a', 32),
                    DateTimeOffset.UnixEpoch, [new CloudPrivacyCategoryCount("SECRET", 37)])],
                [new CloudAnalyticsPrivacyTotal("response", "privacy_blocked", 1)],
                [new CloudAnalyticsCategoryTotal("response", "SECRET", 37)]));
    }

    private sealed class EmbeddingReader(Guid tenant) : ICloudKnowledgeEmbeddingPrivacyReader
    {
        public Task<CloudKnowledgeEmbeddingPrivacyDashboard?> GetTenantAsync(CloudSubject subject, Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<CloudKnowledgeEmbeddingPrivacyDashboard?>(tenantId != tenant ? null : new(
                [new CloudKnowledgeEmbeddingPrivacyCase(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "request", "privacy_blocked", new string('b', 32),
                    DateTimeOffset.UnixEpoch, [new CloudPrivacyCategoryCount("PAYMENT_CARD", 19)])],
                [new CloudKnowledgeEmbeddingPrivacyTotal("request", "privacy_blocked", 1)],
                [new CloudKnowledgeEmbeddingCategoryTotal("request", "PAYMENT_CARD", 19)]));
    }

    [Fact]
    public void Host_case_shows_recorded_action_and_clears_on_identity_change()
    {
        var tenant = Guid.NewGuid();
        var caseId = Guid.NewGuid();
        var authentication = new MutableAuthentication();
        Services.AddSingleton<ICloudHostPrivacyCaseReader>(new HostCaseReader(tenant, caseId));
        Services.AddSingleton<AuthenticationStateProvider>(authentication);
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["OpenConnect:Cloud:Identity:Enabled"] = "true", ["OpenConnect:Cloud:Identity:Authority"] = "https://issuer.example" }).Build());
        var component = Render<TenantHostPrivacyCase>(p => p.Add(x => x.TenantId, tenant).Add(x => x.CaseId, caseId));
        Assert.Contains(caseId.ToString(), component.Markup);
        Assert.Contains("PHONE: 3", component.Markup);
        Assert.Contains("Blocked", component.Markup);
        Assert.Contains("Inspection failed; the host blocked the decision", component.Markup);
        Assert.Contains("further checks", component.Markup);
        component.Render(p => p.Add(x => x.TenantId, Guid.NewGuid()).Add(x => x.CaseId, caseId));
        Assert.Contains("does not allow security access", component.Markup);
        Assert.DoesNotContain("PHONE: 3", component.Markup);
        component.Render(p => p.Add(x => x.TenantId, tenant).Add(x => x.CaseId, caseId));
        Assert.Contains("PHONE: 3", component.Markup);
        authentication.SignOut();
        component.WaitForAssertion(() => Assert.Contains("Your identity changed", component.Markup));
        Assert.DoesNotContain("PHONE: 3", component.Markup);
    }

    [Fact]
    public void Case_page_shows_evidence_and_clears_it_when_access_changes()
    {
        var tenant = Guid.NewGuid();
        var caseId = Guid.NewGuid();
        Services.AddSingleton<ICloudPrivacyCaseReader>(new CaseReader(tenant, caseId));
        Services.AddSingleton<AuthenticationStateProvider>(new TestAuthentication());
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["OpenConnect:Cloud:Identity:Enabled"] = "true", ["OpenConnect:Cloud:Identity:Authority"] = "https://issuer.example" }).Build();
        Services.AddSingleton<IConfiguration>(config);
        var component = Render<TenantPrivacyCase>(p => p.Add(x => x.TenantId, tenant).Add(x => x.CaseId, caseId));
        Assert.Contains("blocked the request before worker dispatch", component.Markup);
        Assert.Contains("EMAIL: 2", component.Markup);
        Assert.Contains(caseId.ToString(), component.Markup);
        component.Render(p => p.Add(x => x.TenantId, Guid.NewGuid()).Add(x => x.CaseId, caseId));
        Assert.Contains("does not allow security access", component.Markup);
        Assert.DoesNotContain("EMAIL: 2", component.Markup);
        config["OpenConnect:Cloud:Identity:Enabled"] = "false";
        component.Render(p => p.Add(x => x.TenantId, tenant).Add(x => x.CaseId, caseId));
        Assert.DoesNotContain("EMAIL: 2", component.Markup);
    }

    [Fact]
    public void Security_page_clears_all_evidence_when_authentication_changes()
    {
        var tenant = Guid.NewGuid();
        var authentication = new MutableAuthentication();
        Services.AddSingleton<ICloudPrivacyObservationReader>(new Reader(tenant));
        Services.AddSingleton<ICloudPrivacyStatisticsReader>(new StatisticsReader(tenant));
        Services.AddSingleton<ICloudHostPrivacyObservationReader>(new HostReader(tenant));
        Services.AddSingleton<CloudPrivacyObservationService>();
        Services.AddSingleton<AuthenticationStateProvider>(authentication);
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["OpenConnect:Cloud:Identity:Enabled"] = "true", ["OpenConnect:Cloud:Identity:Authority"] = "https://issuer.example" }).Build());
        var component = Render<TenantSecurity>(p => p.Add(x => x.TenantId, tenant));
        Assert.Contains("PHONE: 3", component.Markup);
        Assert.Contains("Cloud privacy check blocked", component.Markup);
        authentication.SignOut();
        component.WaitForAssertion(() => Assert.Contains("Your identity changed", component.Markup));
        Assert.DoesNotContain("PHONE: 3", component.Markup);
        Assert.DoesNotContain("Cloud privacy check blocked", component.Markup);
    }

    [Fact]
    public void Case_page_clears_displayed_evidence_on_authentication_notification()
    {
        var tenant = Guid.NewGuid();
        var caseId = Guid.NewGuid();
        var authentication = new MutableAuthentication();
        Services.AddSingleton<ICloudPrivacyCaseReader>(new CaseReader(tenant, caseId));
        Services.AddSingleton<AuthenticationStateProvider>(authentication);
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["OpenConnect:Cloud:Identity:Enabled"] = "true", ["OpenConnect:Cloud:Identity:Authority"] = "https://issuer.example" }).Build());
        var component = Render<TenantPrivacyCase>(p => p.Add(x => x.TenantId, tenant).Add(x => x.CaseId, caseId));
        Assert.Contains("EMAIL: 2", component.Markup);
        authentication.SignOut();
        component.WaitForAssertion(() => Assert.Contains("Your identity changed", component.Markup));
        Assert.DoesNotContain("EMAIL: 2", component.Markup);
    }

    [Fact]
    public void Case_page_discards_in_flight_evidence_after_identity_changes()
    {
        var tenant = Guid.NewGuid();
        var caseId = Guid.NewGuid();
        var reader = new DelayedCaseReader();
        var authentication = new MutableAuthentication();
        Services.AddSingleton<ICloudPrivacyCaseReader>(reader);
        Services.AddSingleton<AuthenticationStateProvider>(authentication);
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["OpenConnect:Cloud:Identity:Enabled"] = "true", ["OpenConnect:Cloud:Identity:Authority"] = "https://issuer.example" }).Build());
        var component = Render<TenantPrivacyCase>(p => p.Add(x => x.TenantId, tenant).Add(x => x.CaseId, caseId));
        Assert.Contains("Loading case evidence", component.Markup);
        authentication.SignedIn = false;
        reader.Completion.SetResult(new CloudPrivacyObservation(caseId, Guid.NewGuid(), Guid.NewGuid(), "privacy_blocked", "trace", DateTimeOffset.UnixEpoch)
            { Categories = [new CloudPrivacyCategoryCount("EMAIL", 2)] });
        component.WaitForAssertion(() => Assert.Contains("does not allow security access", component.Markup));
        Assert.DoesNotContain("EMAIL: 2", component.Markup);
    }

    [Fact]
    public void Hides_observations_if_statistics_access_is_revoked()
    {
        var tenant = Guid.NewGuid();
        Services.AddSingleton<ICloudPrivacyObservationReader>(new Reader(tenant));
        Services.AddSingleton<ICloudPrivacyStatisticsReader>(new RevokedStatisticsReader());
        Services.AddSingleton<CloudPrivacyObservationService>();
        Services.AddSingleton<AuthenticationStateProvider>(new TestAuthentication());
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["OpenConnect:Cloud:Identity:Enabled"] = "true", ["OpenConnect:Cloud:Identity:Authority"] = "https://issuer.example" }).Build());
        var component = Render<TenantSecurity>(p => p.Add(x => x.TenantId, tenant));
        Assert.Contains("does not allow security access", component.Markup);
        Assert.DoesNotContain("Cloud privacy check blocked", component.Markup);
        Assert.DoesNotContain("Recorded Cloud checks", component.Markup);
    }

    private sealed class TestAuthentication : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(
            new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "analyst", ClaimValueTypes.String, "https://issuer.example")], "test"))));
    }

    private sealed class CaseReader(Guid tenant, Guid caseId) : ICloudPrivacyCaseReader
    {
        public Task<CloudPrivacyObservation?> GetAsync(CloudSubject subject, Guid tenantId, Guid id, CancellationToken cancellationToken) =>
            Task.FromResult<CloudPrivacyObservation?>(tenantId == tenant && id == caseId
                ? new CloudPrivacyObservation(caseId, Guid.NewGuid(), Guid.NewGuid(), "privacy_blocked", "trace", DateTimeOffset.UnixEpoch)
                { Categories = [new CloudPrivacyCategoryCount("EMAIL", 2)] } : null);
    }

    private sealed class HostCaseReader(Guid tenant, Guid caseId) : ICloudHostPrivacyCaseReader
    {
        public Task<CloudHostPrivacyObservation?> GetAsync(CloudSubject subject, Guid tenantId, Guid id, CancellationToken cancellationToken) =>
            Task.FromResult<CloudHostPrivacyObservation?>(tenantId == tenant && id == caseId
                ? new CloudHostPrivacyObservation(caseId, Guid.NewGuid(), "Output", false, "Block", DateTimeOffset.UnixEpoch,
                    [new CloudPrivacyCategoryCount("PHONE", 3)]) { ReasonCode = "inspection_failed" } : null);
    }

    private sealed class DelayedCaseReader : ICloudPrivacyCaseReader
    {
        public TaskCompletionSource<CloudPrivacyObservation?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<CloudPrivacyObservation?> GetAsync(CloudSubject subject, Guid tenantId, Guid caseId, CancellationToken cancellationToken) => Completion.Task;
    }

    private sealed class MutableAuthentication : AuthenticationStateProvider
    {
        public bool SignedIn { get; set; } = true;
        public void SignOut() { SignedIn = false; NotifyAuthenticationStateChanged(GetAuthenticationStateAsync()); }
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(SignedIn
            ? new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "analyst", ClaimValueTypes.String, "https://issuer.example")], "test"))
            : new ClaimsPrincipal(new ClaimsIdentity())));
    }

    private sealed class Reader(Guid tenant) : ICloudPrivacyObservationReader
    {
        public DateTimeOffset? From { get; private set; }
        public DateTimeOffset? To { get; private set; }
        public Task<CloudPrivacyObservationPage?> GetTenantAsync(CloudSubject subject, Guid tenantId, DateTimeOffset? from, DateTimeOffset? to,
            CloudPrivacyObservationCursor? before, int limit, CancellationToken cancellationToken)
        {
            From = from; To = to;
            if (tenantId != tenant) return Task.FromResult<CloudPrivacyObservationPage?>(null);
            var observation = new CloudPrivacyObservation(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "privacy_blocked", Guid.NewGuid().ToString("N"), DateTimeOffset.UnixEpoch)
                { Categories = [new CloudPrivacyCategoryCount("EMAIL", 2)] };
            return Task.FromResult<CloudPrivacyObservationPage?>(new([observation], null));
        }
    }

    private sealed class HostReader(Guid tenant) : ICloudHostPrivacyObservationReader
    {
        public Task<CloudHostPrivacyObservationPage?> GetTenantAsync(CloudSubject subject, Guid tenantId,
            CloudPrivacyObservationCursor? before, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<CloudHostPrivacyObservationPage?>(tenantId == tenant ? new([new CloudHostPrivacyObservation(
                Guid.NewGuid(), Guid.NewGuid(), "Output", false, "Block", DateTimeOffset.UnixEpoch,
                [new CloudPrivacyCategoryCount("PHONE", 3)])], null)
                { Statistics = new CloudHostPrivacyStatistics(2, 3, 4, 1, [new CloudPrivacyCategoryTotal("EMAIL", 9)])
                    { Actions = [new CloudPrivacyActionTotal("Mask", 4)],
                      Reasons = [new CloudPrivacyReasonTotal("inspection_failed", 1)],
                      OutputCategories = [new CloudPrivacyCategoryTotal("PHONE", 8)] } } : null);
    }

    private sealed class StatisticsReader(Guid tenant) : ICloudPrivacyStatisticsReader
    {
        public Task<CloudPrivacyStatistics?> GetTenantAsync(CloudSubject subject, Guid tenantId, DateTimeOffset? from,
            DateTimeOffset? to, CancellationToken cancellationToken) => Task.FromResult<CloudPrivacyStatistics?>(
                tenantId == tenant ? new CloudPrivacyStatistics(7, 2, 1)
                { Categories = [new CloudPrivacyCategoryTotal("EMAIL", 4)], CategoryEvidenceUnavailable = 3 } : null);
    }

    private sealed class RevokedStatisticsReader : ICloudPrivacyStatisticsReader
    {
        public Task<CloudPrivacyStatistics?> GetTenantAsync(CloudSubject subject, Guid tenantId, DateTimeOffset? from,
            DateTimeOffset? to, CancellationToken cancellationToken) => Task.FromResult<CloudPrivacyStatistics?>(null);
    }
}
