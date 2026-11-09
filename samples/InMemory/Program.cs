using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Persistence.EFCore.Configuration;
using OpenConnect.Persistence.EFCore.DependencyInjection;
using OpenConnect.Samples.InMemory;
using OpenConnect.Samples.Shared;

var verb = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
if (verb is "migrate")
{
    Console.WriteLine("sample_inmemory_no_migrations: the InMemory provider has no migration pipeline and is NON-DURABLE.");
    Console.WriteLine("Use 'run' for a dev-only smoke check. No schema is created on disk.");
    return 2;
}

if (verb is not ("run" or "verify"))
{
    Console.WriteLine("Usage: OpenConnect.Samples.InMemory <run|verify>");
    Console.WriteLine("  run     : dev-only smoke check against a NON-DURABLE InMemory store. Seeds no data.");
    Console.WriteLine("  verify  : fail unless the model validates and the store is reachable in-process.");
    Console.WriteLine("  migrate : rejected by design (no migration pipeline for InMemory).");
    return 2;
}

var databaseName = SampleEnvironment.ResolveInMemoryName("OPENCONNECT_INMEMORY_DBNAME", "openconnect-inmemory-sample");

var builder = SampleHost.CreateBuilder(args);
builder.Services.AddDbContext<InMemorySampleDbContext>(options =>
    options.UseInMemoryDatabase(databaseName));
builder.Services.AddOpenConnectEntityFramework<InMemorySampleDbContext>(options =>
{
    // DurableSingleDatabase would fail closed here (ef_inmemory_not_durable).
    options.Migrations = OpenConnectMigrationMode.None;
    options.Consistency = OpenConnectPersistenceConsistency.DevelopmentOnly;
    options.ValidateSchema = OpenConnectSchemaValidationMode.ValidateModel;
    options.OutboxClaimStrategy = OpenConnectOutboxClaimStrategy.Auto;
});

using var host = builder.Build();
await host.StartAsync().ConfigureAwait(false);

using var scope = host.Services.CreateScope();
var db = scope.ServiceProvider.GetRequiredService<InMemorySampleDbContext>();

await db.Database.EnsureCreatedAsync().ConfigureAwait(false);
await SampleVerify.VerifyModelAsync(db).ConfigureAwait(false);
Console.WriteLine("InMemory check complete. WARNING: all state is NON-DURABLE and lost on process exit.");

return 0;
