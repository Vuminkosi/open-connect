using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Persistence.EFCore.Configuration;
using OpenConnect.Persistence.EFCore.DependencyInjection;
using OpenConnect.Samples.Shared;
using OpenConnect.Samples.Sqlite;

var verb = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
if (verb is not ("migrate" or "run" or "verify"))
{
    Console.WriteLine("Usage: OpenConnect.Samples.Sqlite <migrate|run|verify>");
    Console.WriteLine("  migrate : apply this sample's own migrations (idempotent).");
    Console.WriteLine("  run     : ensure schema, connect, and report mapped tables. Seeds no data.");
    Console.WriteLine("  verify  : fail unless schema is reachable with zero pending migrations.");
    return 2;
}

var sqliteFile = SampleEnvironment.ResolveSqliteFile("OPENCONNECT_SQLITE_PATH", "sqlite-sample.db");

var builder = SampleHost.CreateBuilder(args);
builder.Services.AddDbContext<SqliteSampleDbContext>(options =>
    SqliteSampleDbContext.ConfigureSqlite(options, SampleEnvironment.ToSqliteConnectionString(sqliteFile)));
builder.Services.AddOpenConnectEntityFramework<SqliteSampleDbContext>(options =>
{
    options.Migrations = OpenConnectMigrationMode.Manual;
    options.Consistency = OpenConnectPersistenceConsistency.DurableSingleDatabase;
    options.ValidateSchema = OpenConnectSchemaValidationMode.ValidateModel;
    options.OutboxClaimStrategy = OpenConnectOutboxClaimStrategy.Auto;
    options.SqliteTablePrefix = "oc_";
});

using var host = builder.Build();
await host.StartAsync().ConfigureAwait(false);

using var scope = host.Services.CreateScope();
var db = scope.ServiceProvider.GetRequiredService<SqliteSampleDbContext>();

switch (verb)
{
    case "migrate":
        await db.Database.MigrateAsync().ConfigureAwait(false);
        Console.WriteLine($"Migrations applied (history: {SqliteSampleDbContext.HistoryTable}).");
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
