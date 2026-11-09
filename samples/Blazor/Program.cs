using Microsoft.EntityFrameworkCore;
using OpenConnect.Persistence.EFCore.Configuration;
using OpenConnect.Persistence.EFCore.DependencyInjection;
using OpenConnect.Samples.Blazor;
using OpenConnect.Samples.Blazor.Components;
using OpenConnect.Samples.Shared;

var builder = WebApplication.CreateBuilder(args);

// Blazor Server (interactive) + routing for the sample's single job screen.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Exactly one provider (SQLite file) + the hardened EFCore adapter, mirroring
// the Minimal console sample's registration (own context, own history table).
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

// Idempotent startup: applies this sample's own migrations only
// (history: __BlazorMigrationsHistory). Seeds no data.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<BlazorDbContext>();
    await db.Database.MigrateAsync().ConfigureAwait(false);
}

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
