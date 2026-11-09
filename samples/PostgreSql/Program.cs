using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Persistence.EFCore.Configuration;
using OpenConnect.Persistence.EFCore.DependencyInjection;
using OpenConnect.Samples.PostgreSql;
using OpenConnect.Samples.Shared;

static int FailClosed(string code, string detail)
{
    Console.Error.WriteLine($"{code}: {detail} (connection details withheld).");
    return 1;
}

var verb = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
if (verb is not ("migrate" or "run" or "verify" or "parity"))
{
    Console.WriteLine("Usage: OpenConnect.Samples.PostgreSql <migrate|run|verify|parity>");
    Console.WriteLine("  migrate : apply this sample's own migrations (idempotent).");
    Console.WriteLine("  run     : ensure schema, connect, and report mapped tables. Seeds no data.");
    Console.WriteLine("  verify  : fail unless schema is reachable with zero pending migrations.");
    Console.WriteLine("  parity  : offline legacy 8-table parity check (no connection, no seeding).");
    return 2;
}

if (verb is "parity")
{
    try
    {
        var offlineConnection = Environment.GetEnvironmentVariable(
            PostgreSqlSampleDbContext.ConnectionStringVariable)
            ?? "Host=localhost;Port=5432;Database=parity_offline;Username=postgres";
        var offlineOptions = new DbContextOptionsBuilder<PostgreSqlSampleDbContext>();
        PostgreSqlSampleDbContext.ConfigureNpgsql(offlineOptions, offlineConnection);
        using var probe = new PostgreSqlSampleDbContext(offlineOptions.Options);
        return SampleParity.ReportParity(probe);
    }
    catch (Exception ex) when (ex is not OutOfMemoryException)
    {
        return FailClosed("sample_parity_failed", ex.GetType().Name);
    }
}

string connectionString;
try
{
    connectionString = PostgreSqlSampleDbContext.ResolveConnectionString();
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

try
{
    var builder = SampleHost.CreateBuilder(args);
    builder.Services.AddDbContext<PostgreSqlSampleDbContext>(options =>
        PostgreSqlSampleDbContext.ConfigureNpgsql(options, connectionString));
    builder.Services.AddOpenConnectEntityFramework<PostgreSqlSampleDbContext>(options =>
    {
        options.Migrations = OpenConnectMigrationMode.Manual;
        options.Consistency = OpenConnectPersistenceConsistency.DurableSingleDatabase;
        options.ValidateSchema = OpenConnectSchemaValidationMode.ValidateModel;
        options.OutboxClaimStrategy = OpenConnectOutboxClaimStrategy.Auto;
    });

    using var host = builder.Build();
    await host.StartAsync().ConfigureAwait(false);

    using var scope = host.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<PostgreSqlSampleDbContext>();

    switch (verb)
    {
        case "migrate":
            await db.Database.MigrateAsync().ConfigureAwait(false);
            Console.WriteLine(
                $"Migrations applied (history: {PostgreSqlSampleDbContext.HistorySchema}.{PostgreSqlSampleDbContext.HistoryTable}).");
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
    return FailClosed("sample_postgres_unreachable", ex.GetType().Name);
}
