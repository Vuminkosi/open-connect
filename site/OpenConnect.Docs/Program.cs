using OpenConnect.Client;
using OpenConnect.Components.Blazor;
using OpenConnect.Core;
using OpenConnect.Docs.Services;
using OpenConnect.Docs.Cloud;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using OpenConnect.Privacy.Rules;

var builder = WebApplication.CreateBuilder(args);
var gatewayTrustSection = builder.Configuration.GetSection("OpenConnect:Cloud:GatewayTrust");
CloudGatewayClientTrust? gatewayTrust = null;
if (gatewayTrustSection.GetValue<bool>("Enabled"))
{
    gatewayTrust = new CloudGatewayClientTrust(gatewayTrustSection.GetSection("ClientCertificateSha256").Get<string[]>() ?? []);
}
CloudWorkerClientTrust? workerTrust = null;
var workerTrustSection = builder.Configuration.GetSection("OpenConnect:Cloud:WorkerTrust");
if (workerTrustSection.GetValue<bool>("Enabled"))
    workerTrust = new CloudWorkerClientTrust(workerTrustSection.GetSection("Certificates").Get<CloudWorkerCertificateBinding[]>() ?? [], gatewayTrust);
if (gatewayTrust is not null || workerTrust is not null)
    builder.WebHost.ConfigureKestrel(server => server.ConfigureHttpsDefaults(https =>
    {
        https.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
        https.ClientCertificateValidation = (certificate, _, _) => gatewayTrust?.IsTrusted(certificate, DateTimeOffset.UtcNow) == true ||
            workerTrust?.ResolveDeployment(certificate, DateTimeOffset.UtcNow) is not null;
    }));

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "__Host-OpenConnect.Cloud";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.Path = "/";
        options.LoginPath = "/cloud/sign-in";
        options.SlidingExpiration = true;
        options.Events.OnValidatePrincipal = async context =>
        {
            if (!CloudIdentity.IsEnabled(context.Principal, context.HttpContext.RequestServices
                .GetRequiredService<IConfiguration>().GetSection("OpenConnect:Cloud:Identity")))
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            }
        };
        options.Events.OnRedirectToLogin = context =>
        {
            if (context.Request.Path.StartsWithSegments("/cloud/api/v1"))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            }
            context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            if (context.Request.Path.StartsWithSegments("/cloud/api/v1"))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            }
            context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization(options => options.AddPolicy("CloudApi", policy =>
{
    policy.AddAuthenticationSchemes(CookieAuthenticationDefaults.AuthenticationScheme);
    policy.RequireAuthenticatedUser();
}));
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<CloudOperatorAccess>();

var cloudOidc = builder.Configuration.GetSection("OpenConnect:Cloud:Identity");
var cloudIdentityEnabled = cloudOidc.GetValue<bool>("Enabled");
var googleIdentityEnabled = cloudOidc.GetValue<bool>("Google:Enabled");
var cloudDatabaseConnection = builder.Configuration.GetConnectionString("CloudControlPlane");
if (!string.IsNullOrWhiteSpace(cloudDatabaseConnection))
{
    builder.Services.AddDbContextFactory<CloudControlPlaneDbContext>(options => options.UseNpgsql(cloudDatabaseConnection));
    builder.Services.AddScoped<ICloudTenantStore, EfCloudTenantStore>();
    builder.Services.AddScoped<ICloudInvitationStore, EfCloudInvitationStore>();
    builder.Services.AddScoped<CloudInvitationService>();
    builder.Services.AddScoped<CloudControlPlaneService>();
    builder.Services.AddScoped<ICloudCapabilityReader, EfCloudCapabilityReader>();
    builder.Services.AddScoped<ICloudCapabilitySelectionStore, EfCloudCapabilitySelectionStore>();
    builder.Services.AddScoped<CloudCapabilityService>();
    builder.Services.AddScoped<ICloudCredentialStore, EfCloudCredentialStore>();
    builder.Services.AddScoped<CloudCredentialService>();
    builder.Services.AddScoped<ICloudMachineCapabilityReader, EfCloudMachineCapabilityReader>();
    builder.Services.AddScoped<CloudMachineAuthorizationService>();
    builder.Services.AddSingleton(new CloudClassificationPrivacyService(new RulesSecurityDetector()));
    builder.Services.AddSingleton(new CloudInferencePrivacyService(new RulesSecurityDetector()));
    builder.Services.AddCloudVoiceFeature();
    builder.Services.AddCloudTranslationFeature();
    builder.Services.AddScoped<ICloudInferencePrivacyEvidenceStore, EfCloudInferencePrivacyEvidenceStore>();
    builder.Services.AddSingleton(new CloudInferenceOutputPrivacyService(new RulesSecurityDetector()));
    builder.Services.AddScoped<ICloudInferenceOutputEvidenceStore, EfCloudInferenceOutputEvidenceStore>();
    builder.Services.AddScoped<ICloudInferenceAccessStore, EfCloudInferenceAccessStore>();
    builder.Services.AddSingleton(new CloudLiveUIPrivacyService(new RulesSecurityDetector()));
    builder.Services.AddScoped<ICloudLiveUIPrivacyEvidenceStore, EfCloudLiveUIPrivacyEvidenceStore>();
    builder.Services.AddScoped<ICloudLiveUIClaimStore, EfCloudLiveUIClaimStore>();
    builder.Services.AddScoped<CloudLiveUIClaimService>();
    builder.Services.AddScoped<ICloudLiveUIConsumptionStore, EfCloudLiveUIConsumptionStore>();
    builder.Services.AddScoped<CloudLiveUIConsumptionService>();
    builder.Services.AddScoped<ICloudLiveUISettlementStore, EfCloudLiveUISettlementStore>();
    builder.Services.AddScoped<CloudLiveUISettlement>();
    builder.Services.AddScoped<ICloudLiveUIDualQuotaStore, EfCloudLiveUIDualQuotaStore>();
    builder.Services.AddScoped<CloudLiveUIDualQuotaService>();
    builder.Services.AddScoped<ICloudLiveUIMeteredConsumptionStore, EfCloudLiveUIConsumptionStore>();
    builder.Services.AddScoped<CloudLiveUIMeteredConsumption>();
    builder.Services.AddScoped<ICloudLiveUIDualSettlementStore, EfCloudLiveUIDualSettlementStore>();
    builder.Services.AddScoped<CloudLiveUIDualSettlement>();
    builder.Services.AddSingleton(new CloudLiveUIOutputPrivacyService(new RulesSecurityDetector()));
    builder.Services.AddScoped<ICloudLiveUIOutputEvidenceStore, EfCloudLiveUIOutputEvidenceStore>();
    builder.Services.AddScoped<ICloudLiveUIAccessStore, EfCloudLiveUIAccessStore>();
    builder.Services.AddScoped<ICloudPrivacyEvidenceStore, EfCloudPrivacyEvidenceStore>();
    builder.Services.AddScoped<ICloudPrivacyObservationReader, EfCloudPrivacyObservationReader>();
    builder.Services.AddScoped<CloudPrivacyObservationService>();
    builder.Services.AddScoped<ICloudQuotaStore, EfCloudQuotaStore>();
    builder.Services.AddScoped<ICloudQuotaReader, EfCloudQuotaReader>();
    builder.Services.AddScoped<CloudQuotaQueryService>();
    builder.Services.AddScoped<CloudQuotaService>();
    builder.Services.AddScoped<ICloudUsageReader, EfCloudUsageReader>();
    builder.Services.AddScoped<CloudUsageQueryService>();
    builder.Services.AddScoped<ICloudUsageSummaryReader, EfCloudUsageSummaryReader>();
    builder.Services.AddScoped<CloudUsageSummaryService>();
    builder.Services.AddScoped<ICloudQuotaRecoveryStore, EfCloudQuotaRecoveryStore>();
    builder.Services.AddScoped<CloudQuotaRecovery>();
    builder.Services.AddScoped<ICloudLiveUIUnclaimedRecoveryStore, EfCloudLiveUIUnclaimedRecoveryStore>();
    builder.Services.AddScoped<ICloudLiveUIUnconsumedRecoveryStore, EfCloudLiveUIUnconsumedRecoveryStore>();
    builder.Services.AddScoped<CloudLiveUIObservedRecovery>();
    builder.Services.AddScoped<ICloudLiveUIReconciliationReader, EfCloudLiveUIReconciliationReader>();
    builder.Services.AddScoped<CloudLiveUIReconciliation>();
    builder.Services.AddScoped<ICloudUnconsumedClaimRecoveryStore, EfCloudUnconsumedClaimRecoveryStore>();
    builder.Services.AddScoped<CloudUnconsumedClaimRecovery>();
    if (builder.Configuration.GetValue<bool>("OpenConnect:Cloud:QuotaRecovery:UnconsumedClaimsEnabled"))
    {
        builder.Services.AddHostedService<CloudUnconsumedClaimRecoveryWorker>();
        builder.Services.AddHostedService<CloudLiveUIUnconsumedRecoveryWorker>();
    }
    if (builder.Configuration.GetValue<bool>("OpenConnect:Cloud:QuotaRecovery:Enabled"))
    {
        builder.Services.AddHostedService<CloudQuotaRecoveryWorker>();
        builder.Services.AddHostedService<CloudLiveUIUnclaimedRecoveryWorker>();
        builder.Services.AddHostedService<CloudLiveUIObservedRecoveryWorker>();
    }
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
    builder.Services.AddScoped<ICloudExecutionClaimStore, EfCloudExecutionClaimStore>();
    builder.Services.AddScoped<CloudExecutionClaimService>();
    builder.Services.AddScoped<ICloudExecutionConsumptionStore, EfCloudExecutionConsumptionStore>();
    builder.Services.AddScoped<CloudExecutionConsumptionService>();
    builder.Services.AddScoped<ICloudInferenceConsumptionStore, EfCloudInferenceConsumptionStore>();
    builder.Services.AddScoped<CloudInferenceConsumptionService>();
    builder.Services.AddScoped<ICloudInferenceTokenSettlementStore, EfCloudInferenceTokenSettlementStore>();
    builder.Services.AddScoped<CloudInferenceTokenSettlement>();
    builder.Services.AddScoped<ICloudWorkerReadinessStore, EfCloudWorkerReadinessStore>();
    builder.Services.AddScoped<CloudWorkerReadiness>();
    builder.Services.AddScoped<ICloudExecutionCompletionStore, EfCloudExecutionCompletionStore>();
    builder.Services.AddScoped<CloudExecutionCompletionService>();
    builder.Services.AddScoped<ICloudQuotaAdministrationStore, EfCloudQuotaAdministrationStore>();
    builder.Services.AddScoped<CloudQuotaAdministration>();
    builder.Services.AddScoped<ICloudEntitlementAdministrationStore, EfCloudEntitlementAdministrationStore>();
    builder.Services.AddScoped<CloudEntitlementAdministration>();
    builder.Services.AddScoped<CloudOperatorEntitlementAdministration>();
    builder.Services.AddScoped<ICloudDeploymentStore, EfCloudDeploymentStore>();
    builder.Services.AddScoped<CloudDeploymentRegistry>();
    builder.Services.AddScoped<ICloudClassificationDeploymentReader, EfCloudClassificationDeploymentReader>();
    builder.Services.AddScoped<CloudClassificationDeploymentService>();
    builder.Services.AddScoped<CloudOperatorDeploymentService>();
    builder.Services.AddScoped<CloudReadinessService>();
}
builder.Services.AddCloudIdentityProviders(cloudOidc);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddSingleton<DocumentationContentStore>();
builder.Services.AddSingleton<DocumentationSearchService>();
builder.Services.AddSingleton<GeneratedApiStore>();
builder.Services.AddOpenConnect(options => options.Privacy.Enabled = true);
builder.Services.AddOpenConnectComponents();

var assistantEnabled = builder.Configuration.GetValue<bool>("OpenConnect:Assistant:Enabled");
var gatewayAddress = builder.Configuration["OpenConnect:Assistant:GatewayUrl"];
if (assistantEnabled && Uri.TryCreate(gatewayAddress, UriKind.Absolute, out var gatewayUri))
{
    builder.Services.AddOpenConnectClient(options =>
    {
        options.ApiBaseAddress = gatewayUri;
        options.AllowInsecureHttp = builder.Environment.IsDevelopment();
    });
    builder.Services.AddOpenConnectComposer();
}

var app = builder.Build();
var content = app.Services.GetRequiredService<DocumentationContentStore>();
content.Load();

app.UseExceptionHandler("/error");
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/cloud/api/v1"))
    {
        context.Response.Headers.CacheControl = "no-store";
    }
    try
    {
        await next();
    }
    catch (Exception exception) when (
        context.Request.Path.StartsWithSegments("/cloud/api/v1") &&
        CloudDatabaseFailure.IsDatabaseFailure(exception))
    {
        context.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("OpenConnect.Cloud.Api")
            .LogWarning("Cloud control-plane request failed because its database is unavailable.");
        context.Response.Clear();
        await Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Cloud control plane is temporarily unavailable.").ExecuteAsync(context);
    }
});
if (gatewayTrust is not null)
{
    app.MapCloudGatewayReadiness(gatewayTrust, !string.IsNullOrWhiteSpace(cloudDatabaseConnection));
    app.MapCloudGatewayAuthorization(gatewayTrust, !string.IsNullOrWhiteSpace(cloudDatabaseConnection));
    app.MapCloudClassificationPrivacy(gatewayTrust, !string.IsNullOrWhiteSpace(cloudDatabaseConnection));
    app.MapCloudInferencePrivacy(gatewayTrust, !string.IsNullOrWhiteSpace(cloudDatabaseConnection));
    app.MapCloudVoiceGatewayEndpoints(gatewayTrust, !string.IsNullOrWhiteSpace(cloudDatabaseConnection));
    app.MapCloudTranslationPrivacy(gatewayTrust, !string.IsNullOrWhiteSpace(cloudDatabaseConnection));
    app.MapCloudInferenceOutputPrivacy(gatewayTrust, !string.IsNullOrWhiteSpace(cloudDatabaseConnection));
    app.MapCloudInferenceAccess(gatewayTrust, !string.IsNullOrWhiteSpace(cloudDatabaseConnection));
    app.MapCloudLiveUIPrivacy(gatewayTrust, !string.IsNullOrWhiteSpace(cloudDatabaseConnection));
    app.MapCloudLiveUIQuota(gatewayTrust, !string.IsNullOrWhiteSpace(cloudDatabaseConnection));
    app.MapCloudLiveUIDualQuota(gatewayTrust, !string.IsNullOrWhiteSpace(cloudDatabaseConnection));
    app.MapCloudLiveUIClaim(gatewayTrust, !string.IsNullOrWhiteSpace(cloudDatabaseConnection));
    app.MapCloudLiveUIOutputPrivacy(gatewayTrust, !string.IsNullOrWhiteSpace(cloudDatabaseConnection));
    app.MapCloudLiveUIAccess(gatewayTrust, !string.IsNullOrWhiteSpace(cloudDatabaseConnection));
    app.MapCloudClassificationDeployment(gatewayTrust, !string.IsNullOrWhiteSpace(cloudDatabaseConnection));
    app.MapCloudFingerprintKeyLookup(gatewayTrust, !string.IsNullOrWhiteSpace(cloudDatabaseConnection));
    app.MapCloudClassificationQuota(gatewayTrust, !string.IsNullOrWhiteSpace(cloudDatabaseConnection));
    app.MapCloudInferenceFingerprintKeyLookup(gatewayTrust, !string.IsNullOrWhiteSpace(cloudDatabaseConnection));
    app.MapCloudInferenceQuota(gatewayTrust, !string.IsNullOrWhiteSpace(cloudDatabaseConnection));
    app.MapCloudInferenceClaim(gatewayTrust, !string.IsNullOrWhiteSpace(cloudDatabaseConnection));
    app.MapCloudClassificationClaim(gatewayTrust, !string.IsNullOrWhiteSpace(cloudDatabaseConnection));
    app.MapCloudClassificationCompletion(gatewayTrust, !string.IsNullOrWhiteSpace(cloudDatabaseConnection));
}
if (workerTrust is not null)
{
    app.MapCloudClassificationConsumption(workerTrust, !string.IsNullOrWhiteSpace(cloudDatabaseConnection));
    app.MapCloudInferenceConsumption(workerTrust, !string.IsNullOrWhiteSpace(cloudDatabaseConnection));
    app.MapCloudVoiceWorkerEndpoints(workerTrust, !string.IsNullOrWhiteSpace(cloudDatabaseConnection));
    app.MapCloudLiveUIConsumption(workerTrust, !string.IsNullOrWhiteSpace(cloudDatabaseConnection));
    app.MapCloudLiveUISettlement(workerTrust, !string.IsNullOrWhiteSpace(cloudDatabaseConnection));
    app.MapCloudLiveUIMeteredConsumption(workerTrust, !string.IsNullOrWhiteSpace(cloudDatabaseConnection));
    app.MapCloudLiveUIDualSettlement(workerTrust, !string.IsNullOrWhiteSpace(cloudDatabaseConnection));
    app.MapCloudInferenceTokenSettlement(workerTrust, !string.IsNullOrWhiteSpace(cloudDatabaseConnection));
    app.MapCloudWorkerReadiness(workerTrust, !string.IsNullOrWhiteSpace(cloudDatabaseConnection));
}
app.MapCloudControlPlaneEndpoints(cloudOidc["Authority"], !string.IsNullOrWhiteSpace(cloudDatabaseConnection));
app.MapCloudLiveUIReconciliation(!string.IsNullOrWhiteSpace(cloudDatabaseConnection));
app.MapCloudIdentityEndpoints(cloudIdentityEnabled, googleIdentityEnabled);

app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", (DocumentationContentStore store) =>
    store.IsLoaded
        ? Results.Ok(new { status = "ready", documents = store.Documents.Count })
        : Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Documentation content is not ready."));
app.MapGet("/sitemap.xml", (HttpContext context, DocumentationContentStore store) =>
{
    var origin = $"{context.Request.Scheme}://{context.Request.Host}";
    var urls = new[] { $"{origin}/", $"{origin}/docs" }
        .Concat(store.Documents.Select(d => $"{origin}/docs/{d.Version}/{d.Slug}"));
    var xml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
        "<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">" +
        string.Concat(urls.Select(url => $"<url><loc>{System.Security.SecurityElement.Escape(url)}</loc></url>")) +
        "</urlset>";
    return Results.Text(xml, "application/xml");
});

app.MapRazorComponents<OpenConnect.Docs.Components.App>()
    .AddInteractiveServerRenderMode();

app.Run();
