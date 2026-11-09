# McpMicroservices sample (`samples/McpMicroservices`)

Two minimal microservices (A and B) demonstrating multi-service EF Core use with
per-service migrations and cross-service isolation. Each service owns its
`DbContext`, exactly one provider (SQLite file), its own database file, and its
own migrations history table — no shared database and no shared migration history.
Owned by PERSIST-11-SAMPLE-MCP.

Both services map the identical OpenConnect model (`ApplyOpenConnectModel` with an
explicit `oc_` table prefix), so isolation comes from separate database files plus
tenant-scoped store reads — not from different table names. Only one
`OpenConnect` EF context may be registered per host process
(`ef_ambiguous_registration`); each service therefore runs as its own process,
which is also what makes the isolation demo meaningful.

## Services

| Service | DbContext | Database file | History table |
| --- | --- | --- | --- |
| A | `ServiceADbContext` | `microservice-a.db` | `__ServiceAMcpMigrationsHistory` |
| B | `ServiceBDbContext` | `microservice-b.db` | `__ServiceBMcpMigrationsHistory` |

## Configuration (environment, no production credentials)

| Variable | Purpose | Default |
| --- | --- | --- |
| `OPENCONNECT_MICROSERVICE_A_SQLITE_PATH` | Absolute SQLite file path for service A | `<appdir>/microservice-a.db` |
| `OPENCONNECT_MICROSERVICE_B_SQLITE_PATH` | Absolute SQLite file path for service B | `<appdir>/microservice-b.db` |

No other configuration is required. The `run`/`verify` verbs seed no data; only the
explicit `seed-demo` verb writes the single fixed demo session (synthetic ULIDs,
no production meaning) into service A's database.

## Startup commands (from the repository root)

```powershell
# 1. Apply each service's own migrations (idempotent; separate histories)
dotnet run --project samples/McpMicroservices/ServiceA/OpenConnect.Samples.McpMicroservices.ServiceA.csproj -- migrate
dotnet run --project samples/McpMicroservices/ServiceB/OpenConnect.Samples.McpMicroservices.ServiceB.csproj -- migrate

# 2. Run: ensure schema, connect, report mapped tables
dotnet run --project samples/McpMicroservices/ServiceA/OpenConnect.Samples.McpMicroservices.ServiceA.csproj -- run
dotnet run --project samples/McpMicroservices/ServiceB/OpenConnect.Samples.McpMicroservices.ServiceB.csproj -- run

# 3. Verify: fail unless reachable with zero pending migrations
dotnet run --project samples/McpMicroservices/ServiceA/OpenConnect.Samples.McpMicroservices.ServiceA.csproj -- verify
dotnet run --project samples/McpMicroservices/ServiceB/OpenConnect.Samples.McpMicroservices.ServiceB.csproj -- verify
```

## Cross-service isolation demo (deterministic; no timers or races)

```powershell
# 1. Seed the fixed demo session into service A only (idempotent upsert)
dotnet run --project samples/McpMicroservices/ServiceA/OpenConnect.Samples.McpMicroservices.ServiceA.csproj -- seed-demo

# 2. Prove the tenant-scoped demo session is invisible to service B (exit 0 = PASS, exit 1 = leak)
dotnet run --project samples/McpMicroservices/ServiceB/OpenConnect.Samples.McpMicroservices.ServiceB.csproj -- check-isolation
```

`check-isolation` performs a tenant-scoped `ISessionStore.GetAsync` (session +
tenant + principal) against service B's database only — it never opens service
A's file — and fails closed (non-zero exit) if the demo row is visible. Run the
commands sequentially; there is no background coordination to race.

## Migrations

Per-service migrations are owned by each service and are created exclusively via
`dotnet ef migrations add` (hand-authored migrations prohibited). Each service's
`Migrations/` directory is checked in (CLI-generated and present — Initial
20261109061727 for service A, 20261109061735 for service B), generated with:

```powershell
dotnet ef migrations add Initial --context ServiceADbContext --project samples/McpMicroservices/ServiceA/OpenConnect.Samples.McpMicroservices.ServiceA.csproj --startup-project samples/McpMicroservices/ServiceA/OpenConnect.Samples.McpMicroservices.ServiceA.csproj --output-dir Migrations
dotnet ef migrations add Initial --context ServiceBDbContext --project samples/McpMicroservices/ServiceB/OpenConnect.Samples.McpMicroservices.ServiceB.csproj --startup-project samples/McpMicroservices/ServiceB/OpenConnect.Samples.McpMicroservices.ServiceB.csproj --output-dir Migrations
```

Each service carries a `Microsoft.EntityFrameworkCore.Design` reference solely to
support its own `dotnet ef` pipeline. The solution file (`slnx`) is intentionally
untouched (outside owned paths); invoke every command with an explicit `--project`
path as shown above.

## Ownership

- Own DbContexts: `ServiceA/ServiceADbContext.cs`, `ServiceB/ServiceBDbContext.cs`.
- Own migrations: each service's `Migrations/` (CLI-generated and present:
  Initial 20261109061727 for service A, 20261109061735 for service B) +
  dedicated history table in that service's database file only.
- Own databases: `microservice-a.db` / `microservice-b.db` (separate files; never shared).
- Design-time factories: `ServiceADbContextFactory`, `ServiceBDbContextFactory`.
- Demo keys: `ServiceA/ServiceADemoKeys.cs`, `ServiceB/ServiceBDemoKeys.cs`
  (intentionally duplicated owned copies; keep in sync; synthetic ULIDs only).
- Gap dispositions (PERSIST gap design): G1–G4 all **RETAIN** — both services map
  the four EFCore-only tables (`OperationalRuns`, `NativeAgentSessions`,
  `PublicSessionMappings`, `ApprovalNonces`) via `ApplyOpenConnectModel`. G4
  backfill is future work: no backfill SQL here. Receipt carrier is UNDECIDED:
  no receipt linkage or receipt columns are invented in this sample.

## Proposed verification (verification lane owns execution — NOT RUN by this task)

```powershell
dotnet build samples/McpMicroservices/ServiceA/OpenConnect.Samples.McpMicroservices.ServiceA.csproj -c Release
dotnet build samples/McpMicroservices/ServiceB/OpenConnect.Samples.McpMicroservices.ServiceB.csproj -c Release
dotnet run --project samples/McpMicroservices/ServiceA/OpenConnect.Samples.McpMicroservices.ServiceA.csproj -- migrate
dotnet run --project samples/McpMicroservices/ServiceB/OpenConnect.Samples.McpMicroservices.ServiceB.csproj -- migrate
dotnet run --project samples/McpMicroservices/ServiceA/OpenConnect.Samples.McpMicroservices.ServiceA.csproj -- verify
dotnet run --project samples/McpMicroservices/ServiceB/OpenConnect.Samples.McpMicroservices.ServiceB.csproj -- verify
dotnet run --project samples/McpMicroservices/ServiceA/OpenConnect.Samples.McpMicroservices.ServiceA.csproj -- seed-demo
dotnet run --project samples/McpMicroservices/ServiceB/OpenConnect.Samples.McpMicroservices.ServiceB.csproj -- check-isolation
dotnet run --project samples/McpMicroservices/ServiceA/OpenConnect.Samples.McpMicroservices.ServiceA.csproj -- migrate  # restart-idempotent
dotnet run --project samples/McpMicroservices/ServiceA/OpenConnect.Samples.McpMicroservices.ServiceA.csproj -- verify
```

## Residual risks (NOT VERIFIED)

- No build, run, test, or `dotnet ef` execution was performed by this task; all
  runtime behavior (migrate/run/verify/seed-demo/check-isolation, migration SQL
  content, history-table separation, isolation PASS) is **NOT VERIFIED**.
- Per-service `Migrations/` are present (Initial 20261109061727 for service A,
  20261109061735 for service B); `migrate`/`verify`/`seed-demo` ordering above
  remains required but is unexecuted by this task.
- `seed-demo` before migrations exist fails closed (`no such table`); required
  ordering is documented above but unexecuted.
- Tenant/lease/outbox contention, provider translation, and net9/net10 support are
  **NOT VERIFIED** (same experimental scope as OC-PERSIST-001 §5).
