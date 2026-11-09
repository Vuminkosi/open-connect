using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Persistence;
using OpenConnect.Persistence.EFCore.Configuration;
using OpenConnect.Persistence.EFCore.DependencyInjection;
using OpenConnect.Samples.McpMicroservices.ServiceB;
using OpenConnect.Samples.Shared;

var verb = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
if (verb is not ("migrate" or "run" or "verify" or "check-isolation"))
{
    Console.WriteLine("Usage: OpenConnect.Samples.McpMicroservices.ServiceB <migrate|run|verify|check-isolation>");
    Console.WriteLine("  migrate         : apply this service's own migrations (idempotent).");
    Console.WriteLine("  run             : ensure schema, connect, and report mapped tables. Seeds no data.");
    Console.WriteLine("  verify          : fail unless schema is reachable with zero pending migrations.");
    Console.WriteLine("  check-isolation : fail unless service A's demo session is invisible to service B.");
    return 2;
}

var sqliteFile = SampleEnvironment.ResolveSqliteFile("OPENCONNECT_MICROSERVICE_B_SQLITE_PATH", "microservice-b.db");

var builder = SampleHost.CreateBuilder(args);
builder.Services.AddDbContext<ServiceBDbContext>(options =>
    ServiceBDbContext.ConfigureSqlite(options, SampleEnvironment.ToSqliteConnectionString(sqliteFile)));
builder.Services.AddOpenConnectEntityFramework<ServiceBDbContext>(options =>
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
var db = scope.ServiceProvider.GetRequiredService<ServiceBDbContext>();

switch (verb)
{
    case "migrate":
        await db.Database.MigrateAsync().ConfigureAwait(false);
        Console.WriteLine($"Migrations applied (history: {ServiceBDbContext.HistoryTable}).");
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
    case "check-isolation":
        var sessions = scope.ServiceProvider.GetRequiredService<ISessionStore>();
        var demo = await sessions.GetAsync(
            new SessionId(Ulid.Parse(ServiceBDemoKeys.SessionUlid)),
            new TenantId(Ulid.Parse(ServiceBDemoKeys.TenantUlid)),
            new PrincipalId(Ulid.Parse(ServiceBDemoKeys.PrincipalUlid))).ConfigureAwait(false);
        if (demo is not null)
        {
            Console.Error.WriteLine("isolation check FAILED: service A's demo session is visible to service B (cross-service leak).");
            return 1;
        }

        Console.WriteLine("ISOLATION PASS: service A's demo session is invisible to service B.");
        break;
}

return 0;
