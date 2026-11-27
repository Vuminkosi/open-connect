using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenConnect.Docs.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class CloudControlPlaneEndpointTests
{
    private const string TestIssuer = "https://issuer.example";

    [Fact]
    public async Task CreateApplication_Returns403ForbiddenForMemberAnd404NotFoundForNonMemberWithTraceId()
    {
        var forbiddenTenantId = Guid.NewGuid();
        var outsiderTenantId = Guid.NewGuid();
        var store = new StubTenantStore((subject, tenantId, name, env) =>
            tenantId == forbiddenTenantId
                ? (null, true)
                : (null, false));

        await using var app = CreateApp(store, databaseConfigured: true);
        await app.StartAsync();
        using var handler = new HttpClientHandler { UseCookies = true, CookieContainer = new CookieContainer() };
        using var client = CreateClient(app, handler);
        var antiforgeryToken = await GetAntiforgeryTokenAsync(client);

        using var forbiddenRequest = new HttpRequestMessage(HttpMethod.Post, $"/cloud/api/v1/tenants/{forbiddenTenantId:D}/applications")
        {
            Content = JsonContent.Create(new CreateCloudApplicationRequest("My App", "Development")),
        };
        forbiddenRequest.Headers.Add("RequestVerificationToken", antiforgeryToken);

        var forbiddenResponse = await client.SendAsync(forbiddenRequest);
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenResponse.StatusCode);
        Assert.Equal("application/problem+json", forbiddenResponse.Content.Headers.ContentType?.MediaType);
        using (var doc = JsonDocument.Parse(await forbiddenResponse.Content.ReadAsStringAsync()))
        {
            Assert.Equal("forbidden", doc.RootElement.GetProperty("code").GetString());
            Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("traceId").GetString()));
        }

        using var notFoundRequest = new HttpRequestMessage(HttpMethod.Post, $"/cloud/api/v1/tenants/{outsiderTenantId:D}/applications")
        {
            Content = JsonContent.Create(new CreateCloudApplicationRequest("My App", "Development")),
        };
        notFoundRequest.Headers.Add("RequestVerificationToken", antiforgeryToken);

        var notFoundResponse = await client.SendAsync(notFoundRequest);
        Assert.Equal(HttpStatusCode.NotFound, notFoundResponse.StatusCode);
        Assert.Equal("application/problem+json", notFoundResponse.Content.Headers.ContentType?.MediaType);
        using (var doc = JsonDocument.Parse(await notFoundResponse.Content.ReadAsStringAsync()))
        {
            Assert.Equal("not_found", doc.RootElement.GetProperty("code").GetString());
            Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("traceId").GetString()));
        }
    }

    [Fact]
    public async Task MissingAntiforgeryToken_ReturnsTypedProblemDetailsWithTraceId()
    {
        var store = new StubTenantStore((_, _, _, _) => (null, false));
        await using var app = CreateApp(store, databaseConfigured: true);
        await app.StartAsync();
        using var client = CreateClient(app);

        var response = await client.PostAsJsonAsync(
            $"/cloud/api/v1/tenants/{Guid.NewGuid():D}/applications",
            new CreateCloudApplicationRequest("App", "Development"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("invalid_antiforgery", doc.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("traceId").GetString()));
    }

    [Fact]
    public async Task ValidationAndPreconditionErrors_ReturnTypedProblemDetailsWithTraceId()
    {
        var store = new StubTenantStore((_, _, _, _) => (null, false));
        await using var app = CreateApp(store, databaseConfigured: true);
        await app.StartAsync();
        using var handler = new HttpClientHandler { UseCookies = true, CookieContainer = new CookieContainer() };
        using var client = CreateClient(app, handler);
        var antiforgeryToken = await GetAntiforgeryTokenAsync(client);

        using var invalidTenantRequest = new HttpRequestMessage(HttpMethod.Post, "/cloud/api/v1/tenants")
        {
            Content = JsonContent.Create(new CreateCloudTenantRequest("   ")),
        };
        invalidTenantRequest.Headers.Add("RequestVerificationToken", antiforgeryToken);
        var validationResponse = await client.SendAsync(invalidTenantRequest);
        Assert.Equal(HttpStatusCode.BadRequest, validationResponse.StatusCode);
        Assert.Equal("application/problem+json", validationResponse.Content.Headers.ContentType?.MediaType);
        using (var doc = JsonDocument.Parse(await validationResponse.Content.ReadAsStringAsync()))
        {
            Assert.Equal("invalid_request", doc.RootElement.GetProperty("code").GetString());
            Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("traceId").GetString()));
        }

        using var missingIfMatchRequest = new HttpRequestMessage(HttpMethod.Put, $"/cloud/api/v1/tenants/{Guid.NewGuid():D}/capabilities/text.classify")
        {
            Content = JsonContent.Create(new CloudCapabilitySelectionRequest(true)),
        };
        missingIfMatchRequest.Headers.Add("RequestVerificationToken", antiforgeryToken);
        var preconditionResponse = await client.SendAsync(missingIfMatchRequest);
        Assert.Equal(HttpStatusCode.PreconditionRequired, preconditionResponse.StatusCode);
        Assert.Equal("application/problem+json", preconditionResponse.Content.Headers.ContentType?.MediaType);
        using (var doc = JsonDocument.Parse(await preconditionResponse.Content.ReadAsStringAsync()))
        {
            Assert.Equal("precondition_required", doc.RootElement.GetProperty("code").GetString());
            Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("traceId").GetString()));
        }
    }

    [Fact]
    public async Task UnconfiguredControlPlane_ReturnsProblemDetailsWithTraceId()
    {
        var store = new StubTenantStore((_, _, _, _) => (null, false));
        await using var app = CreateApp(store, databaseConfigured: false);
        await app.StartAsync();
        using var client = CreateClient(app);

        var response = await client.GetAsync("/cloud/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("service_unavailable", doc.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("traceId").GetString()));
    }

    private static WebApplication CreateApp(ICloudTenantStore tenantStore, bool databaseConfigured)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthentication("Test")
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
        builder.Services.AddAuthorizationBuilder()
            .AddPolicy("CloudApi", policy => policy.RequireAuthenticatedUser());
        builder.Services.AddAntiforgery();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<CloudOperatorAccess>();
        builder.Services.AddDbContextFactory<CloudControlPlaneDbContext>(options => options.UseNpgsql("Host=127.0.0.1;Database=unused"));
        builder.Services.AddScoped<CloudReadinessService>();
        builder.Services.AddSingleton(tenantStore);
        builder.Services.AddScoped<CloudControlPlaneService>();
        builder.Services.AddSingleton<ICloudCapabilityReader>(new EmptyCapabilityReader());
        builder.Services.AddSingleton<ICloudCapabilitySelectionStore>(new EmptyCapabilitySelectionStore());
        builder.Services.AddScoped<CloudCapabilityService>();
        builder.Services.AddScoped<ICloudInvitationStore, EfCloudInvitationStore>();
        builder.Services.AddScoped<CloudInvitationService>();
        builder.Services.AddScoped<ICloudCredentialStore, EfCloudCredentialStore>();
        builder.Services.AddScoped<CloudCredentialService>();
        builder.Services.AddScoped<ICloudQuotaReader, EfCloudQuotaReader>();
        builder.Services.AddScoped<CloudQuotaQueryService>();
        builder.Services.AddScoped<ICloudUsageReader, EfCloudUsageReader>();
        builder.Services.AddScoped<CloudUsageQueryService>();
        builder.Services.AddScoped<ICloudUsageSummaryReader, EfCloudUsageSummaryReader>();
        builder.Services.AddScoped<CloudUsageSummaryService>();
        builder.Services.AddScoped<ICloudRegionPolicyStore, EfCloudRegionPolicyStore>();
        builder.Services.AddScoped<CloudRegionPolicyService>();
        builder.Services.AddScoped<ICloudInferenceModelPolicyStore, EfCloudInferenceModelPolicyStore>();
        builder.Services.AddScoped<CloudInferenceModelPolicyService>();
        builder.Services.AddScoped<ICloudCatalogRegionsStore, EfCloudCatalogRegionsStore>();
        builder.Services.AddScoped<CloudCatalogRegionsService>();
        builder.Services.AddScoped<CloudOperatorCatalogRegionsService>();
        builder.Services.AddScoped<ICloudCapabilityAvailabilityStore, EfCloudCapabilityAvailabilityStore>();
        builder.Services.AddScoped<CloudCapabilityAvailabilityService>();
        builder.Services.AddScoped<CloudOperatorCapabilityAvailabilityService>();
        builder.Services.AddScoped<ICloudQuotaAdministrationStore, EfCloudQuotaAdministrationStore>();
        builder.Services.AddScoped<CloudQuotaAdministration>();
        builder.Services.AddScoped<ICloudEntitlementAdministrationStore, EfCloudEntitlementAdministrationStore>();
        builder.Services.AddScoped<CloudEntitlementAdministration>();
        builder.Services.AddScoped<CloudOperatorEntitlementAdministration>();
        builder.Services.AddScoped<ICloudPrivacyObservationReader, EfCloudPrivacyObservationReader>();
        builder.Services.AddScoped<ICloudPrivacyCaseReader, EfCloudPrivacyCaseReader>();
        builder.Services.AddScoped<ICloudHostPrivacyObservationReader, CloudHostPrivacyObservationReader>();
        builder.Services.AddScoped<ICloudHostPrivacyCaseReader, CloudHostPrivacyObservationReader>();
        builder.Services.AddScoped<ICloudPrivacyStatisticsReader, EfCloudPrivacyStatisticsReader>();
        builder.Services.AddScoped<CloudPrivacyObservationService>();
        builder.Services.AddScoped<ICloudDeploymentStore, EfCloudDeploymentStore>();
        builder.Services.AddScoped<CloudDeploymentRegistry>();
        builder.Services.AddScoped<CloudOperatorDeploymentService>();
        builder.Services.AddCloudTranslationFeature();

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseAntiforgery();
        app.MapCloudControlPlaneEndpoints(TestIssuer, databaseConfigured);
        return app;
    }

    private static HttpClient CreateClient(WebApplication app, HttpMessageHandler? handler = null)
    {
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return handler is null
            ? new HttpClient { BaseAddress = new Uri(address) }
            : new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri(address) };
    }

    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client)
    {
        var response = await client.GetAsync("/cloud/api/v1/antiforgery");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("requestToken").GetString()!;
    }

    private sealed class TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "user-1", ClaimValueTypes.String, TestIssuer)],
                "Test");
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), "Test")));
        }
    }

    private sealed class StubTenantStore(
        Func<CloudSubject, Guid, string, string, (CloudApplicationSummary? Application, bool Forbidden)> onCreateApp) : ICloudTenantStore
    {
        public Task<IReadOnlyList<CloudTenantSummary>> GetTenantsAsync(CloudSubject subject, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CloudTenantSummary>>([]);
        public Task<CloudTenantSummary> CreateTenantAsync(CloudSubject subject, string name, CancellationToken cancellationToken) =>
            Task.FromResult(new CloudTenantSummary(Guid.NewGuid(), name, "Owner", DateTimeOffset.UtcNow));
        public Task<IReadOnlyList<CloudMemberSummary>?> GetMembersAsync(CloudSubject subject, Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CloudMemberSummary>?>(null);
        public Task<CloudMemberChangeResult> ChangeMemberAsync(CloudSubject actor, Guid tenantId, CloudSubject target, string? role, int expectedRevision, CancellationToken cancellationToken) =>
            Task.FromResult(new CloudMemberChangeResult("not_found"));
        public Task<IReadOnlyList<CloudApplicationSummary>?> GetApplicationsAsync(CloudSubject subject, Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CloudApplicationSummary>?>(null);
        public Task<(CloudApplicationSummary? Application, bool Forbidden)> CreateApplicationAsync(CloudSubject subject, Guid tenantId, string name, string environment, CancellationToken cancellationToken) =>
            Task.FromResult(onCreateApp(subject, tenantId, name, environment));
    }

    private sealed class EmptyCapabilityReader : ICloudCapabilityReader
    {
        public Task<IReadOnlyList<CloudCapabilityDefinition>> GetCatalogAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CloudCapabilityDefinition>>([]);
        public Task<CloudCapabilitySnapshot?> GetTenantAsync(CloudSubject subject, Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<CloudCapabilitySnapshot?>(null);
        public Task<CloudCapabilitySnapshot?> GetApplicationAsync(CloudSubject subject, Guid tenantId, Guid applicationId, CancellationToken cancellationToken) =>
            Task.FromResult<CloudCapabilitySnapshot?>(null);
    }

    private sealed class EmptyCapabilitySelectionStore : ICloudCapabilitySelectionStore
    {
        public Task<CloudCapabilitySelectionResult> ChangeAsync(
            CloudSubject actor, Guid tenantId, Guid? applicationId, string capabilityId, bool enabled,
            Func<string, bool> authorize, Func<CloudCapabilitySnapshot, CloudCapabilitySelectionResult> evaluate,
            CancellationToken cancellationToken) =>
            Task.FromResult(new CloudCapabilitySelectionResult("not_found"));
    }
}
