# OpenConnect.WorkerHosts.Outbox

Standalone .NET Generic Host that relays committed outbox entries to JetStream
(workers-spec §6.11, WRK-REQ-108–112). Uses the EF `EfOutboxDispatcher` claim/retry engine and the existing
`NatsPublisher` envelope validator. The legacy `OutboxStoreRelaySource` adapter
is not used by this host. Its legacy interface cannot persist attempts, backoff,
or quarantine; the compatibility relay processes later batch entries before
rethrowing the first failure, but durable delivery requires the EF dispatch path.

## Dispatch contract

Per batch, in order:

1. **Acquire/renew fence** — renew the durable supervisor lease `oc/outbox/supervisor`
   (`ILeaseStore`, EF-backed). Exactly one dispatcher is active across replicas;
   a duplicate fails startup and a lost lease stops the host fail-closed.
   A heartbeat renews every third of the lease duration using independent scopes
   and database contexts. Each publish rechecks current supervisor ownership.
   The current expiry remains a cancellation deadline until renewal is confirmed;
   ownership loss or a failed renewal cancels the active broker call without
   consuming a poison retry. Shutdown awaits dispatch/heartbeat before releasing
   the supervisor lease; if the shutdown deadline expires first, the lease is
   left to expire rather than released under an unfinished broker call.
   Configure `PollingInterval` shorter than the supervisor lease duration;
   otherwise ownership may expire between batches and the host stops fail-closed.
2. **Claim and publish** — `OutboxBatchDispatcher` claims due entries with a
   per-row owner, expiry and fencing token, validates each envelope
   (persisted event id must match envelope message id), and publishes via `NatsPublisher`
   with the JetStream `Nats-Msg-Id` header set to the envelope message id.
3. **PUBACK** — `NatsJetStreamPublisher` awaits the JetStream publish
   acknowledgement (`EnsureSuccess`); a NACK/timeout throws and the entry stays
   pending with durable exponential backoff, then quarantined at `MaxAttempts`.
   A failing entry never aborts publication of later valid entries in the batch.
4. **CAS mark delivered** — the entry is marked delivered only after PUBACK and a matching, unexpired
   row lease/fencing token, in a
   short independent `SaveChanges`. No database transaction is ever held across
   the broker call. Both completion and failure updates are fenced. A crash after PUBACK can
   cause redelivery: delivery is at-least-once, and JetStream deduplication is
   limited to its configured duplicate window; consumers must remain idempotent.
   Row claims last at most `SupervisorLeaseDuration` and are not extended by the
   supervisor heartbeat. A slow publish can outlive its row claim, so it cannot
   complete that claim and may be redelivered; this is not exactly-once delivery.
   Cancellation propagates without consuming a retry attempt.

Failures are logged using exception type only, never exception messages or
payload contents. Quarantined entries are excluded from future polls; inspection
and any replay require an explicit operator action outside this host.

## Configuration (`WorkerHosts:Outbox`, env `WorkerHosts__Outbox__*`)

| Key | Default | Notes |
| --- | --- | --- |
| `Mode` | `Standalone` | `OutboxHostingMode`. `Embedded` refuses to dispatch. |
| `EmbeddedDispatcherEnabled` | `false` | Must be `false` in `Standalone`; startup fails otherwise. |
| `TenantUlid` | — | Required. Tenant ULID scoping the supervisor lease. |
| `DatabaseProvider` | `npgsql` | `npgsql` / `postgres` (same as gateway), `sqlserver`, or `sqlite`. Provider package is bundled because the host owns its `DbContext`. |
| `ConnectionString` | — | Required. Never logged. |
| `SupervisorLeaseDuration` | `00:00:30` | 5s–1h. |
| `PollingInterval` | `00:00:02` | 100ms–5min. Only due pending entries are claimed. |
| `BatchSize` | `50` | 1–1000. |
| `MaxAttempts` | `5` | 1–100. Failed attempts back off exponentially (2–256 seconds); reaching the limit quarantines the row. |
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
