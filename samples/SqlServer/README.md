# SQL Server sample (`samples/SqlServer`)

SQL Server OpenConnect EF Core host proving hardened-adapter behavior on SQL Server:
its own `SqlServerSampleDbContext`, exactly one provider (SqlServer), default
`ApplyOpenConnectModel` mapping (schema `openconnect`, twelve tables), and this
sample's own `dotnet ef`-generated migration + dedicated history table in its own
database. Owned by PERSIST-08-SAMPLE-MSSQL.

## Configuration (environment, no production credentials)

| Variable | Purpose | Default |
| --- | --- | --- |
| `OPENCONNECT_SQLSERVER_CONNECTION` | SQL Server connection string for **this sample's own database** | *(none — required, fails closed when unset)* |

There is intentionally no default connection string: the sample never falls back to a
shared database. Point it at a disposable local database, e.g.:

```powershell
# Start a disposable local SQL Server (Docker) — test fixture only, no production data.
docker run --rm -d --name oc-ms-sample -e "ACCEPT_EULA=Y" -e "MSSQL_SA_PASSWORD=Change_Me_123!" -p 1433:1433 mcr.microsoft.com/mssql/server:2022-latest

$env:OPENCONNECT_SQLSERVER_CONNECTION = "Server=localhost,1433;Database=openconnect_ms_sample;User Id=sa;Password=Change_Me_123!;TrustServerCertificate=True"
```

The connection string is never logged. Failure output carries only stable codes
(`sample_sqlserver_no_connection`, `sample_sqlserver_unreachable`) plus the exception
type name — never host, user, or secret values.

## Startup commands (from the repository root)

```powershell
# 1. Apply this sample's own migration (idempotent; history: openconnect.__SqlServerMigrationsHistory)
dotnet run --project samples/SqlServer/OpenConnect.Samples.SqlServer.csproj -- migrate

# 2. Run: ensure schema, connect, report mapped tables
dotnet run --project samples/SqlServer/OpenConnect.Samples.SqlServer.csproj -- run

# 3. Verify: fail unless reachable with zero pending migrations
dotnet run --project samples/SqlServer/OpenConnect.Samples.SqlServer.csproj -- verify
```

## Migrate / verify steps

- `migrate` calls `Database.MigrateAsync()` through this sample's pipeline only
  (`SqlServerSampleDbContext`, `MigrationsAssembly` = this sample, dedicated history
  table in this sample's own database — never shared with Minimal/Sqlite/PostgreSql or
  the legacy `OpenConnect.Persistence.Postgres` pipeline).
- `verify` checks `CanConnect`, lists the twelve mapped tables, and fails when any
  migration is pending (`sample_verify_failed`, exit non-zero).
- Restart-idempotent: re-running `migrate` or `run` against an existing database is a
  no-op for already-applied migrations.
- Negative case: an unreachable or malformed connection string fails closed
  (`sample_sqlserver_unreachable`, exit 1, details withheld). Proposed check:
  `OPENCONNECT_SQLSERVER_CONNECTION="Server=invalid.invalid;Database=x;User Id=u;Password=p;TrustServerCertificate=True"`
  `dotnet run --project samples/SqlServer/OpenConnect.Samples.SqlServer.csproj -- verify`
  must exit non-zero without logging the connection string.

## Ownership

- Own DbContext: `SqlServerSampleDbContext` (`SqlServerSampleDbContext.cs`).
- Own migration: `Migrations/<timestamp>_Initial.cs` (`dotnet ef migrations add`
  output against the twelve `EntityConfiguration`s, with Designer/snapshot;
  verification lane validates via build + `dotnet ef migrations list` readout).
- Own history: `openconnect.__SqlServerMigrationsHistory` in this sample's database only.
- Own database: `OPENCONNECT_SQLSERVER_CONNECTION` target (never shared with other samples).
- Design-time factory: `SqlServerSampleDbContextFactory` (supports `dotnet ef` from
  this sample; offline localhost placeholder when the env var is unset).
- Not in `OpenConnect.slnx`: solution wiring is left to integration (this task is
  prohibited from touching `*.slnx`). Build/verify by project path.
- Receipt/command linkage (PERSIST gaps G1–G4 RETAIN / RETAIN+MIGRATE): this sample
  exercises no receipt flows and invents no receipt columns; receipt passthrough is
  **NOT VERIFIED**. Backfill planning is mandatory future work outside this slice.

## Proposed verification (verification lane owns execution — NOT RUN by this task)

```powershell
dotnet build samples/SqlServer/OpenConnect.Samples.SqlServer.csproj -c Release
dotnet run --project samples/SqlServer/OpenConnect.Samples.SqlServer.csproj -- migrate
dotnet run --project samples/SqlServer/OpenConnect.Samples.SqlServer.csproj -- verify
dotnet run --project samples/SqlServer/OpenConnect.Samples.SqlServer.csproj -- migrate  # restart-idempotent
dotnet run --project samples/SqlServer/OpenConnect.Samples.SqlServer.csproj -- verify
dotnet ef migrations list --project samples/SqlServer/OpenConnect.Samples.SqlServer.csproj --context SqlServerSampleDbContext
# negative (must fail closed, exit non-zero, no secret in output):
$env:OPENCONNECT_SQLSERVER_CONNECTION = "Server=invalid.invalid;Database=x;User Id=u;Password=p;TrustServerCertificate=True"
dotnet run --project samples/SqlServer/OpenConnect.Samples.SqlServer.csproj -- verify
```
