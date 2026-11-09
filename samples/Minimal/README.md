# Minimal sample (`samples/Minimal`)

The smallest runnable OpenConnect EF Core host: console app, its own `MinimalDbContext`,
exactly one provider (SQLite file), `ApplyOpenConnectModel`, and this sample's own
migrations + history table. Owned by PERSIST-06-SAMPLE-BASE.

## Configuration (environment, no production credentials)

| Variable | Purpose | Default |
| --- | --- | --- |
| `OPENCONNECT_MINIMAL_SQLITE_PATH` | Absolute SQLite file path | `<appdir>/minimal-sample.db` |

No other configuration is required. The sample seeds no production data.

## Startup commands (from the repository root)

```powershell
# 1. Apply this sample's own migrations (idempotent; history: __MinimalMigrationsHistory)
dotnet run --project samples/Minimal/OpenConnect.Samples.Minimal.csproj -- migrate

# 2. Run: ensure schema, connect, report mapped tables
dotnet run --project samples/Minimal/OpenConnect.Samples.Minimal.csproj -- run

# 3. Verify: fail unless reachable with zero pending migrations
dotnet run --project samples/Minimal/OpenConnect.Samples.Minimal.csproj -- verify
```

## Migrate / verify steps

- `migrate` calls `Database.MigrateAsync()` through this sample's pipeline only
  (`MinimalDbContext`, `MigrationsAssembly` = this sample, dedicated history table).
- `verify` checks `CanConnect`, lists the twelve mapped `oc_*` tables, and fails when
  any migration is pending (`sample_verify_failed`, exit non-zero).
- Restart-idempotent: re-running `migrate` or `run` against an existing database file
  is a no-op for already-applied migrations.

## Ownership

- Own DbContext: `MinimalDbContext` (`MinimalDbContext.cs`).
- Own migrations: `Migrations/20261109000000_Initial.cs` (+ future `dotnet ef` output here).
- Own history: `__MinimalMigrationsHistory` in this sample's database file only.
- Design-time factory: `MinimalDbContextFactory` (supports `dotnet ef migrations add`).
- Receipt/command linkage (PERSIST gap G4): this sample exercises no receipt flows;
  receipt passthrough is **NOT VERIFIED** and no legacy receipt columns are invented.

## Proposed verification (verification lane owns execution — NOT RUN by this task)

```powershell
dotnet build samples/Minimal/OpenConnect.Samples.Minimal.csproj -c Release
dotnet run --project samples/Minimal/OpenConnect.Samples.Minimal.csproj -- migrate
dotnet run --project samples/Minimal/OpenConnect.Samples.Minimal.csproj -- verify
dotnet run --project samples/Minimal/OpenConnect.Samples.Minimal.csproj -- migrate  # restart-idempotent
dotnet run --project samples/Minimal/OpenConnect.Samples.Minimal.csproj -- verify
```
