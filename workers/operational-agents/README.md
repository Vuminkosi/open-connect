# OpenConnect.WorkerHosts.Operational

Standalone .NET Generic Host that supervises operational navigator runs
(workers-spec §6.10, WRK-REQ-102–107).

## What it does

- Holds the durable supervisor lease `oc/operational/supervisor` via `ILeaseStore`
  (EF-backed). The first host to acquire it supervises; a duplicate supervisor
  fails startup instead of fencing the same executions twice.
- Composes `OperationalNavigatorHandler` over durable execution leases
  (`DurableOperationalLeaseStore` on the public `INavigatorLeaseSeam`) and
  first-writer-wins completed receipts (`DurableIdempotencyStore` on the public
  `EfNavigatorReceiptStore`). No persistence internals are duplicated.
- Renews the supervisor lease on a heartbeat; losing it stops the host fail-closed.
- Releases the lease on graceful shutdown.

Plan, step-executor, and workload-identity providers are operator-registered in DI
(`IOperationalPlanProvider`, `IOperationalStepExecutor`,
`IOperationalWorkloadIdentityProvider`); resolving the handler without them fails
fast rather than executing placeholder logic.

## Configuration (`WorkerHosts:Operational`, env `WorkerHosts__Operational__*`)

| Key | Default | Notes |
| --- | --- | --- |
| `Mode` | `Standalone` | `OperationalAgentHostingMode`. `Embedded` refuses to supervise. |
| `EmbeddedEnabled` | `false` | Must be `false` in `Standalone`; startup fails otherwise. |
| `TenantUlid` | — | Required. Tenant ULID scoping the supervisor lease. |
| `DatabaseProvider` | `npgsql` | `npgsql` / `postgres` (same as gateway), `sqlserver`, or `sqlite`. Provider package is bundled because the host owns its `DbContext`. |
| `ConnectionString` | — | Required. Never logged. |
| `SupervisorLeaseDuration` | `00:00:30` | 5s–1h. |
| `RenewInterval` | `00:00:10` | Must be shorter than the lease. |

EF uses schema `openconnect` with `Migrations=None`-equivalent (`Manual`) and
model validation on startup (`EfStartupValidator`); migrations stay with the
database lane. No model volume exists: this host loads no detectors.

## Run

```powershell
$env:WorkerHosts__Operational__TenantUlid="<26-char ULID>"
$env:WorkerHosts__Operational__ConnectionString="Host=localhost;Port=5874;Database=openconnect;Username=openconnect;Password=..."
dotnet run --project workers/operational-agents
```

Or `docker compose -f workers/operational-agents/compose.yaml up --build`.
