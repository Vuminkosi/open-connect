# OpenConnect.WorkerHosts.Outbox

Standalone .NET Generic Host that relays committed outbox entries to JetStream
(workers-spec §6.11, WRK-REQ-108–112). Reuses `OutboxRelay`,
`OutboxStoreRelaySource`, and `NatsPublisher` from `OpenConnect.Transport.Nats`;
never duplicates them.

## Dispatch contract

Per batch, in order:

1. **Acquire/renew fence** — renew the durable supervisor lease `oc/outbox/supervisor`
   (`ILeaseStore`, EF-backed). Exactly one dispatcher is active across replicas;
   a duplicate fails startup and a lost lease stops the host fail-closed.
2. **Publish** — `OutboxRelay` reads pending entries, validates each envelope
   (entry id must match envelope message id), and publishes via `NatsPublisher`
   with the JetStream `Nats-Msg-Id` header set to the envelope message id.
3. **PUBACK** — `NatsJetStreamPublisher` awaits the JetStream publish
   acknowledgement (`EnsureSuccess`); a NACK/timeout throws and the entry stays
   pending for redrive.
4. **CAS mark delivered** — the entry is marked delivered only after PUBACK, in a
   short independent `SaveChanges`. No database transaction is ever held across
   the broker call. Crash-redelivery is idempotent via message-id dedup.

## Configuration (`WorkerHosts:Outbox`, env `WorkerHosts__Outbox__*`)

| Key | Default | Notes |
| --- | --- | --- |
| `Mode` | `Standalone` | `OutboxHostingMode`. `Embedded` refuses to dispatch. |
| `EmbeddedDispatcherEnabled` | `false` | Must be `false` in `Standalone`; startup fails otherwise. |
| `TenantUlid` | — | Required. Tenant ULID scoping the supervisor lease. |
| `DatabaseProvider` | `npgsql` | `npgsql` / `postgres` (same as gateway), `sqlserver`, or `sqlite`. Provider package is bundled because the host owns its `DbContext`. |
| `ConnectionString` | — | Required. Never logged. |
| `SupervisorLeaseDuration` | `00:00:30` | 5s–1h. |
| `PollingInterval` | `00:00:02` | 100ms–5min. Failures keep entries pending; the next poll redrives. |
| `BatchSize` | `50` | 1–1000. |
| `NatsUrl` | `nats://localhost:4222` | Absolute NATS server URL (publisher env). |
| `Subject` | `oc.v1.notifications.inapp` | JetStream publish subject (publisher env). |

EF uses schema `openconnect` with `Manual` migrations and model validation on
startup; migrations stay with the database lane. No model volume exists: this
host only relays envelopes.

## Run

```powershell
$env:WorkerHosts__Outbox__TenantUlid="<26-char ULID>"
$env:WorkerHosts__Outbox__ConnectionString="Host=localhost;Port=5874;Database=openconnect;Username=openconnect;Password=..."
$env:WorkerHosts__Outbox__NatsUrl="nats://localhost:4332"
dotnet run --project src/OpenConnect.WorkerHosts.Outbox
```

Or `docker compose -f src/OpenConnect.WorkerHosts.Outbox/compose.yaml up --build`.
