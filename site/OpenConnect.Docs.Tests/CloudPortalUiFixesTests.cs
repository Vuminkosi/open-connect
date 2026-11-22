using System.Security.Claims;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Docs.Cloud;
using OpenConnect.Docs.Components.Layout;
using OpenConnect.Docs.Components.Pages;
using OpenConnect.Docs.Components.Pages.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class CloudPortalUiFixesTests : BunitContext
{
    [Fact]
    public void ErrorPage_HasUniqueErrorRouteAndRendersTraceIdWithoutDiagnostics()
    {
        var errorRoutes = typeof(Error).GetCustomAttributes(typeof(RouteAttribute), inherit: false)
            .Cast<RouteAttribute>().Select(r => r.Template).ToArray();
        var notFoundRoutes = typeof(NotFound).GetCustomAttributes(typeof(RouteAttribute), inherit: false)
            .Cast<RouteAttribute>().Select(r => r.Template).ToArray();

        Assert.Contains("/error", errorRoutes);
        Assert.DoesNotContain("/error", notFoundRoutes);

        var httpContext = new DefaultHttpContext { TraceIdentifier = "trace-portal-42" };
        var cut = Render<Error>(parameters => parameters.AddCascadingValue(httpContext));

        Assert.Contains("Something went wrong", cut.Markup);
        Assert.Contains("role=\"alert\"", cut.Markup);
        Assert.Contains("trace-portal-42", cut.Markup);
        Assert.DoesNotContain("Exception", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("StackTrace", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CloudLayout_DoesNotRenderSiteAssistant()
    {
        ComponentFactories.AddStub<OpenConnect.Docs.Components.ThemeToggle>();
        var cut = Render<CloudLayout>(parameters => parameters
            .Add(x => x.Body, (RenderFragment)(builder => builder.AddContent(0, "Cloud body"))));

        Assert.Contains("Cloud body", cut.Markup);
        Assert.Contains("/cloud/sign-out", cut.Markup);
        Assert.DoesNotContain("SiteAssistant", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("assistant-trigger", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Ask OpenConnect", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TenantHostPrivacyReports_LoadingStateUsesEllipsis()
    {
        var pending = new TaskCompletionSource<CloudHostPrivacyObservationPage?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Services.AddSingleton<ICloudHostPrivacyObservationReader>(new PendingHostPrivacyReader(pending.Task));
        Services.AddSingleton<AuthenticationStateProvider>(new StubAuthProvider("user-1", "https://issuer.example"));
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OpenConnect:Cloud:Identity:Enabled"] = "true",
            ["OpenConnect:Cloud:Identity:Authority"] = "https://issuer.example",
        }).Build());

        var cut = Render<TenantHostPrivacyReports>(parameters => parameters.Add(x => x.TenantId, Guid.NewGuid()));

        Assert.Contains("Loading host reports…", cut.Markup);
        Assert.DoesNotContain("Loading host reports?", cut.Markup);
    }

    [Fact]
    public void TenantApplications_ClearsStateAndIgnoresStaleAsyncLoadOnTenantSwitch()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var slowTenantA = new TaskCompletionSource<IReadOnlyList<CloudApplicationSummary>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new SwitchableTenantStore(
            tenants:
            [
                new CloudTenantSummary(tenantA, "Tenant A", "Owner", DateTimeOffset.UtcNow),
                new CloudTenantSummary(tenantB, "Tenant B", "Developer", DateTimeOffset.UtcNow),
            ],
            applications: new Dictionary<Guid, Func<Task<IReadOnlyList<CloudApplicationSummary>?>>>
            {
                [tenantA] = () => slowTenantA.Task,
                [tenantB] = () => Task.FromResult<IReadOnlyList<CloudApplicationSummary>?>([
                    new CloudApplicationSummary(Guid.NewGuid(), tenantB, "Beta App", "Staging", DateTimeOffset.UtcNow),
                ]),
            });

        Services.AddSingleton<ICloudTenantStore>(store);
        Services.AddScoped<CloudControlPlaneService>();
        Services.AddSingleton<AuthenticationStateProvider>(new StubAuthProvider("user-1", "https://issuer.example"));
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OpenConnect:Cloud:Identity:Authority"] = "https://issuer.example",
        }).Build());

        var cut = Render<TenantApplications>(p => p.Add(x => x.TenantId, tenantA));
        Assert.Contains("Loading applications…", cut.Markup);

        // Switch to Tenant B before Tenant A resolves.
        cut.Render(p => p.Add(x => x.TenantId, tenantB));
        cut.WaitForAssertion(() => Assert.Contains("Beta App", cut.Markup));
        Assert.Contains("Organization role: Developer", cut.Markup);
        Assert.DoesNotContain("Register application", cut.Markup);

        // Complete the stale Tenant A load; it must not overwrite Tenant B.
        slowTenantA.SetResult([new CloudApplicationSummary(Guid.NewGuid(), tenantA, "Alpha App", "Production", DateTimeOffset.UtcNow)]);
        Assert.Contains("Beta App", cut.Markup);
        Assert.DoesNotContain("Alpha App", cut.Markup);
        Assert.DoesNotContain("Organization role: Owner", cut.Markup);

        // Switch to an unauthorized tenant; previous Tenant B applications must be cleared.
        cut.Render(p => p.Add(x => x.TenantId, Guid.NewGuid()));
        cut.WaitForAssertion(() => Assert.Contains("The organization was not found or you do not have access.", cut.Markup));
        Assert.DoesNotContain("Beta App", cut.Markup);
    }

    [Fact]
    public void TenantServices_ClearsStateAndIgnoresStaleAsyncLoadOnTenantSwitch()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var slowTenantA = new TaskCompletionSource<CloudCapabilitySnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var capA = new CloudCapabilityDefinition
        {
            Id = "cap.alpha", Name = "Alpha Capability", Description = "Alpha desc",
            ContractVersion = "1", BillingUnit = "request", PlatformAvailability = "available",
            Dependencies = "[]", Modalities = "[]", AllowedRegions = "[]",
        };
        var capB = new CloudCapabilityDefinition
        {
            Id = "cap.beta", Name = "Beta Capability", Description = "Beta desc",
            ContractVersion = "1", BillingUnit = "request", PlatformAvailability = "available",
            Dependencies = "[]", Modalities = "[]", AllowedRegions = "[]",
        };

        var tenantStore = new SwitchableTenantStore(
            tenants:
            [
                new CloudTenantSummary(tenantA, "Tenant A", "Owner", DateTimeOffset.UtcNow),
                new CloudTenantSummary(tenantB, "Tenant B", "Developer", DateTimeOffset.UtcNow),
            ],
            applications: new Dictionary<Guid, Func<Task<IReadOnlyList<CloudApplicationSummary>?>>>());

        var capabilityReader = new SwitchableCapabilityReader(new Dictionary<Guid, Func<Task<CloudCapabilitySnapshot?>>>
        {
            [tenantA] = () => slowTenantA.Task,
            [tenantB] = () => Task.FromResult<CloudCapabilitySnapshot?>(new CloudCapabilitySnapshot(
                [capB],
                new Dictionary<string, CloudTenantEntitlement>(),
                new Dictionary<string, CloudTenantCapabilitySelection>(),
                null)),
        });

        Services.AddSingleton<ICloudTenantStore>(tenantStore);
        Services.AddScoped<CloudControlPlaneService>();
        Services.AddSingleton<ICloudCapabilityReader>(capabilityReader);
        Services.AddSingleton<ICloudCapabilitySelectionStore>(new NoopCapabilitySelectionStore());
        Services.AddSingleton(TimeProvider.System);
        Services.AddScoped<CloudCapabilityService>();
        Services.AddSingleton<AuthenticationStateProvider>(new StubAuthProvider("user-1", "https://issuer.example"));
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OpenConnect:Cloud:Identity:Authority"] = "https://issuer.example",
        }).Build());

        var cut = Render<TenantServices>(p => p.Add(x => x.TenantId, tenantA));
        Assert.Contains("Loading service catalog…", cut.Markup);

        cut.Render(p => p.Add(x => x.TenantId, tenantB));
        cut.WaitForAssertion(() => Assert.Contains("Beta Capability", cut.Markup));
        Assert.DoesNotContain("Enable for organization", cut.Markup);

        // Complete the stale Tenant A load; it must not overwrite Tenant B.
        slowTenantA.SetResult(new CloudCapabilitySnapshot(
            [capA],
            new Dictionary<string, CloudTenantEntitlement>(),
            new Dictionary<string, CloudTenantCapabilitySelection>(),
            null));
        Assert.Contains("Beta Capability", cut.Markup);
        Assert.DoesNotContain("Alpha Capability", cut.Markup);

        // Switch to an unauthorized tenant; previous Tenant B capabilities must be cleared.
        cut.Render(p => p.Add(x => x.TenantId, Guid.NewGuid()));
        cut.WaitForAssertion(() => Assert.Contains("The organization was not found or you do not have access.", cut.Markup));
        Assert.DoesNotContain("Beta Capability", cut.Markup);
    }

    [Fact]
    public void ApplicationCredentials_RendersEncodedOnboardingSnippetsWithPlaceholders()
    {
        var tenantId = Guid.NewGuid();
        var appId = Guid.NewGuid();
        var tenantStore = new SwitchableTenantStore(
            tenants: [new CloudTenantSummary(tenantId, "Org", "Owner", DateTimeOffset.UtcNow)],
            applications: new Dictionary<Guid, Func<Task<IReadOnlyList<CloudApplicationSummary>?>>>());

        var capabilityReader = new SwitchableCapabilityReader(
            new Dictionary<Guid, Func<Task<CloudCapabilitySnapshot?>>>(),
            applicationSnapshots: new Dictionary<(Guid, Guid), CloudCapabilitySnapshot>
            {
                [(tenantId, appId)] = new CloudCapabilitySnapshot(
                    [],
                    new Dictionary<string, CloudTenantEntitlement>(),
                    new Dictionary<string, CloudTenantCapabilitySelection>(),
                    new Dictionary<string, CloudApplicationCapabilitySelection>()),
            });

        Services.AddSingleton<ICloudTenantStore>(tenantStore);
        Services.AddScoped<CloudControlPlaneService>();
        Services.AddSingleton<ICloudCapabilityReader>(capabilityReader);
        Services.AddSingleton<ICloudCapabilitySelectionStore>(new NoopCapabilitySelectionStore());
        Services.AddSingleton<ICloudCredentialStore>(new StubCredentialStore(tenantId, appId));
        Services.AddSingleton(TimeProvider.System);
        Services.AddScoped<CloudCapabilityService>();
        Services.AddScoped<CloudCredentialService>();
        Services.AddSingleton<AuthenticationStateProvider>(new StubAuthProvider("user-1", "https://issuer.example"));
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OpenConnect:Cloud:Identity:Authority"] = "https://issuer.example",
        }).Build());

        var cut = Render<ApplicationCredentials>(p => p.Add(x => x.TenantId, tenantId).Add(x => x.ApplicationId, appId));
        cut.WaitForAssertion(() => Assert.Contains("Connect your application", cut.Markup));

        Assert.Contains("<h3>.NET</h3>", cut.Markup);
        Assert.Contains("<h3>REST</h3>", cut.Markup);
        Assert.Contains("<h3>TypeScript</h3>", cut.Markup);
        Assert.Contains("&lt;YOUR_API_KEY&gt;", cut.Markup);
        Assert.DoesNotContain("<YOUR_API_KEY>", cut.Markup);
        Assert.DoesNotContain("oc_dev_", cut.Markup);
        Assert.DoesNotContain("oc_live_", cut.Markup);
    }

    private sealed class StubAuthProvider(string subjectId, string issuer) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, subjectId, ClaimValueTypes.String, issuer)], "test"))));
    }

    private sealed class PendingHostPrivacyReader(Task<CloudHostPrivacyObservationPage?> task) : ICloudHostPrivacyObservationReader
    {
        public Task<CloudHostPrivacyObservationPage?> GetTenantAsync(CloudSubject subject, Guid tenantId, CloudPrivacyObservationCursor? before, int limit, CancellationToken cancellationToken) => task;
    }

    private sealed class SwitchableTenantStore(
        IReadOnlyList<CloudTenantSummary> tenants,
        IDictionary<Guid, Func<Task<IReadOnlyList<CloudApplicationSummary>?>>> applications) : ICloudTenantStore
    {
        public Task<IReadOnlyList<CloudTenantSummary>> GetTenantsAsync(CloudSubject subject, CancellationToken cancellationToken) =>
            Task.FromResult(tenants);

        public Task<CloudTenantSummary> CreateTenantAsync(CloudSubject subject, string name, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<CloudMemberSummary>?> GetMembersAsync(CloudSubject subject, Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CloudMemberSummary>?>(null);

        public Task<CloudMemberChangeResult> ChangeMemberAsync(CloudSubject actor, Guid tenantId, CloudSubject target, string? role, int expectedRevision, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<CloudApplicationSummary>?> GetApplicationsAsync(CloudSubject subject, Guid tenantId, CancellationToken cancellationToken) =>
            applications.TryGetValue(tenantId, out var loader)
                ? loader()
                : Task.FromResult<IReadOnlyList<CloudApplicationSummary>?>(null);

        public Task<(CloudApplicationSummary? Application, bool Forbidden)> CreateApplicationAsync(CloudSubject subject, Guid tenantId, string name, string environment, CancellationToken cancellationToken) =>
            Task.FromResult<(CloudApplicationSummary?, bool)>((null, false));
    }

    private sealed class SwitchableCapabilityReader(
        IDictionary<Guid, Func<Task<CloudCapabilitySnapshot?>>> tenantSnapshots,
        IDictionary<(Guid, Guid), CloudCapabilitySnapshot>? applicationSnapshots = null) : ICloudCapabilityReader
    {
        public Task<IReadOnlyList<CloudCapabilityDefinition>> GetCatalogAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CloudCapabilityDefinition>>([]);

        public Task<CloudCapabilitySnapshot?> GetTenantAsync(CloudSubject subject, Guid tenantId, CancellationToken cancellationToken) =>
            tenantSnapshots.TryGetValue(tenantId, out var loader)
                ? loader()
                : Task.FromResult<CloudCapabilitySnapshot?>(null);

        public Task<CloudCapabilitySnapshot?> GetApplicationAsync(CloudSubject subject, Guid tenantId, Guid applicationId, CancellationToken cancellationToken) =>
            Task.FromResult(applicationSnapshots is not null && applicationSnapshots.TryGetValue((tenantId, applicationId), out var snapshot) ? snapshot : null);
    }

    private sealed class NoopCapabilitySelectionStore : ICloudCapabilitySelectionStore
    {
        public Task<CloudCapabilitySelectionResult> ChangeAsync(
            CloudSubject actor, Guid tenantId, Guid? applicationId, string capabilityId, bool enabled,
            Func<string, bool> authorize, Func<CloudCapabilitySnapshot, CloudCapabilitySelectionResult> evaluate,
            CancellationToken cancellationToken) =>
            Task.FromResult(new CloudCapabilitySelectionResult("not_found"));
    }

    private sealed class StubCredentialStore(Guid tenantId, Guid appId) : ICloudCredentialStore
    {
        public Task<ICloudCredentialSession> OpenAsync(bool transactional, CancellationToken cancellationToken) =>
            Task.FromResult<ICloudCredentialSession>(new StubSession(tenantId, appId));

        private sealed class StubSession(Guid tenantId, Guid appId) : ICloudCredentialSession
        {
            public Task<string?> FindRoleAsync(CloudSubject subject, Guid id, CancellationToken cancellationToken) =>
                Task.FromResult<string?>(id == tenantId ? "Owner" : null);
            public Task<CloudCredentialApplication?> FindApplicationAsync(Guid tid, Guid aid, CancellationToken cancellationToken) =>
                Task.FromResult<CloudCredentialApplication?>(tid == tenantId && aid == appId ? new CloudCredentialApplication(appId, "Development") : null);
            public Task<CloudCredential?> FindAsync(Guid credentialId, Guid? tid, Guid? aid, CancellationToken cancellationToken) =>
                Task.FromResult<CloudCredential?>(null);
            public Task<IReadOnlyList<CloudCredential>> ListAsync(Guid tid, Guid aid, CancellationToken cancellationToken) =>
                Task.FromResult<IReadOnlyList<CloudCredential>>([]);
            public Task<CloudCapabilitySnapshot?> GetCapabilitiesAsync(CloudSubject subject, Guid tid, Guid aid, CancellationToken cancellationToken) =>
                Task.FromResult<CloudCapabilitySnapshot?>(null);
            public Task<bool> TouchIfActiveAsync(Guid credentialId, DateTimeOffset now, CancellationToken cancellationToken) =>
                Task.FromResult(false);
            public void Add(CloudCredential credential) { }
            public void AddAudit(CloudSubject subject, Guid tid, string role, string action, Guid credentialId, DateTimeOffset now) { }
            public Task CommitAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
