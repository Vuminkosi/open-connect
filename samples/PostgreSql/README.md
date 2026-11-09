# PostgreSQL sample (`samples/PostgreSql`)

PostgreSQL OpenConnect EF Core host proving legacy 8-table + 3-migration behavior parity
on the hardened adapter: its own `PostgreSqlSampleDbContext`, exactly one provider
(Npgsql), default `ApplyOpenConnectModel` mapping (schema `openconnect`, twelve tables),
and this sample's own `dotnet ef`-generated migration + dedicated history table in its own
database. Owned by PERSIST-07-SAMPLE-PG.

## Configuration (environment, no production credentials)

| Variable | Purpose | Default |
| --- | --- | --- |
| `OPENCONNECT_POSTGRESQL_CONNECTION` | Npgsql connection string for **this sample's own database** | *(none — required, fails closed when unset)* |

There is intentionally no default connection string: the sample never falls back to a
shared database. Point it at a disposable local database, e.g.:

```powershell
# Start a disposable local PostgreSQL (Docker) — test fixture only, no production data.
docker run --rm -d --name oc-pg-sample -e POSTGRES_PASSWORD=CHANGE_ME -e POSTGRES_DB=openconnect_pg_sample -p 5432:5432 postgres:16

$env:OPENCONNECT_POSTGRESQL_CONNECTION = "Host=localhost;Port=5432;Database=openconnect_pg_sample;Username=postgres;Password=CHANGE_ME"
```

The connection string is never logged. Failure output carries only stable codes
(`sample_postgres_no_connection`, `sample_postgres_unreachable`) plus the exception
type name — never host, user, or secret values.

## Startup commands (from the repository root)

```powershell
# 0. Offline parity check: no connection, no seeding (safe anywhere)
dotnet run --project samples/PostgreSql/OpenConnect.Samples.PostgreSql.csproj -- parity

# 1. Apply this sample's own migration (idempotent; history: openconnect.__PostgreSqlMigrationsHistory)
dotnet run --project samples/PostgreSql/OpenConnect.Samples.PostgreSql.csproj -- migrate

# 2. Run: ensure schema, connect, report mapped tables
dotnet run --project samples/PostgreSql/OpenConnect.Samples.PostgreSql.csproj -- run

# 3. Verify: fail unless reachable with zero pending migrations
dotnet run --project samples/PostgreSql/OpenConnect.Samples.PostgreSql.csproj -- verify
```

## Migrate / verify steps

- `parity` inspects the EF Core model only (no connection opened, no data written) and
  asserts the parity matrix below: 8 retained legacy tables + 4 hardened-only tables,
  all in schema `openconnect`. Mismatch exits non-zero (`sample_parity_failed`).
- `migrate` calls `Database.MigrateAsync()` through this sample's pipeline only
  (`PostgreSqlSampleDbContext`, `MigrationsAssembly` = this sample, dedicated history
  table in this sample's own database — never shared with Minimal/Sqlite/InMemory or
  the legacy `OpenConnect.Persistence.Postgres` pipeline).
- `verify` checks `CanConnect`, lists the twelve mapped tables, and fails when any
  migration is pending (`sample_verify_failed`, exit non-zero).
- Restart-idempotent: re-running `migrate` or `run` against an existing database is a
  no-op for already-applied migrations.
- Negative case: an unreachable or malformed connection string fails closed
  (`sample_postgres_unreachable`, exit 1, details withheld). Proposed check:
  `OPENCONNECT_POSTGRESQL_CONNECTION="Host=invalid.invalid;Port=5432;Database=x;Username=u;Password=p"`
  `dotnet run --project samples/PostgreSql/OpenConnect.Samples.PostgreSql.csproj -- verify`
  must exit non-zero without logging the connection string.

## Parity matrix (legacy → hardened)

The legacy `OpenConnect.Persistence.Postgres` pipeline ships 8 tables across 3
migrations (`InitialCreate`, `PendingModelSync`, `NavigatorDurability`). This sample
replays that behavior on the hardened adapter as follows:

| Legacy table (3-migration pipeline) | Hardened table (this sample, `openconnect` schema) | Disposition |
| --- | --- | --- |
| `oc_sessions` | `Sessions` | RETAIN — id, tenant/principal scopes, concurrency token, expiry index |
| `oc_tasks` | `Tasks` | RETAIN — idempotency unique `(TenantScope, PrincipalScope, ClientTaskId)` |
| `oc_turns` | `Turns` | RETAIN — unique `(SessionId, Sequence)` |
| `oc_events` | `Events` | RETAIN — unique `(TenantScope, SessionId, Sequence)` |
| `oc_outbox` | `Outbox` | RETAIN — claim columns (`Status`, `FencingVersion`, `LeaseOwner`), unique `(TenantScope, EventId)` |
| `oc_leases` | `Leases` | RETAIN — unique `(TenantScope, ResourceKey)`, fencing token |
| `oc_approvals` | `Approvals` | RETAIN — `(TenantScope, TaskId)` lookup |
| `oc_privacy_tokens` | `PrivacyTokens` | RETAIN — unique token binding + expiry index |
| — | `OperationalRuns` | Hardened-only (new) |
| — | `NativeAgentSessions` | Hardened-only (new) |
| — | `PublicSessionMappings` | Hardened-only (new) |
| — | `ApprovalNonces` | Hardened-only (new) |

Table names differ by design (legacy `oc_*` snake_case vs hardened schema-qualified
PascalCase); parity is behavioral (constraints, claim/fencing columns, tenant scoping),
asserted offline by `parity` and live by `verify`. Live fixture replay of legacy
behaviors (idempotent insert conflict, outbox claim race, lease fencing) is deferred to
integration tests with Testcontainers (see proposed verification) — this sample seeds no
data: `migrate`/`run`/`verify`/`parity` write no rows beyond the migrations history.

## Ownership

- Own DbContext: `PostgreSqlSampleDbContext` (`PostgreSqlSampleDbContext.cs`).
- Own parity probe: `SampleParity` (`SampleParity.cs`, offline model inspection only).
- Own migration: `Migrations/20261109052852_Initial.cs` (`dotnet ef migrations add`
  output against the twelve `EntityConfiguration`s, with Designer/snapshot;
  verification lane validates via build + `dotnet ef migrations list` readout).
- Own history: `openconnect.__PostgreSqlMigrationsHistory` in this sample's database only.
- Own database: `OPENCONNECT_POSTGRESQL_CONNECTION` target (never shared with other samples).
- Design-time factory: `PostgreSqlSampleDbContextFactory` (supports `dotnet ef` from
  this sample; offline localhost placeholder when the env var is unset).
- Not in `OpenConnect.slnx`: solution wiring is left to integration (this task is
  prohibited from touching `*.slnx`). Build/verify by project path.
- Receipt/command linkage (PERSIST gaps G1–G4 RETAIN / RETAIN+MIGRATE): this sample
  exercises no receipt flows and invents no receipt columns; receipt passthrough is
  **NOT VERIFIED**. Backfill planning is mandatory future work outside this slice.

## Proposed verification (verification lane owns execution — NOT RUN by this task)

```powershell
dotnet build samples/PostgreSql/OpenConnect.Samples.PostgreSql.csproj -c Release
dotnet run --project samples/PostgreSql/OpenConnect.Samples.PostgreSql.csproj -- parity
$env:OPENCONNECT_POSTGRESQL_CONNECTION = "Host=localhost;Port=5432;Database=openconnect_pg_sample;Username=postgres;Password=CHANGE_ME"
dotnet run --project samples/PostgreSql/OpenConnect.Samples.PostgreSql.csproj -- migrate
dotnet run --project samples/PostgreSql/OpenConnect.Samples.PostgreSql.csproj -- verify
dotnet run --project samples/PostgreSql/OpenConnect.Samples.PostgreSql.csproj -- migrate  # restart-idempotent
dotnet run --project samples/PostgreSql/OpenConnect.Samples.PostgreSql.csproj -- verify
dotnet ef migrations list --project samples/PostgreSql/OpenConnect.Samples.PostgreSql.csproj --context PostgreSqlSampleDbContext
# negative (must fail closed, exit non-zero, no secret in output):
$env:OPENCONNECT_POSTGRESQL_CONNECTION = "Host=invalid.invalid;Port=5432;Database=x;Username=u;Password=p"
dotnet run --project samples/PostgreSql/OpenConnect.Samples.PostgreSql.csproj -- verify
# live fixture replay (Testcontainers PostgreSQL): idempotent task insert conflict,
# outbox claim race, lease fencing conflict — deferred to integration tests.
```
