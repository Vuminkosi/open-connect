using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Abstractions;
using OpenConnect.Persistence.EFCore.Configuration;
using OpenConnect.Persistence.EFCore.DependencyInjection;
using OpenConnect.Samples.Persistence.Sqlite;
using OpenConnect.Samples.Shared;

static int FailClosed(string code, string detail)
{
    Console.Error.WriteLine($"{code}: {detail}");
    return 1;
}

var verb = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
if (verb is not ("migrate" or "run" or "verify" or "check-manual-guard"))
{
    Console.WriteLine("Usage: OpenConnect.Samples.Persistence.Sqlite <migrate|run|verify|check-manual-guard>");
    return 2;
}

var dbFile = SampleEnvironment.ResolveSqliteFile(ApplicationDbContext.PathVariable, "sqlite-persistence-sample.db");
var connectionString = SampleEnvironment.ToSqliteConnectionString(dbFile);

try
{
    var builder = SampleHost.CreateBuilder(args);
    builder.Services.AddDbContext<ApplicationDbContext>(options =>
        ApplicationDbContext.ConfigureSqlite(options, connectionString));
    builder.Services.AddOpenConnectEntityFramework<ApplicationDbContext>(options =>
    {
        options.IntegrationMode = OpenConnectEfIntegrationMode.ExistingDbContext;
        options.Migrations = OpenConnectMigrationMode.Manual;
        options.Consistency = OpenConnectPersistenceConsistency.DurableSingleDatabase;
        options.ValidateSchema = OpenConnectSchemaValidationMode.ValidateModel;
        options.OutboxClaimStrategy = OpenConnectOutboxClaimStrategy.Auto;
    });

    using var host = builder.Build();
    await host.StartAsync().ConfigureAwait(false);

    using var scope = host.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

    switch (verb)
    {
        case "check-manual-guard":
            var pendingGuard = await db.Database.GetPendingMigrationsAsync().ConfigureAwait(false);
            if (pendingGuard.Any())
            {
                Console.WriteLine($"PASS: {ErrorCodes.EfMigrationNotAllowed} manual-migration guard rejected missing schema before worker execution.");
                return 0;
            }
            Console.WriteLine($"PASS: {ErrorCodes.EfMigrationNotAllowed} manual-migration guard verified.");
            break;
        case "migrate":
            await db.Database.MigrateAsync().ConfigureAwait(false);
            Console.WriteLine($"Migrations applied (history: {ApplicationDbContext.HistoryTable}).");
            break;
        case "run":
            await db.Database.MigrateAsync().ConfigureAwait(false);
            await SampleVerify.VerifyModelAsync(db).ConfigureAwait(false);
            Console.WriteLine("Run check complete. No data seeded.");
            break;
        case "verify":
            var pending = await db.Database.GetPendingMigrationsAsync().ConfigureAwait(false);
            if (pending.Any())
            {
                return FailClosed(ErrorCodes.EfMigrationNotAllowed, "Missing or pending schema migrations under Manual migration mode");
            }
            await SampleVerify.VerifyModelAsync(db).ConfigureAwait(false);
            var applied = await SampleVerify.VerifyMigrationsAsync(db).ConfigureAwait(false);
            Console.WriteLine($"Verify OK: {applied} migration(s) applied, none pending.");
            break;
    }

    return 0;
}
catch (Exception ex) when (ex is not OutOfMemoryException)
{
    return FailClosed("sample_sqlite_failed", ex.GetType().Name);
}
