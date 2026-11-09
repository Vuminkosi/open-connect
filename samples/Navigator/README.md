# Navigator sample (`samples/Navigator`)

Runnable proof of lease fencing (acquire / conflict / expire / renew / release) plus a
Navigator-to-EFCore outbox relay, against the hardened EF Core lease seam
(`EfNavigatorLeaseSeam`). Owned by PERSIST-10-SAMPLE-NAV.

## What this proves

- **Lease fencing**: the fencing token is monotonic per execution across
  acquire → conflict → renew → stale-token reject → release → re-acquire → expire/reclaim,
  with fail-closed `null`/`false` on invalid input (no exceptions for semantic failures).
- **Outbox relay**: safe Navigator events flow into the EFCore outbox as pending entries;
  unsafe events are skipped by the privacy gate and never touch outbox payloads.

## Configuration (environment, no production credentials)

| Variable | Purpose | Default |
| --- | --- | --- |
| `OPENCONNECT_NAVIGATOR_SQLITE_PATH` | Absolute SQLite file path | `<appdir>/navigator-sample.db` |

No other configuration is required. The sample seeds no production data. The fixed tenant
GUID (`11111111-...`) and relay tenant/principal strings are synthetic demo constants.

## Startup commands (from the repository root)

```powershell
# 1. Ensure this sample's own schema (idempotent; owns no migrations — see below)
dotnet run --project samples/Navigator/OpenConnect.Samples.Navigator.csproj -- migrate

# 2. Lease fencing scenario (deterministic; exit non-zero on first failed assertion)
dotnet run --project samples/Navigator/OpenConnect.Samples.Navigator.csproj -- lease-demo

# 3. Outbox relay scenario (deterministic; exit non-zero on first failed assertion)
dotnet run --project samples/Navigator/OpenConnect.Samples.Navigator.csproj -- outbox-relay-demo

# 4. Verify: fail unless reachable with zero pending migrations
dotnet run --project samples/Navigator/OpenConnect.Samples.Navigator.csproj -- verify
```

Demo verbs are rerun-safe (lease execution keys are unique per invocation; relay
assertions filter by the fresh session id) and contain no timers, sleeps, or background
races: every clock value is derived arithmetically from one captured instant.

## Migrations: none, by contract (explicit statement)

This sample ships **zero migrations** because its adapters need **no tables beyond the
EFCore model**: the lease adapter persists only through the shared `oc_leases` table and
the relay only through the shared `oc_outbox` table. Schema for this sample's own SQLite
file is created with `EnsureCreatedAsync`; no new or shared tables are introduced, and no
receipt tables/columns exist anywhere in this slice (receipt carrier UNDECIDED).

## Ownership

- Own DbContext: `NavigatorDbContext` (`NavigatorDbContext.cs`; no migrations assembly,
  no history table — there are no migrations to track).
- Own migrations: **none** (see above).
- Lease adapter: `NavigatorLeaseAdapter.cs` — lease surface only. No
  `ConsumeApprovalAndSaveReceipt` / `GetReceipt` / `TrySaveReceipt` members, no receipt
  wiring (contract: receipt paths MUST NOT be implemented in this slice).
- Outbox-relay adapter: `NavigatorOutboxRelay.cs` (+ `OutboxRelayDemo.cs`) — bridges
  `IDurableNavigatorStore` events to `IOutboxStore`; `IsSafe` gate enforced.
- Raw SQL: **none anywhere in the sample**. All persistence flows through
  `EfNavigatorLeaseSeam` (LINQ + `SaveChanges`) and the `IOutboxStore` /
  `IDurableNavigatorStore` interfaces. Grep gate before review:
  `FromSql|ExecuteSql|SqlQuery` must return zero hits under `samples/Navigator/`.

## Proposed verification (verification lane owns execution — NOT RUN by this task)

```powershell
dotnet build samples/Navigator/OpenConnect.Samples.Navigator.csproj -c Release
# grep gate: no raw SQL in the sample
dotnet run --project samples/Navigator/OpenConnect.Samples.Navigator.csproj -- migrate
dotnet run --project samples/Navigator/OpenConnect.Samples.Navigator.csproj -- lease-demo
dotnet run --project samples/Navigator/OpenConnect.Samples.Navigator.csproj -- lease-demo  # rerun-safe
dotnet run --project samples/Navigator/OpenConnect.Samples.Navigator.csproj -- outbox-relay-demo
dotnet run --project samples/Navigator/OpenConnect.Samples.Navigator.csproj -- outbox-relay-demo  # rerun-safe
dotnet run --project samples/Navigator/OpenConnect.Samples.Navigator.csproj -- verify
```
