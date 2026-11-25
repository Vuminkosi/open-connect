using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;
using OpenConnect.Agents.Templates;
using OpenConnect.Client;
using OpenConnect.Components.Blazor;
using OpenConnect.Core;
using OpenConnect.Hosting.AspNetCore;
using OpenConnect.Hosting.AspNetCore.Execution;
using OpenConnect.LiveUI;
using OpenConnect.LiveUI.Blazor;
using OpenConnect.LiveUI.Inference;
using OpenConnect.Persistence.EFCore.Configuration;
using OpenConnect.Persistence.EFCore.DependencyInjection;
using OpenConnect.Privacy;
using OpenConnect.Samples.Blazor;
using OpenConnect.Samples.Blazor.Components;
using OpenConnect.Samples.Blazor.Models;
using OpenConnect.Samples.Shared;
using OpenConnect.Samples.Workers.Host;

var builder = WebApplication.CreateBuilder(args);

// Persist Data Protection keys so antiforgery tokens remain valid across restarts
var configuredKeysDirectory = builder.Configuration["OPENCONNECT_BLAZOR_DATA_PROTECTION_KEYS_PATH"];
var keysDir = !string.IsNullOrWhiteSpace(configuredKeysDirectory)
    ? Path.GetFullPath(configuredKeysDirectory)
    : Directory.Exists("/data")
        ? "/data/dataprotection-keys"
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".openconnect", "keys");
Directory.CreateDirectory(keysDir);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keysDir))
    .SetApplicationName("OpenConnect.Samples.Blazor");

// MudBlazor UI Services
builder.Services.AddMudServices();

// Blazor Server Interactive
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// OpenConnect Client, Composer, and Component Discovery
var gatewayOptions = TodoGatewayOptions.FromConfiguration(builder.Configuration, builder.Environment);
var sameHostGatewayPlanner = builder.Configuration.GetValue<bool>("OpenConnect:LiveUI:SameHostGatewayPlanner");
var externalGatewayPlanner = builder.Configuration.GetValue<bool>("OpenConnect:LiveUI:ExternalGatewayPlanner");
var cloudLiveUIEnabled = builder.Configuration.GetValue<bool>("OpenConnect:LiveUI:Cloud:Enabled");
if (cloudLiveUIEnabled && (gatewayOptions.Mode != TodoGatewayMode.SameHostGateway || !builder.Environment.IsDevelopment() ||
    sameHostGatewayPlanner || externalGatewayPlanner))
    throw new InvalidOperationException("Cloud LiveUI in this sample requires SameHostGateway in Development and no other gateway planner.");
if (externalGatewayPlanner && gatewayOptions.Mode != TodoGatewayMode.ExternalGateway)
    throw new InvalidOperationException("The external gateway planner requires ExternalGateway mode.");
var sameHostWorkerConfigured = gatewayOptions.Mode == TodoGatewayMode.SameHostGateway &&
    !string.IsNullOrWhiteSpace(builder.Configuration["OpenConnect:LiveUI:Inference:Worker:Token"]);
if (sameHostGatewayPlanner && (gatewayOptions.Mode != TodoGatewayMode.SameHostGateway || !builder.Environment.IsDevelopment()))
    throw new InvalidOperationException("The same-host gateway planner requires SameHostGateway in Development.");
builder.Services.AddSingleton(gatewayOptions);
builder.Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(gatewayOptions));
if (builder.Configuration.GetValue<bool>("OpenConnect:DocumentDemo:Enabled"))
{
    var documentAccess = new TodoDocumentDemoAccess(builder.Environment, gatewayOptions, enabled: true);
    builder.Services.AddSingleton(documentAccess);
    builder.Services.AddOpenConnectRulesDetector();
    builder.Services.EnsureHostPrivacyServices();
    builder.Services.AddScoped<TodoDocumentDemo>();
    if (builder.Configuration.GetValue<bool>("OpenConnect:KnowledgeDemo:Enabled"))
        builder.Services.AddScoped<TodoKnowledgeDemo>();
    builder.Services.AddCascadingAuthenticationState();
}
var localGatewayBearer = gatewayOptions.Mode == TodoGatewayMode.ExternalGateway
    ? builder.Configuration["OpenConnect:Gateway:LocalServiceBearer"]
    : null;

if (gatewayOptions.Mode == TodoGatewayMode.SameHostGateway)
{
    builder.Services
        .AddAuthentication(SameHostDevelopmentAuthenticationHandler.SchemeName)
        .AddScheme<AuthenticationSchemeOptions, SameHostDevelopmentAuthenticationHandler>(SameHostDevelopmentAuthenticationHandler.SchemeName, _ => { });
    builder.Services.AddAuthorization();
    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.AddPolicy("liveui-inference", context =>
        {
            var tenant = context.User.FindFirst("tenant_id")?.Value ?? "anonymous";
            var principal = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? context.User.FindFirst("sub")?.Value ?? "anonymous";
            return RateLimitPartition.GetFixedWindowLimiter($"{tenant}:{principal}", _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 20,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            });
        });
    });
    builder.Services.AddOpenConnectTaskExecution();
    builder.Services.AddOpenConnectAgent("worker-sample-direct");
    builder.Services.AddMetaTodoIntegration(builder.Configuration);
    RegisterSameHostLiveUIWorker(builder.Services, builder.Configuration);
    builder.Services.AddOpenConnectClient(options =>
    {
        options.AllowInsecureHttp = true;
        options.ApiBaseAddress = new Uri(gatewayOptions.SameHostBaseUrl.TrimEnd('/') + "/openconnect/v1/");
        options.WebSocketHeaders["X-OpenConnect-Test-Tenant"] = gatewayOptions.TenantId;
        options.WebSocketHeaders["X-OpenConnect-Test-Principal"] = gatewayOptions.PrincipalId;
    });
}
else
{
    var gatewayUrl = builder.Configuration["GATEWAY_URL"]
        ?? builder.Configuration["OpenConnect:Gateway:Url"]
        ?? "http://gateway-host:8080/";
    var gatewayBase = gatewayUrl.TrimEnd('/');
    var gatewayApiBase = gatewayBase.EndsWith("/openconnect/v1", StringComparison.OrdinalIgnoreCase)
        ? gatewayBase + "/"
        : gatewayBase + "/openconnect/v1/";
    if (!string.IsNullOrEmpty(localGatewayBearer))
    {
        if (!builder.Environment.IsDevelopment() || !Uri.TryCreate(gatewayApiBase, UriKind.Absolute, out var localGateway) ||
            !System.Net.IPAddress.TryParse(localGateway.Host.Trim('[', ']'), out var gatewayAddress) ||
            !System.Net.IPAddress.IsLoopback(gatewayAddress.IsIPv4MappedToIPv6 ? gatewayAddress.MapToIPv4() : gatewayAddress))
            throw new InvalidOperationException("A local gateway service credential requires a loopback gateway in Development.");
        try
        {
            if (Convert.FromBase64String(localGatewayBearer).Length != 32 || Convert.ToBase64String(Convert.FromBase64String(localGatewayBearer)) != localGatewayBearer)
                throw new FormatException();
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("The local gateway service credential is invalid.");
        }
    }
    builder.Services.AddOpenConnectClient(options =>
    {
        options.AllowInsecureHttp = true;
        options.ApiBaseAddress = new Uri(gatewayApiBase);
        if (!string.IsNullOrEmpty(localGatewayBearer))
            options.WebSocketHeaders["Authorization"] = $"Bearer {localGatewayBearer}";
    });
    if (!string.IsNullOrEmpty(localGatewayBearer))
        builder.Services.AddHttpClient(nameof(IOpenConnectClient), client =>
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", localGatewayBearer))
            .ConfigurePrimaryHttpMessageHandler(CreateLocalGatewayTransport);
}
builder.Services.AddOpenConnectComponents();
builder.Services.AddScoped<ILiveUIActionAuthorizer, TodoLiveUIActionAuthorizer>();
builder.Services.AddOpenConnectLiveUIBlazor(options =>
{
    options.Discovery.EnableRuntimeFallback = true;
    options.Interaction.EnableNavigation = true;
    options.Interaction.EnableFiltering = true;
    options.Interaction.EnableHighlighting = true;
    options.Narration.Enabled = true;
    options.Narration.SynchronizeWithSpeech = true;
});
builder.Services.Configure<LiveUIInferenceOptions>(options =>
{
    options.Worker.Enabled = !(sameHostGatewayPlanner || cloudLiveUIEnabled) || sameHostWorkerConfigured;
    options.Gateway.Enabled = sameHostGatewayPlanner || externalGatewayPlanner || cloudLiveUIEnabled;
    var configuredFallback = builder.Configuration.GetValue<LiveUIFallbackPolicy?>("OpenConnect:LiveUI:Inference:Gateway:FallbackPolicy");
    if (configuredFallback.HasValue)
        options.Gateway.FallbackPolicy = configuredFallback.Value;
    if ((sameHostGatewayPlanner || cloudLiveUIEnabled) && !sameHostWorkerConfigured)
        options.Mode = LiveUIInferenceMode.GatewayOnly;
});
if (cloudLiveUIEnabled)
{
    builder.Services.AddCloudLiveUIBlazorHostAuthorizer<SameHostCloudLiveUISessionContextProvider>();
    builder.Services.AddCloudLiveUIInferenceProvider(options =>
    {
        options.Origin = Uri.TryCreate(builder.Configuration["OpenConnect:LiveUI:Cloud:Origin"], UriKind.Absolute, out var origin) ? origin : null;
        options.Credential = builder.Configuration["OpenConnect:LiveUI:Cloud:Credential"];
    });
}
if (sameHostGatewayPlanner)
    builder.Services.AddLiveUIGatewayInferenceProvider<SameHostGatewayPlanner>();
if (externalGatewayPlanner)
{
    var gatewayPlannerClient = builder.Services.AddHttpClient<GatewayPlannerLiveUIInferenceProvider>((services, client) =>
    {
        var gatewayClientOptions = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<OpenConnectClientOptions>>().Value;
        client.BaseAddress = gatewayClientOptions.ApiBaseAddress;
        foreach (var header in gatewayClientOptions.WebSocketHeaders)
            client.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
        client.Timeout = Timeout.InfiniteTimeSpan;
    });
    if (!string.IsNullOrEmpty(localGatewayBearer))
        gatewayPlannerClient.ConfigurePrimaryHttpMessageHandler(CreateLocalGatewayTransport);
    builder.Services.AddLiveUIGatewayInferenceProvider<GatewayPlannerLiveUIInferenceProvider>();
}
builder.Services.AddOpenConnectComposer(options =>
{
    options.Position = ComposerPosition.BottomRight;
    options.EnableTaskProgress = true;
    options.EnableComponentDiscovery = true;
    options.EnableVoiceInput = true;
    options.EnableVoiceOutput = true;
});

// Register sample components in the OpenConnect catalogue
builder.Services.AddOpenConnectComponent<TodoListComponent>();
builder.Services.AddOpenConnectComponent<TodoEditorComponent>();
builder.Services.AddOpenConnectComponent<TodoProgressComponent>();

// Todo store
var useAcceptanceTodoData = builder.Configuration.GetValue<bool>("OpenConnect:Todo:AcceptanceData") ||
    bool.TryParse(Environment.GetEnvironmentVariable("OPENCONNECT_BLAZOR_ACCEPTANCE_DATA"), out var acceptanceDataEnabled) && acceptanceDataEnabled;
var exposeLiveUIMetricsForAcceptance = builder.Environment.IsDevelopment() && useAcceptanceTodoData;
if (exposeLiveUIMetricsForAcceptance)
    builder.Services.AddSingleton<LiveUIMetricsAcceptanceRecorder>();

builder.Services.AddSingleton(sp => new SqliteTodoStore(sp.GetRequiredService<IServiceScopeFactory>(), useAcceptanceTodoData));
builder.Services.AddSingleton<ITodoStore>(sp => sp.GetRequiredService<SqliteTodoStore>());
builder.Services.AddSingleton<ITenantTodoStore>(sp => sp.GetRequiredService<SqliteTodoStore>());
builder.Services.AddSingleton(_ => TodoInternalOptions.FromConfiguration(builder.Configuration));

// OpenConnect Core & EFCore adapter
builder.Services.AddOpenConnect(options =>
{
    options.Privacy.Enabled = true;
    options.Agents.RegisterLeadAgent = true;
});
builder.Services.Configure<OpenConnect.Core.Configuration.OpenConnectVoiceOptions>(
    builder.Configuration.GetSection("OpenConnect:Voice"));
TodoCloudPrivacyConfiguration.Configure(builder.Services, builder.Configuration);
if (builder.Configuration.GetValue<bool>("OpenConnect:CapabilityMonitor:Enabled"))
{
    var sources = TodoCapabilityMonitorConfiguration.Create(builder.Configuration, builder.Environment, gatewayOptions);
    builder.Services.AddOpenConnectWorkerCapabilityMonitor(sources);
}

var sqliteFile = SampleEnvironment.ResolveSqliteFile("OPENCONNECT_BLAZOR_SQLITE_PATH", "blazor-sample.db");
builder.Services.AddDbContext<BlazorDbContext>(options =>
    BlazorDbContext.ConfigureSqlite(options, SampleEnvironment.ToSqliteConnectionString(sqliteFile)));
builder.Services.AddOpenConnectEntityFramework<BlazorDbContext>(options =>
{
    options.Migrations = OpenConnectMigrationMode.Manual;
    options.Consistency = OpenConnectPersistenceConsistency.DurableSingleDatabase;
    options.ValidateSchema = OpenConnectSchemaValidationMode.ValidateModel;
    options.OutboxClaimStrategy = OpenConnectOutboxClaimStrategy.Auto;
});

var app = builder.Build();

LocalAcceptanceGuard.UseIfEnabled(app);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseWebSockets();

if (gatewayOptions.Mode == TodoGatewayMode.SameHostGateway)
{
    app.UseAuthentication();
    app.UseAuthorization();
    app.UseRateLimiter();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAntiforgery();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<BlazorDbContext>();
    await db.Database.MigrateAsync().ConfigureAwait(false);
}

app.MapTodoInternalApi();
if (exposeLiveUIMetricsForAcceptance)
{
    app.MapGet("/__acceptance/liveui-metrics", (LiveUIMetricsAcceptanceRecorder recorder) => Results.Ok(new
    {
        framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
        operatingSystem = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
        measurements = recorder.Snapshot()
    }));
    app.MapPost("/__acceptance/liveui-metrics/reset", (LiveUIMetricsAcceptanceRecorder recorder) =>
    {
        recorder.Reset();
        return Results.NoContent();
    });
}

if (gatewayOptions.Mode == TodoGatewayMode.SameHostGateway)
{
    app.MapOpenConnect();
    app.MapOpenConnectLiveUIInference();
}
else if (builder.Environment.IsDevelopment())
{
    ExternalGatewayVoiceProxy.Map(app, gatewayOptions, builder.Configuration);
}

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

static HttpMessageHandler CreateLocalGatewayTransport() => new SocketsHttpHandler
{
    AllowAutoRedirect = false,
    UseProxy = false,
    UseCookies = false
};

static void RegisterSameHostLiveUIWorker(IServiceCollection services, IConfiguration configuration)
{
    var token = configuration["OpenConnect:LiveUI:Inference:Worker:Token"];
    if (string.IsNullOrWhiteSpace(token))
        return;

    var endpoint = configuration["OpenConnect:LiveUI:Inference:Worker:Endpoint"] ?? "http://liveui-inference:8080";
    if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var workerEndpoint))
        throw new InvalidOperationException("OpenConnect:LiveUI:Inference:Worker:Endpoint must be an absolute HTTP endpoint.");

    services.AddLiveUILocalWorkerInferenceProvider(options =>
    {
        options.Endpoint = workerEndpoint;
        options.BearerToken = token;
    }, registerRouterProvider: false);
    services.Configure<LiveUIInferenceOptions>(options => options.Worker.Enabled = true);
}
