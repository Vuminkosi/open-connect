using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Persistence;
using OpenConnect.Persistence.EFCore.Configuration;
using OpenConnect.Persistence.EFCore.DependencyInjection;
using OpenConnect.Samples.McpMicroservices.ServiceA;
using OpenConnect.Samples.Shared;

var verb = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
if (verb is not ("migrate" or "run" or "verify" or "seed-demo"))
{
    Console.WriteLine("Usage: OpenConnect.Samples.McpMicroservices.ServiceA <migrate|run|verify|seed-demo>");
    Console.WriteLine("  migrate   : apply this service's own migrations (idempotent).");
    Console.WriteLine("  run       : ensure schema, connect, and report mapped tables. Seeds no data.");
    Console.WriteLine("  verify    : fail unless schema is reachable with zero pending migrations.");
    Console.WriteLine("  seed-demo : idempotently upsert the fixed demo session into service A's database only.");
    return 2;
}

var sqliteFile = SampleEnvironment.ResolveSqliteFile("OPENCONNECT_MICROSERVICE_A_SQLITE_PATH", "microservice-a.db");

var builder = SampleHost.CreateBuilder(args);
builder.Services.AddDbContext<ServiceADbContext>(options =>
    ServiceADbContext.ConfigureSqlite(options, SampleEnvironment.ToSqliteConnectionString(sqliteFile)));
builder.Services.AddOpenConnectEntityFramework<ServiceADbContext>(options =>
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
var db = scope.ServiceProvider.GetRequiredService<ServiceADbContext>();

switch (verb)
{
    case "migrate":
        await db.Database.MigrateAsync().ConfigureAwait(false);
        Console.WriteLine($"Migrations applied (history: {ServiceADbContext.HistoryTable}).");
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
    case "seed-demo":
        await db.Database.MigrateAsync().ConfigureAwait(false);
        var sessions = scope.ServiceProvider.GetRequiredService<ISessionStore>();
        var record = new SessionRecord(
            new SessionId(Ulid.Parse(ServiceADemoKeys.SessionUlid)),
            new TenantId(Ulid.Parse(ServiceADemoKeys.TenantUlid)),
            new PrincipalId(Ulid.Parse(ServiceADemoKeys.PrincipalUlid)),
            1);
        await sessions.UpsertAsync(record).ConfigureAwait(false);
        var reread = await sessions.GetAsync(record.Id, record.TenantId, record.PrincipalId).ConfigureAwait(false);
        if (reread is null)
        {
            throw new InvalidOperationException("seed-demo failed: demo session was not readable after upsert.");
        }

        Console.WriteLine("Seed-demo OK: demo session upserted in service A's database only.");
        break;
}

return 0;
