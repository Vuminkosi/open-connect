---
title: EF Core persistence and outbox
version: v1
summary: Use provider-neutral OpenConnect persistence with sample-owned DbContexts and migrations.
order: 70
---

# EF Core persistence and outbox

`OpenConnect.Persistence.EFCore` provides provider-neutral mappings and persistence integration. The application owns its `DbContext`, provider choice and migrations.

```csharp
builder.Services.AddOpenConnectEntityFramework<ApplicationDbContext>(options =>
{
    options.Migrations = OpenConnectMigrationMode.Manual;
});
```

A durable task acceptance requires the task, event and outbox intent to commit atomically. Broker publication is asynchronous and external side effects must not be described as exactly once without provider evidence.

## Add an application-owned context

Install the persistence package and the EF Core provider for your database. The application context applies the OpenConnect mappings as well as its own mappings:

```csharp
using Microsoft.EntityFrameworkCore;
using OpenConnect.Persistence.EFCore.Model;

public sealed class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
    : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyOpenConnectModel();
    }
}
```

Register `AddDbContext<ApplicationDbContext>` with your provider before `AddOpenConnectEntityFramework<ApplicationDbContext>`. The latter wires the persistence services; it does not supply a connection string or own your migrations. Create and review migrations in the application, apply them explicitly to the intended database, then verify schema compatibility before starting workers. Back up existing data and test upgrades and rollback plans in a disposable environment.

```csharp
using OpenConnect.Persistence.EFCore.Configuration;
using OpenConnect.Persistence.EFCore.DependencyInjection;

// connectionString comes from protected application configuration.
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(connectionString));
builder.Services.AddOpenConnectEntityFramework<ApplicationDbContext>(options =>
{
    options.Migrations = OpenConnectMigrationMode.Manual;
    options.Consistency = OpenConnectPersistenceConsistency.DurableSingleDatabase;
});
```

Install the matching Npgsql EF Core provider for this example. Choose a different provider and migration set if your application uses SQLite or SQL Server.

## Try the source samples

From the repository root, the SQLite sample has its own context and migrations:

```powershell
dotnet run --project samples/Sqlite/OpenConnect.Samples.Sqlite.csproj -- migrate
dotnet run --project samples/Sqlite/OpenConnect.Samples.Sqlite.csproj -- verify
```

`OPENCONNECT_SQLITE_PATH` selects its database file. `migrate` changes that file; choose a disposable path for a trial. The PostgreSQL sample instead requires `OPENCONNECT_POSTGRESQL_CONNECTION` and provides `parity`, `migrate`, `run` and `verify` commands. The InMemory sample is process-local and loses data on exit; it is for development, not durability or multi-node lease guarantees. Inspect each sample's README for provider-specific options and migration history.

## Outbox and recovery

Durable acceptance and external publication are separate stages. Monitor pending, failed and retried outbox work; use idempotent consumers because a relay or network failure can lead to redelivery. A successful database commit does not prove the broker or an external provider accepted a message. Protect credentials and scope all data operations to the authenticated tenant. See [NATS and workers](/docs/v1/nats-and-workers).

## Related links

- [NATS and worker integration](/docs/v1/nats-and-workers)
- [Examples and sample projects](/docs/v1/examples)
