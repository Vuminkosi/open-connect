using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;
using OpenConnect.Agents.Templates;
using OpenConnect.Client;
using OpenConnect.Components.Blazor;
using OpenConnect.Core;
using OpenConnect.Hosting.AspNetCore;
using OpenConnect.Hosting.AspNetCore.Execution;
using OpenConnect.Samples.Workers.Host;using OpenConnect.Persistence.EFCore.Configuration;
using OpenConnect.Persistence.EFCore.DependencyInjection;
using OpenConnect.Samples.Blazor;
using OpenConnect.Samples.Blazor.Components;
using OpenConnect.Samples.Blazor.Models;
using OpenConnect.Samples.Shared;

var builder = WebApplication.CreateBuilder(args);

// Persist Data Protection keys so antiforgery tokens remain valid across restarts
var keysDir = Directory.Exists("/data")
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
builder.Services.AddSingleton(gatewayOptions);
builder.Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(gatewayOptions));

if (gatewayOptions.Mode == TodoGatewayMode.SameHostGateway)
{
    builder.Services
        .AddAuthentication(SameHostDevelopmentAuthenticationHandler.SchemeName)
        .AddScheme<AuthenticationSchemeOptions, SameHostDevelopmentAuthenticationHandler>(SameHostDevelopmentAuthenticationHandler.SchemeName, _ => { });
    builder.Services.AddAuthorization();
    builder.Services.AddOpenConnectTaskExecution();
    builder.Services.AddOpenConnectAgent("worker-sample-direct");
    builder.Services.AddMetaTodoIntegration(builder.Configuration);
    builder.Services.AddOpenConnectClient(options =>
    {
        options.AllowInsecureHttp = true;
        options.ApiBaseAddress = new Uri(gatewayOptions.SameHostBaseUrl.TrimEnd('/') + "/openconnect/v1/");
    });
    builder.Services.AddHttpClient<IOpenConnectClient, OpenConnectClient>(client =>
        client.BaseAddress = new Uri(gatewayOptions.SameHostBaseUrl.TrimEnd('/') + "/openconnect/v1/"))
        .AddHttpMessageHandler<SameHostIdentityHandler>();
    builder.Services.AddTransient<SameHostIdentityHandler>();
}
else
{
    var gatewayUrl = builder.Configuration["GATEWAY_URL"]
        ?? builder.Configuration["OpenConnect:Gateway:Url"]
        ?? "http://gateway-host:8080/";
    builder.Services.AddOpenConnectClient(options =>
    {
        options.AllowInsecureHttp = true;
        options.ApiBaseAddress = new Uri(gatewayUrl.TrimEnd('/') + "/openconnect/v1/");
    });
    if (builder.Environment.IsDevelopment())
    {
        builder.Services.AddTransient<SameHostIdentityHandler>();
        builder.Services.AddHttpClient<IOpenConnectClient, OpenConnectClient>(client =>
            client.BaseAddress = new Uri(gatewayUrl.TrimEnd('/') + "/openconnect/v1/"))
            .AddHttpMessageHandler<SameHostIdentityHandler>();
    }
}
builder.Services.AddOpenConnectComponents();
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
builder.Services.AddSingleton<ITodoStore, InMemoryTodoStore>();
builder.Services.AddSingleton<ITenantTodoStore>(sp => (InMemoryTodoStore)sp.GetRequiredService<ITodoStore>());
builder.Services.AddSingleton(_ => TodoInternalOptions.FromConfiguration(builder.Configuration));

// OpenConnect Core & EFCore adapter
builder.Services.AddOpenConnect(options =>
{
    options.Privacy.Enabled = true;
    options.Agents.RegisterLeadAgent = true;
});

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

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

if (gatewayOptions.Mode == TodoGatewayMode.SameHostGateway)
{
    app.UseAuthentication();
    app.UseAuthorization();
    app.UseWebSockets();
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
if (gatewayOptions.Mode == TodoGatewayMode.SameHostGateway)
{
    app.MapOpenConnect();
}

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
