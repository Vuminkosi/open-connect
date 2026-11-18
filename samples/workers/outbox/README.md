# Worker Sample: `outbox`

End-to-end sample and verification harness for the standalone **Outbox Relay** worker host (`src/OpenConnect.WorkerHosts.Outbox/Dockerfile` and `workers/outbox/Dockerfile`), covering specification **§5.11 (`WSE2E-IMP-020`, `WSE2E-AT-073..080`)**.

---

## 1. Canonical Production Image & Build Command

- **Canonical Dockerfile**: `src/OpenConnect.WorkerHosts.Outbox/Dockerfile` (mirrored at `workers/outbox/Dockerfile`)
- **Canonical Image Name**: `vuminkosimatibe/openconnect-worker-outbox:e2e-local`
- **Model Volume / Provisioner**: **None.** The outbox relay handles relational transaction outbox dispatching to NATS JetStream with PUBACK-gated marking, lease fencing, and retry backoff. No ML models or provisioner service exist (`WSE2E-AT-073`).

Build the production image locally from the repository root:

```bash
docker build \
  -f src/OpenConnect.WorkerHosts.Outbox/Dockerfile \
  -t vuminkosimatibe/openconnect-worker-outbox:e2e-local \
  .
```

---

## 2. Docker Compose Invocation

Start the sample stack (`postgres` + `nats` + `outbox` dispatcher on the isolated `oc-test-private` network):

```bash
docker compose -f samples/workers/outbox/compose.e2e.yaml up -d --build
```

Execute verification across tiers:

```bash
# T0 static validation + T1 offline contract & .NET test suite
sh samples/workers/outbox/tests/t1_contract.sh

# T3 runtime outbox relay proof (live PostgreSQL, NATS JetStream, PUBACK delivery, deduplication)
sh samples/workers/outbox/tests/t3_runtime.sh
```

---

## 3. Environment Variables, Secrets & Database Configuration

| Variable | Default in `compose.e2e.yaml` | Purpose |
|---|---|---|
| `WorkerHosts__Outbox__Mode` | `Standalone` | Required hosting mode. Fails closed if embedded dispatcher is also active (`outbox_standalone_requires_embedded_disabled`). |
| `WorkerHosts__Outbox__EmbeddedDispatcherEnabled` | `false` | Must be `false` when running as standalone dispatcher. |
| `WorkerHosts__Outbox__TenantUlid` | `01ARZ3NDEKTSV4RRFFQ69G5FAV` | 26-character Crockford Base32 tenant ULID scoping the dispatcher lease row. |
| `WorkerHosts__Outbox__DatabaseProvider` | `npgsql` | Relational provider (`npgsql`, `sqlite`, or `sqlserver`). |
| `WorkerHosts__Outbox__ConnectionString` | `Host=postgres;Port=5432;Database=openconnect;Username=openconnect;Password=...` | Database connection string. Test password uses safe `do-not-use-in-prod` suffix. |
| `WorkerHosts__Outbox__NatsUrl` | `nats://nats:4222` | NATS JetStream broker URL. |
| `WorkerHosts__Outbox__Subject` | `oc.v1.notifications.inapp` | JetStream subject where outbox envelopes are published. |
| `WorkerHosts__Outbox__PollingInterval` | `00:00:02` | Polling interval for unleased/pending outbox rows. |
| `WorkerHosts__Outbox__SupervisorLeaseDuration` | `00:00:30` | Durable lease duration for `oc/outbox/supervisor`. |

---

## 4. Architecture & Delivery Guarantees

1. **Atomic Relational Acceptance (`WSE2E-AT-074`)**: Business entity mutation (tasks, events) and outbox rows commit in a single database transaction using `IOpenConnectPersistenceUnitOfWork`. Rollbacks leave zero orphaned outbox rows.
2. **PUBACK Before Delivered Mark (`WSE2E-AT-075`)**: The dispatcher fences pending rows (`OutboxClaimOperations`), publishes each envelope to JetStream, and awaits positive broker PUBACK before marking the row `delivered`. No transaction is held across the network call.
3. **Crash Recovery & Deduplication (`WSE2E-AT-076`)**: If the dispatcher crashes between publish and marking `delivered`, redelivery uses deterministic `Nats-Msg-Id` (`envelope.MessageId`), ensuring JetStream deduplicates duplicate publishes.
4. **Broker Outage Retention (`WSE2E-AT-077`)**: Unreachable NATS brokers trigger exponential backoff on pending rows without marking false completion or corrupting accepted tasks.
5. **High Concurrency & Poison Quarantine (`WSE2E-AT-078`, `WSE2E-AT-080`)**: 100 concurrent rows are claimed across competing dispatchers without duplicate delivery. Rows exceeding `maxAttempts` are marked `quarantined`.
6. **Canary Guardrails**: `NatsPublisher` strictly validates envelope schemas and rejects payloads containing raw prompts, secrets, or audio bytes.

---

## 5. Offline Test Procedure & Acceptance Coverage (`WSE2E-AT-073..080`)

Run the complete offline test suite without Docker or network access:

```bash
sh samples/workers/outbox/tests/t1_contract.sh
```

| Acceptance ID | Scenario | Test Method / Fixture |
|---|---|---|
| `WSE2E-AT-073` | Compose references canonical Dockerfile, image `vuminkosimatibe/openconnect-worker-outbox`, no model volume, standalone mode | `tests/t0_static.sh`, `WSE2E_AT_073_Options_and_hosting_invariants_verify_model_free_and_disables_embedded` |
| `WSE2E-AT-074` | Transactional acceptance of Task + Event + Outbox row in one unit of work; rollback leaves zero orphans | `fixtures/atomic-task-event-outbox.json`, `WSE2E_AT_074_Atomic_relational_acceptance_task_event_outbox_in_one_transaction` |
| `WSE2E-AT-075` | Fenced batch claim with `FencingVersion` increment; broker PUBACK required before delivered mark | `fixtures/fenced-claim-and-puback.json`, `WSE2E_AT_075_Fenced_batch_claim_and_broker_ack_before_delivered_mark` |
| `WSE2E-AT-076` | Post-PUBACK crash simulation with deterministic `Nats-Msg-Id` for idempotent JetStream replay | `fixtures/crash-after-puback-replay.json`, `WSE2E_AT_076_Crash_after_puback_and_deduplicated_replay` |
| `WSE2E-AT-077` | NATS broker outage retention with exponential backoff; no false completion | `fixtures/broker-outage-retention.json`, `WSE2E_AT_077_Broker_outage_retention_and_exponential_backoff` |
| `WSE2E-AT-078` | 100 concurrent rows claimed across competing dispatchers with disjoint claims and full delivery | `fixtures/competing-dispatchers-100-rows.json`, `WSE2E_AT_078_Competing_dispatchers_claim_100_concurrent_rows` |
| `WSE2E-AT-079` | Provider matrix (SQLite, PostgreSQL, SQL Server); manual migrations enforced; InMemory rejection | `fixtures/provider-matrix-no-auto-ddl.json`, `WSE2E_AT_079_Provider_matrix_no_auto_ddl_and_inmemory_rejection` |
| `WSE2E-AT-080` | Poison row quarantine on reaching max attempts; canary scan rejects raw prompts/secrets/audio | `fixtures/canary-and-poison-quarantine.json`, `WSE2E_AT_080_Poison_quarantine_and_canary_payload_validation` |

---

## 6. Teardown & Production Differences

Tear down disposable test resources without deleting named model volumes:

```bash
docker compose -f samples/workers/outbox/compose.e2e.yaml down --remove-orphans
```

**Production differences**:
1. In production, NATS credentials (TLS certificates, NKey/token) are loaded from Kubernetes secrets or managed IAM.
2. In production, database migrations are applied ahead of deployment by release pipelines, not the outbox container.
