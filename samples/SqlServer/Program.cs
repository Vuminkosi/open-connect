using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Persistence.EFCore.Configuration;
using OpenConnect.Persistence.EFCore.DependencyInjection;
using OpenConnect.Samples.Shared;
using OpenConnect.Samples.SqlServer;

static int FailClosed(string code, string detail)
{
    Console.Error.WriteLine($"{code}: {detail} (connection details withheld).");
    return 1;
}

var verb = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
if (verb is not ("migrate" or "run" or "verify"))
{
    Console.WriteLine("Usage: OpenConnect.Samples.SqlServer <migrate|run|verify>");
    Console.WriteLine("  migrate : apply this sample's own migrations (idempotent).");
    Console.WriteLine("  run     : ensure schema, connect, and report mapped tables. Seeds no data.");
    Console.WriteLine("  verify  : fail unless schema is reachable with zero pending migrations.");
    return 2;
}

string connectionString;
try
{
    connectionString = SqlServerSampleDbContext.ResolveConnectionString();
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

try
{
    var builder = SampleHost.CreateBuilder(args);
    builder.Services.AddDbContext<SqlServerSampleDbContext>(options =>
        SqlServerSampleDbContext.ConfigureSqlServer(options, connectionString));
    builder.Services.AddOpenConnectEntityFramework<SqlServerSampleDbContext>(options =>
    {
        options.Migrations = OpenConnectMigrationMode.Manual;
        options.Consistency = OpenConnectPersistenceConsistency.DurableSingleDatabase;
        options.ValidateSchema = OpenConnectSchemaValidationMode.ValidateModel;
        options.OutboxClaimStrategy = OpenConnectOutboxClaimStrategy.Auto;
    });

    using var host = builder.Build();
    await host.StartAsync().ConfigureAwait(false);

    using var scope = host.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<SqlServerSampleDbContext>();

    switch (verb)
    {
        case "migrate":
            await db.Database.MigrateAsync().ConfigureAwait(false);
            Console.WriteLine(
                $"Migrations applied (history: {SqlServerSampleDbContext.HistorySchema}.{SqlServerSampleDbContext.HistoryTable}).");
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
}
catch (Exception ex) when (ex is not OutOfMemoryException)
{
    // Fail closed on unreachable/bad connection strings: stable code, no secrets,
    // no partial success claims. Restart-safe: already-applied migrations stay applied.
    return FailClosed("sample_sqlserver_unreachable", ex.GetType().Name);
}
