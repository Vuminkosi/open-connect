using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Abstractions;
using OpenConnect.Persistence.EFCore.Configuration;
using OpenConnect.Persistence.EFCore.DependencyInjection;
using OpenConnect.Samples.Navigator;
using OpenConnect.Samples.Shared;

var verb = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
if (verb is not ("migrate" or "lease-demo" or "outbox-relay-demo" or "verify"))
{
    Console.WriteLine("Usage: OpenConnect.Samples.Navigator <migrate|lease-demo|outbox-relay-demo|verify>");
    Console.WriteLine("  migrate           : ensure this sample's own schema (idempotent, no migrations).");
    Console.WriteLine("  lease-demo        : deterministic lease fencing scenario (acquire/conflict/renew/stale/release/expire-reclaim).");
    Console.WriteLine("  outbox-relay-demo : deterministic Navigator -> EFCore outbox relay scenario.");
    Console.WriteLine("  verify            : fail unless schema is reachable with zero pending migrations.");
    return 2;
}

var sqliteFile = SampleEnvironment.ResolveSqliteFile("OPENCONNECT_NAVIGATOR_SQLITE_PATH", "navigator-sample.db");

var builder = SampleHost.CreateBuilder(args);
builder.Services.AddDbContext<NavigatorDbContext>(options =>
    NavigatorDbContext.ConfigureSqlite(options, SampleEnvironment.ToSqliteConnectionString(sqliteFile)));
builder.Services.AddOpenConnectEntityFramework<NavigatorDbContext>(options =>
{
    options.Migrations = OpenConnectMigrationMode.Manual;
    options.Consistency = OpenConnectPersistenceConsistency.DurableSingleDatabase;
    options.ValidateSchema = OpenConnectSchemaValidationMode.ValidateModel;
    options.OutboxClaimStrategy = OpenConnectOutboxClaimStrategy.Auto;
});

using var host = builder.Build();
await host.StartAsync().ConfigureAwait(false);

using var scope = host.Services.CreateScope();
var db = scope.ServiceProvider.GetRequiredService<NavigatorDbContext>();

try
{
    switch (verb)
    {
        case "migrate":
            await db.Database.EnsureCreatedAsync().ConfigureAwait(false);
            Console.WriteLine("Schema ensured (EnsureCreated; this sample owns no migrations).");
            break;
        case "lease-demo":
            await LeaseDemo.RunAsync(db).ConfigureAwait(false);
            break;
        case "outbox-relay-demo":
            var outbox = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
            await OutboxRelayDemo.RunAsync(db, outbox).ConfigureAwait(false);
            break;
        case "verify":
            await SampleVerify.VerifyModelAsync(db).ConfigureAwait(false);
            var applied = await SampleVerify.VerifyMigrationsAsync(db).ConfigureAwait(false);
            Console.WriteLine($"Verify OK: {applied} migration(s) applied, none pending.");
            break;
    }
}
catch (InvalidOperationException ex) when (ex.Message.StartsWith("navigator_", StringComparison.Ordinal)
    || ex.Message.StartsWith("sample_verify_failed", StringComparison.Ordinal))
{
    Console.WriteLine(ex.Message);
    return 1;
}

return 0;
