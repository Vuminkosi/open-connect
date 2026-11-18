# Worker Sample: `operational-agents`

End-to-end sample and verification harness for the standalone **Operational Agents** worker host (`src/OpenConnect.WorkerHosts.Operational/Dockerfile` and `workers/operational-agents/Dockerfile`), covering specification **§5.10 (`WSE2E-IMP-019`, `WSE2E-AT-066..072`)**.

---

## 1. Canonical Production Image & Build Command

- **Canonical Dockerfile**: `src/OpenConnect.WorkerHosts.Operational/Dockerfile` (mirrored at `workers/operational-agents/Dockerfile`)
- **Canonical Image Name**: `vuminkosimatibe/openconnect-worker-operational-agents:e2e-local`
- **Model Volume / Provisioner**: **None.** Operational agents supervise background workflows, execute deterministic scheduled tours, and enforce lease fencing against durable storage. No model weights or provisioner service exist (`WSE2E-AT-066`).

Build the production image locally from the repository root:

```bash
docker build \
  -f src/OpenConnect.WorkerHosts.Operational/Dockerfile \
  -t vuminkosimatibe/openconnect-worker-operational-agents:e2e-local \
  .
```

---

## 2. Docker Compose Invocation

Start the sample stack (`postgres` + `operational-agents` supervisor on the isolated `oc-test-private` network):

```bash
docker compose -f samples/workers/operational-agents/compose.e2e.yaml up -d --build
```

Execute verification across tiers:

```bash
# T0 static validation + T1 offline contract & .NET test suite
sh samples/workers/operational-agents/tests/t1_contract.sh

# T3 runtime supervisor lease proof (live PostgreSQL, manual migration application, fencing verification)
sh samples/workers/operational-agents/tests/t3_runtime.sh
```

---

## 3. Environment Variables, Secrets & Database Configuration

| Variable | Default in `compose.e2e.yaml` | Purpose |
|---|---|---|
| `WorkerHosts__Operational__Mode` | `Standalone` | Required hosting mode. Fails closed if embedded supervisor is also active (`operational_standalone_requires_embedded_disabled`). |
| `WorkerHosts__Operational__EmbeddedEnabled` | `false` | Must be `false` when running as standalone supervisor. |
| `WorkerHosts__Operational__TenantUlid` | `01ARZ3NDEKTSV4RRFFQ69G5FAV` | 26-character Crockford Base32 tenant ULID scoping the supervisor lease row. |
| `WorkerHosts__Operational__DatabaseProvider` | `npgsql` | Relational provider (`npgsql`, `sqlite`, or `sqlserver`). |
| `WorkerHosts__Operational__ConnectionString` | `Host=postgres;Port=5432;Database=openconnect;Username=openconnect;Password=...` | Database connection string. Test password uses safe `do-not-use-in-prod` suffix. |
| `WorkerHosts__Operational__SupervisorLeaseDuration` | `00:00:30` | Durable lease duration for `oc/operational/supervisor`. |
| `WorkerHosts__Operational__RenewInterval` | `00:00:10` | Heartbeat interval (must be strictly shorter than lease duration). |

---

## 4. Architecture & Sample-Owned Migrations

1. **Manual Migrations (`OpenConnectMigrationMode.Manual`)**: The operational worker host never executes DDL (`Database.Migrate` or `EnsureCreated`). All schema changes are applied by the gateway or sample-owned migration scripts in `samples/workers/operational-agents/Migrations/` before the worker boots (`WSE2E-AT-066`, `WSE2E-AT-071`).
2. **Exclusivity Guard**: `OperationalWorkerOptionsValidator` prevents duplicate supervision. If a secondary replica attempts to acquire `oc/operational/supervisor` for the same tenant, it exits immediately with `operational_duplicate_supervisor` without disrupting the active supervisor (`WSE2E-AT-072`).
3. **Workload Identity**: Executes under a service principal (`ServicePrincipalRef`), never ambient `HttpContext` or forwarded user cookies (`WSE2E-AT-067`).
4. **MAF Native Checkpoints**: Agent session state is saved to `INativeAgentSessionStore` as encrypted ciphertext, and human-in-the-loop approvals are consumed with exact command digest validation (`WSE2E-AT-069`).

---

## 5. Offline Test Procedure & Acceptance Coverage (`WSE2E-AT-066..072`)

Run the complete offline test suite without Docker or network access:

```bash
sh samples/workers/operational-agents/tests/t1_contract.sh
```

| Acceptance ID | Scenario | Test Method / Fixture |
|---|---|---|
| `WSE2E-AT-066` | Compose references canonical Dockerfile, image `vuminkosimatibe/openconnect-worker-operational-agents`, no model volume, standalone mode | `tests/t0_static.sh`, `WSE2E_AT_066_Options_and_hosting_invariants_verify_model_free_and_disables_embedded` |
| `WSE2E-AT-067` | Constrained workload identity (`service-principal-ref`), ambient `HttpContext` / user-cookie rejection, user trigger provenance does not impersonate user | `fixtures/workload-identity-job.json`, `WSE2E_AT_067_Constrained_workload_identity_and_ambient_context_rejection` |
| `WSE2E-AT-068` | Competing host lease fencing (`oc/operational/supervisor`), monotonic fencing token, stale host renew denial | `fixtures/competing-hosts-lease-fence.json`, `WSE2E_AT_068_Competing_host_lease_fencing_and_stale_fence_denial` |
| `WSE2E-AT-069` | MAF-native checkpoint state persistence via `INativeAgentSessionStore`, HITL approval digest verification and replay protection | `fixtures/checkpoint-hitl-resume.json`, `WSE2E_AT_069_MAF_native_checkpoint_and_approval_nonce_with_digest_validation` |
| `WSE2E-AT-070` | Misfire deadline expiry, overlap prevention, mid-run cancellation handling | `fixtures/misfire-overlap-revocation-cancel.json`, `WSE2E_AT_070_Misfire_overlap_and_mid_run_cancellation` |
| `WSE2E-AT-071` | Provider matrix (SQLite, PostgreSQL, SQL Server), `ef_migration_not_allowed` on shared context, EF InMemory rejection (`ef_inmemory_not_durable`) | `fixtures/provider-matrix-and-schema-guard.json`, `WSE2E_AT_071_Provider_matrix_and_schema_guard_invariants` |
| `WSE2E-AT-072` | Embedded vs standalone exclusivity guard (`operational_standalone_requires_embedded_disabled`), duplicate replica failure | `fixtures/embedded-standalone-exclusivity.json`, `WSE2E_AT_072_Embedded_vs_Standalone_exclusivity_guards` |

---

## 6. Teardown & Production Differences

Tear down disposable test resources without removing named volumes from other workers:

```bash
docker compose -f samples/workers/operational-agents/compose.e2e.yaml down --remove-orphans
```

**Production differences**:
1. In production, database credentials and connection strings are retrieved from a secure secret vault (e.g. Azure Key Vault / HashiCorp Vault) rather than environment files.
2. In production, database schema migrations are executed by a controlled deployment gate prior to deploying new worker replicas.
