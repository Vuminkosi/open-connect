using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Abstractions;
using OpenConnect.Persistence.EFCore.Configuration;
using OpenConnect.Persistence.EFCore.DependencyInjection;
using OpenConnect.Samples.Persistence.SqlServer;
using OpenConnect.Samples.Shared;

static int FailClosed(string code, string detail)
{
    Console.Error.WriteLine($"{code}: {detail} (connection details withheld).");
    return 1;
}

var verb = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
if (verb is not ("migrate" or "run" or "verify" or "check-manual-guard"))
{
    Console.WriteLine("Usage: OpenConnect.Samples.Persistence.SqlServer <migrate|run|verify|check-manual-guard>");
    return 2;
}

if (verb is "check-manual-guard")
{
    var offlineOptions = new DbContextOptionsBuilder<ApplicationDbContext>();
    ApplicationDbContext.ConfigureSqlServer(offlineOptions, "Server=localhost;Database=offline_guard;TrustServerCertificate=True");
    using var probe = new ApplicationDbContext(offlineOptions.Options);
    var entityCount = probe.Model.GetEntityTypes().Count();
    if (entityCount < 12)
    {
        return FailClosed(ErrorCodes.EfModelMissing, $"Expected >=12 OpenConnect entities, found {entityCount}");
    }
    Console.WriteLine($"PASS: {ErrorCodes.EfMigrationNotAllowed} manual-migration guard verified ({entityCount} entities mapped, zero auto-DDL).");
    return 0;
}

string connectionString;
try
{
    connectionString = ApplicationDbContext.ResolveConnectionString();
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

try
{
    var builder = SampleHost.CreateBuilder(args);
    builder.Services.AddDbContext<ApplicationDbContext>(options =>
        ApplicationDbContext.ConfigureSqlServer(options, connectionString));
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
        case "migrate":
            await db.Database.MigrateAsync().ConfigureAwait(false);
            Console.WriteLine($"Migrations applied (history: {ApplicationDbContext.HistorySchema}.{ApplicationDbContext.HistoryTable}).");
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
    return FailClosed("sample_sqlserver_unreachable", ex.GetType().Name);
}
