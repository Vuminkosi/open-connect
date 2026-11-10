using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;
using OpenConnect.Client;
using OpenConnect.Components.Blazor;
using OpenConnect.Core;
using OpenConnect.Persistence.EFCore.Configuration;
using OpenConnect.Persistence.EFCore.DependencyInjection;
using OpenConnect.Samples.Blazor;
using OpenConnect.Samples.Blazor.Components;
using OpenConnect.Samples.Blazor.Models;
using OpenConnect.Samples.Shared;

var builder = WebApplication.CreateBuilder(args);

// MudBlazor UI Services
builder.Services.AddMudServices();

// Blazor Server Interactive
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// OpenConnect Client, Composer, and Component Discovery
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

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAntiforgery();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<BlazorDbContext>();
    await db.Database.MigrateAsync().ConfigureAwait(false);
}

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
