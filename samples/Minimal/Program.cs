using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Persistence.EFCore.Configuration;
using OpenConnect.Persistence.EFCore.DependencyInjection;
using OpenConnect.Samples.Minimal;
using OpenConnect.Samples.Shared;

var verb = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
if (verb is not ("migrate" or "run" or "verify"))
{
    Console.WriteLine("Usage: OpenConnect.Samples.Minimal <migrate|run|verify>");
    Console.WriteLine("  migrate : apply this sample's own migrations (idempotent).");
    Console.WriteLine("  run     : ensure schema, connect, and report mapped tables. Seeds no data.");
    Console.WriteLine("  verify  : fail unless schema is reachable with zero pending migrations.");
    return 2;
}

var sqliteFile = SampleEnvironment.ResolveSqliteFile("OPENCONNECT_MINIMAL_SQLITE_PATH", "minimal-sample.db");

var builder = SampleHost.CreateBuilder(args);
builder.Services.AddDbContext<MinimalDbContext>(options =>
    MinimalDbContext.ConfigureSqlite(options, SampleEnvironment.ToSqliteConnectionString(sqliteFile)));
builder.Services.AddOpenConnectEntityFramework<MinimalDbContext>(options =>
{
    options.Migrations = OpenConnectMigrationMode.Manual;
    options.Consistency = OpenConnectPersistenceConsistency.DurableSingleDatabase;
    options.ValidateSchema = OpenConnectSchemaValidationMode.ValidateModel;
    options.OutboxClaimStrategy = OpenConnectOutboxClaimStrategy.Auto;
});

using var host = builder.Build();
await host.StartAsync().ConfigureAwait(false);

using var scope = host.Services.CreateScope();
var db = scope.ServiceProvider.GetRequiredService<MinimalDbContext>();

switch (verb)
{
    case "migrate":
        await db.Database.MigrateAsync().ConfigureAwait(false);
        Console.WriteLine($"Migrations applied (history: {MinimalDbContext.HistoryTable}).");
        break;
    case "run":
        await db.Database.MigrateAsync().ConfigureAwait(false);
        await SampleVerify.VerifyModelAsync(db).ConfigureAwait(false);
        Console.WriteLine("Run check complete. No data seeded.");
        break;
    case "verify":
        await SampleVerify.VerifyModelAsync(db).ConfigureAwait(false);
        var applied = await SampleVerify.VerifyMigrationsAsync(db).ConfigureAwait(false);
        Console.WriteLine($"Verify OK: {applied} migration(s) applied, none pending.");
        break;
}

return 0;
