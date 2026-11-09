# SQLite sample (`samples/Sqlite`)

SQLite-file OpenConnect EF Core host demonstrating explicit options: its own
`SqliteSampleDbContext`, exactly one provider (SQLite file), `ApplyOpenConnectModel`
with an explicit `oc_` table prefix, and this sample's own migrations + history table.
Owned by PERSIST-06-SAMPLE-BASE.

## Configuration (environment, no production credentials)

| Variable | Purpose | Default |
| --- | --- | --- |
| `OPENCONNECT_SQLITE_PATH` | Absolute SQLite file path | `<appdir>/sqlite-sample.db` |

No other configuration is required. The sample seeds no production data.

## Startup commands (from the repository root)

```powershell
# 1. Apply this sample's own migrations (idempotent; history: __SqliteMigrationsHistory)
dotnet run --project samples/Sqlite/OpenConnect.Samples.Sqlite.csproj -- migrate

# 2. Run: ensure schema, connect, report mapped tables
dotnet run --project samples/Sqlite/OpenConnect.Samples.Sqlite.csproj -- run

# 3. Verify: fail unless reachable with zero pending migrations
dotnet run --project samples/Sqlite/OpenConnect.Samples.Sqlite.csproj -- verify
```

## Migrate / verify steps

- `migrate` calls `Database.MigrateAsync()` through this sample's pipeline only
  (`SqliteSampleDbContext`, `MigrationsAssembly` = this sample, dedicated history table
  in this sample's database file — never shared with Minimal).
- `verify` checks `CanConnect`, lists the twelve mapped `oc_*` tables, and fails when
  any migration is pending (`sample_verify_failed`, exit non-zero).
- Restart-idempotent: re-running `migrate` or `run` against an existing database file
  is a no-op for already-applied migrations.

## Ownership

- Own DbContext: `SqliteSampleDbContext` (`SqliteSampleDbContext.cs`).
- Own migrations: `Migrations/20261109052808_Initial.cs` (+ `dotnet ef` Designer/snapshot here).
- Own history: `__SqliteMigrationsHistory` in this sample's database file only.
- Design-time factory: `SqliteSampleDbContextFactory` (supports `dotnet ef migrations add`).
- Receipt/command linkage (PERSIST gap G4): this sample exercises no receipt flows;
  receipt passthrough is **NOT VERIFIED** and no legacy receipt columns are invented.

## Proposed verification (verification lane owns execution — NOT RUN by this task)

```powershell
dotnet build samples/Sqlite/OpenConnect.Samples.Sqlite.csproj -c Release
dotnet run --project samples/Sqlite/OpenConnect.Samples.Sqlite.csproj -- migrate
dotnet run --project samples/Sqlite/OpenConnect.Samples.Sqlite.csproj -- verify
dotnet run --project samples/Sqlite/OpenConnect.Samples.Sqlite.csproj -- migrate  # restart-idempotent
dotnet run --project samples/Sqlite/OpenConnect.Samples.Sqlite.csproj -- verify
```
