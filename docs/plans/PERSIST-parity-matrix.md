# PERSIST Parity Matrix — EFCore 12-Entity vs Legacy Postgres 8-Table + 3 Migrations

> Scope: read-only source comparison. No builds, tests, migrations, or runtime verification were executed.
> Every claim below is grounded in static inspection of the canonical worktree at task time.
> Anything not directly observed in source is marked **NOT VERIFIED**.

## Source inventory (inspected)

- EFCore model: `src/OpenConnect.Persistence.EFCore/Entities/*.cs` (12 entities), `EntityConfigurations/EntityConfigurations.cs`, `EntityConfigurations/NativeAgentSessionEntityConfiguration.cs`, `EntityConfigurations/PublicSessionMappingEntityConfiguration.cs`, `EntityConfigurations/ApprovalNonceEntityConfiguration.cs`, `Model/OpenConnectModelBuilderExtensions.cs`, `Model/OpenConnectModelOptions.cs`, `Context/OpenConnectDbContext.cs`, `Health/EfRuntimeModelValidator.cs`, `Health/EfStartupValidator.cs`, `Configuration/OpenConnectEfOptions.cs`, `Configuration/OpenConnectEfOptionsValidator.cs`, `Stores/EfLeaseStore.cs`, `Stores/*` (session/task/event/outbox/approval/privacy/native/mapping/nonce), `Providers/IEfOutboxClaimDialect.cs`
- Legacy Postgres: `src/OpenConnect.Persistence.Postgres/OpenConnectDbContext.cs`, `src/OpenConnect.Persistence.Postgres/Entities/PersistenceEntities.cs`, `src/OpenConnect.Persistence.Postgres/Repositories/OwnershipStores.cs`, `src/OpenConnect.Persistence.Postgres/Migrations/20261106000000_InitialCreate.cs`, `src/OpenConnect.Persistence.Postgres/Migrations/20261106220532_PendingModelSync.cs`, `src/OpenConnect.Persistence.Postgres/Migrations/20261107193633_NavigatorDurability.cs`, `src/OpenConnect.Persistence.Postgres/Migrations/OpenConnectDbContextModelSnapshot.cs`
- Navigator Postgres seam: `src/OpenConnect.Navigator.Persistence.Postgres/PostgresDurableNavigatorStore.cs`, `src/OpenConnect.Navigator.Persistence.Postgres/PostgresExpiredLeaseSweeper.cs`, `src/OpenConnect.Navigator.Persistence.Postgres/NavigatorPostgresOutboxRelaySource.cs`
- Legacy-adjacent contract test: `tests/Unit/OpenConnect.Persistence.Tests/PersistenceModelTests.cs`

## Naming / topology divergence (applies to all rows)

| Aspect | Legacy Postgres (8 tables) | EFCore 12-entity model |
|---|---|---|
| Table names | `oc_sessions`, `oc_tasks`, `oc_turns`, `oc_events`, `oc_outbox`, `oc_leases`, `oc_approvals`, `oc_privacy_tokens` (unqualified; snapshot `ToTable("oc_*", (string)null)`) | `Sessions`, `Tasks`, `Turns`, `Events`, `Outbox`, `Leases`, `Approvals`, `PrivacyTokens`, `OperationalRuns`, `NativeAgentSessions`, `PublicSessionMappings`, `ApprovalNonces` in schema `openconnect` by default (`OpenConnectModelOptions.Default.Schema = "openconnect"`); SQLite mode uses `UseSqlitePrefix` + `SqliteTablePrefix = "oc_"` + lowercased names |
| Identity typing | Native `uuid` tenant/principal/owner/session/task ids | String scopes: `TenantScope` (max 128), `PrincipalScope`/`PrincipalOrWorkloadScope`/`WorkloadScope` (max 256), ULID-derived via `UlidGuidMapper`/`UlidGuidConverter` — direct column-type equivalence is **NOT VERIFIED** |
| Time typing | `timestamp with time zone` (`DateTimeOffset`) everywhere | `DateTime`/`DateTime?` UTC (`CreatedUtc`, `UpdatedUtc`, `OccurredUtc`, `ExpiresUtc`, `NextAttemptUtc`, etc.) — truncation/offset behavior **NOT VERIFIED** |
| Payload typing | `jsonb` columns (`context_checkpoint`, `context`, `payload`, `receipt_payload`) | Plain string properties (`ContextCheckpoint`, `Context`, `SanitizedPayload`) with no `HasColumnType("jsonb")` observed in EFCore configs — storage type/translation **NOT VERIFIED** |
| Runtime enforcement | `EfRuntimeModelValidator.Expected` requires all 12 `(Type, Table)` pairs and throws `EfModelMissing` otherwise; `EfStartupValidator` + `OpenConnectEfOptions.ValidateSchema` gate startup | Legacy has no equivalent 12-table validator observed; `PersistenceModelTests` only asserts `oc_sessions`/`oc_tasks` table names + `Version` concurrency tokens + presence of `oc_privacy_tokens` |

## Per-entity / per-table comparison (8 overlapping + 4 EFCore-only)

### 1. Session — legacy `oc_sessions` ↔ EFCore `Sessions`

| Concern | Legacy (`PersistenceEntities.SessionEntity` + `OpenConnectDbContext.cs:30-42`) | EFCore (`Entities/SessionEntity.cs` + `SessionEntityConfiguration`) | Parity |
|---|---|---|---|
| Columns | `id uuid PK`, `tenant_id uuid req`, `owner_id uuid req`, `version bigint concurrency-token default 1`, `context_checkpoint jsonb null`, `created_at/updated_at timestamptz req`, `expires_at timestamptz null` | `SessionId Guid PK`, `TenantScope string(128) req`, `PrincipalScope string(256) req`, `Version long concurrency-token` (no default observed), `ContextCheckpoint string?`, `CreatedUtc/UpdatedUtc req`, `ExpiresUtc null` | Partial: same shape, different key/tenant typing; receipt/session-task linkage in Navigator (`PostgresDurableNavigatorStore`) depends on legacy composite keys — EFCore equivalence **NOT VERIFIED** |
| Constraints | Alternate key `(tenant_id, owner_id, id)` named `uq_session_tenant_owner` | No alternate key observed; only single-column PK | **Gap**: alternate key not carried over in inspected config |
| Indexes | `idx_sessions_tenant_owner (tenant_id, owner_id)` | `HasIndex(TenantScope, PrincipalScope)` (unnamed in source) | Partial: same columns semantically, name/typing differ |
| Concurrency | `Version IsConcurrencyToken + default 1` | `Version IsConcurrencyToken` (no default observed) | Partial; seed behavior **NOT VERIFIED** |

### 2. Task — legacy `oc_tasks` ↔ EFCore `Tasks`

| Concern | Legacy (`OpenConnectDbContext.cs:44-60`) | EFCore (`Entities/TaskEntity.cs` + `TaskEntityConfiguration`) | Parity |
|---|---|---|---|
| Columns | `id`, `tenant_id`, `principal_id`, `session_id uuid`, `client_task_id text req`, `agent_id text req`, `state text req`, `version concurrency default 1`, `canonical_hash text req`, `input_ref/result_ref text null`, `created_at/completed_at timestamptz`, `receipt_payload jsonb null`, `receipt_status text null` | `TaskId`, `TenantScope`, `PrincipalScope`, `SessionId`, `ClientTaskId(256) req`, `WorkloadScope(256) req` (**new, no legacy counterpart**), `AgentId req`, `State req`, `Version concurrency`, `CanonicalHash req`, `InputRef?/ResultRef?`, `CreatedUtc`, `CompletedUtc?`; **no `ReceiptPayload`/`ReceiptStatus` observed** | **Divergent**: `WorkloadScope` added; receipt columns absent in inspected EFCore entity |
| Constraints | Alternate key `(tenant_id, principal_id, id)` `uq_task_tenant_principal_id`; FK `(tenant_id, principal_id, session_id)` → sessions `(tenant_id, owner_id, id)` `Restrict` | No alternate key or FK observed in `TaskEntityConfiguration` | **Gap**: FK + alternate key not observed in EFCore config |
| Indexes | `uq_task_idempotency (tenant_id, principal_id, client_task_id)` unique; `idx_tasks_session(session_id)`; `idx_tasks_state(state)`; `IX_oc_tasks_*(tenant_id, principal_id, session_id)` (002) | `HasIndex(TenantScope, PrincipalScope, ClientTaskId)` unique; `HasIndex(SessionId)`; no `State` index observed | Partial: idempotency + session covered; `state` index missing |
| Concurrency | `Version` token | `Version` token | Preserved |

### 3. Turn — legacy `oc_turns` ↔ EFCore `Turns`

| Concern | Legacy | EFCore | Parity |
|---|---|---|---|
| Columns | `id`, `tenant_id`, `principal_id`, `session_id`, `sequence bigint`, `state text req` (002 makes non-null `""` default), `context jsonb null`, `started_at req`, `completed_at null` | `TurnId`, `TenantScope`, `PrincipalScope`, `SessionId`, `Sequence`, `State req`, `Context string?`, `StartedUtc`, `CompletedUtc?` | Partial: same shape modulo scope/time typing; defaults **NOT VERIFIED** |
| Constraints | FK → sessions `Restrict` | No FK observed | **Gap** |
| Indexes | `uq_turn_session_sequence (session_id, sequence)` unique; `IX_oc_turns_(tenant_id, principal_id, session_id)` (002) | `HasIndex(SessionId, Sequence)` unique; no tenant composite observed | Partial |

### 4. Event — legacy `oc_events` ↔ EFCore `Events`

| Concern | Legacy | EFCore | Parity |
|---|---|---|---|
| Columns | `id`, `session_id`, `sequence`, `event_type text req` (002), `payload jsonb req` (002), `occurred_at timestamptz`, `correlation_id text null` + 003 adds `tenant_id uuid req`, `principal_id uuid req` | `EventId`, `TenantScope req`, `SessionId`, `Sequence`, `EventType(256) req`, `SanitizedPayload req` (renamed from `Payload`), `OccurredUtc`, `CorrelationId?`; **no `PrincipalId` property observed** | **Divergent**: payload renamed to sanitized; principal scoping column absent in inspected entity |
| Constraints | 003: FK `(tenant_id, principal_id, session_id)` → sessions `Restrict` | No FK observed | **Gap** |
| Indexes | 001 `uq_event_sequence (session_id, sequence)` → replaced by 003 `uq_event_sequence (tenant_id, principal_id, session_id, sequence)` unique | `HasIndex(TenantScope, SessionId, Sequence)` unique (no principal) | Partial; principal omitted |

### 5. Outbox — legacy `oc_outbox` ↔ EFCore `Outbox`

| Concern | Legacy | EFCore | Parity |
|---|---|---|---|
| Columns | `id`, `topic text req` (002), `payload jsonb req` (002), `state text default pending` (002), `attempts int`, `next_attempt_at`, `created_at`, `delivered_at null` | `Id`, `TenantScope req` (**new**), `EventId(256) req` (**new**), `Subject(512) req` (**replaces Topic**), `SanitizedPayload req` (**replaces Payload**), `Status(32) default pending` (**renamed State→Status**), `FencingVersion concurrency` (**new**), `LeaseOwner?/LeaseExpiresUtc?` (**new**), `CreatedUtc`, `PublishedUtc?` (**new**), `NextAttemptUtc`, `Attempts`; **no `DeliveredAt`** | **Divergent by design**: claim/lease-oriented outbox, not the legacy topic/payload/delivered model |
| Constraints | PK only | PK only (both) | Preserved |
| Indexes | `idx_outbox_pending (state, next_attempt_at) WHERE state='pending'` | `HasIndex(TenantScope, EventId)` unique; `HasIndex(Status, NextAttemptUtc)` (no partial-filter observed) | Partial: pending index not filter-scoped in inspected config; idempotency key is new |
| Dialects | Single Postgres path | `IEfOutboxClaimDialect` + `OpenConnectOutboxClaimStrategy { Auto, OptimisticConcurrency, ProviderOptimized }`; ProviderOptimized on a non-SQLite provider throws `EfProviderUnsupported`; on SQLite it returns the SQLite claim dialect; behavior on Npgsql **NOT VERIFIED** | New capability; parity **NOT VERIFIED** |

### 6. Lease — legacy `oc_leases` ↔ EFCore `Leases`

| Concern | Legacy | EFCore | Parity |
|---|---|---|---|
| Columns | 001: `name PK`, `owner`, `expires_at`, `version`; 003: PK → `(tenant_id, name)`, adds `tenant_id uuid req` | `Id Guid PK` (**new surrogate**), `TenantScope req`, `ResourceKey(256) req` (**renames Name**), `Owner(256) req`, `FencingVersion concurrency` (**renames Version**), `ExpiresUtc` | **Divergent**: surrogate `Id` PK + `(TenantScope, ResourceKey)` unique replaces composite `(tenant_id, name)` PK |
| Constraints | PK `(tenant_id, name)`; `Version IsConcurrencyToken` | PK `Id`; `HasIndex(TenantScope, ResourceKey)` unique; `FencingVersion IsConcurrencyToken` | Partial: uniqueness preserved via unique index, not PK |
| Store semantics | Navigator uses single-statement `INSERT … ON CONFLICT … DO UPDATE … WHERE expires_at <= now` fencing (see seam below); sweeper retains rows, `ReleaseLeaseAsync` expires to `MinValue` | `EfLeaseStore<TContext>`: read-then-insert/update with `SaveChanges`, `RenewAsync` requires owner+version+unexpired, `ReleaseAsync` **deletes** the row (vs legacy retain-and-expire) | **Divergent**: no raw-SQL upsert observed in `EfLeaseStore`; fencing/race behavior **NOT VERIFIED** |

### 7. Approval — legacy `oc_approvals` ↔ EFCore `Approvals`

| Concern | Legacy | EFCore | Parity |
|---|---|---|---|
| Columns | `id`, `tenant_id`, `principal_id`, `task_id`, `state req` (002), `decision null`, `created_at`, `decided_at null` + 003 adds `command_id/command_digest null`, `consumed_at/expires_at null` | `ApprovalId`, `TenantScope`, `PrincipalScope`, `TaskId`, `State(32) default pending`, `Decision?`, `BoundArgumentsHash(128)?` (**replaces CommandDigest? naming differs**), `CreatedUtc`, `ExpiresUtc req` (**non-null vs legacy nullable**), `DecidedUtc?`; **no `CommandId`/`ConsumedAt` observed** | **Divergent**: command/nonce linkage split to `ApprovalNonces`; expiry nullability tightened |
| Constraints | FK → tasks `Cascade` | No FK observed | **Gap** |
| Indexes | 003 `uq_approval_command (tenant_id, principal_id, task_id, command_id)` unique; `idx_approvals_pending (tenant_id, principal_id, state, expires_at) WHERE state='pending'` (003; replaces 001 non-unique task index and 002 `IX_oc_approvals_*`) | `HasIndex(TenantScope, TaskId)` non-unique; no pending-filter or command-unique observed | **Gap**: idempotency + pending fast-path not carried over in inspected config |

### 8. PrivacyToken — legacy `oc_privacy_tokens` ↔ EFCore `PrivacyTokens`

| Concern | Legacy | EFCore | Parity |
|---|---|---|---|
| Columns | `token text PK`, `encrypted_value bytea req`, `tenant_id/principal_id/session_id uuid`, `turn_id uuid null`, `entity_type/key_id text req` (002), `created_at/expires_at timestamptz` | `Id Guid PK` (**new surrogate; legacy PK was `Token`**), `TenantScope`, `PrincipalOrWorkloadScope(256)` (**renames/merges principal**), `SessionId`, `TurnId?`, `TokenId(256) req` (**replaces Token as non-PK field**), `EntityType(128)`, `EncryptedValue bytea`, **`Nonce`/`AuthenticationTag`/`WrappedDataKey` bytea new**, `KeyProviderId(128)/KeyVersion(128)` (**split from KeyId**), `BindingDigest(128) req` (**new**), `CreatedUtc/ExpiresUtc` | **Divergent**: envelope-encryption shape; key split; binding digest |
| Indexes | `idx_tokens_session(session_id)`; `idx_tokens_expiry(expires_at)` | `HasIndex(TenantScope, PrincipalOrWorkloadScope, TokenId, SessionId, TurnId)` unique; `HasIndex(ExpiresUtc)` | Partial: expiry covered; session lookup replaced by composite unique |
| Vault default | None observed in legacy entities | `EfPrivacyTokenVault` hardcodes `KeyProviderId = "envelope"`, `KeyVersion = "v1"` | New default; rotation story **NOT VERIFIED** |

## Per-migration behavior preserved (legacy 001–003)

### 001 `20261106000000_InitialCreate` — creates 8 tables + base indexes/FKs

Preserved in EFCore model intent: 8 overlapping entities exist with PKs, tenant scoping, concurrency tokens on Sessions/Tasks/Leases, unique turn/event sequences, pending-filtered outbox intent, token expiry/session indexes.
**Not** carried over verbatim in inspected EFCore configs: `jsonb`/`timestamptz`/`bytea` column types, snake_case names, `openconnect` schema vs unqualified `oc_*`, composite FKs (`Restrict` sessions→tasks/turns/events; `Cascade` tasks→approvals), alternate keys (`uq_session_tenant_owner`, `uq_task_tenant_principal_id`), `idx_tasks_state`, partial-filter clause text, lease PK shape. Data migration / ordering equivalence **NOT VERIFIED** (no EFCore migration bundle inspected; none observed under `Persistence.EFCore/`).

### 002 `20261106220532_PendingModelSync` — nullability tightening + 3 FK-supporting indexes

Intent (non-null `state/topic/payload/owner/event_type/entity_type/key_id`, `IX_oc_turns/tasks/approvals (tenant_id, principal_id, session_id|task_id)`) is reflected in EFCore `IsRequired()` + `HasMaxLength` + `HasIndex` usage.
Exact DDL (text `""` defaults, `pending` default on outbox state, index names) is **NOT VERIFIED** to be reproduced; EFCore default-value DDL was not observed in inspected configs. Down reversibility has no EFCore counterpart in inspected sources.

### 003 `20261107193633_NavigatorDurability` — receipts, tenant leases, event ownership, approval fencing, quarantine

| 003 behavior | EFCore disposition (inspected) |
|---|---|
| `oc_tasks.receipt_payload jsonb null` + `receipt_status text null` | **Absent** on inspected `TaskEntity`; Navigator receipt path depends on legacy columns (`PostgresDurableNavigatorStore.ConsumeApprovalAndSaveReceiptAsync`, `GetReceiptAsync`, `TrySaveReceiptAsync`) — EFCore receipt parity **NOT VERIFIED** |
| `oc_leases.tenant_id` + PK `(tenant_id, name)` | Replaced by surrogate `Id` + unique `(TenantScope, ResourceKey)`; tenant scoping preserved semantically, PK shape changed |
| `oc_events.tenant_id/principal_id` backfill from sessions + quarantine sentinel `00000000-0000-0000-0000-000000000001` + `reserved_range ≥ 2_000_000_000` + `jsonb_set` provenance keys + FK to sessions | **No** sentinel/quarantine/backfill logic observed in EFCore sources; `EventEntity` lacks `PrincipalId`; FK absent — legacy-data compatibility **NOT VERIFIED** |
| `oc_approvals.command_id/command_digest/consumed_at/expires_at` + `uq_approval_command` unique + `idx_approvals_pending` partial | Split: `ApprovalEntity.BoundArgumentsHash` + separate `ApprovalNonceEntity(TenantId, Nonce)` PK with `ApprovalId/RequestId/PrincipalId/CreatedAtUtc` + indexes on `ApprovalId`, `RequestId`; unique-command and pending-filter indexes **not observed** |
| Guard rail: fail-closed sentinel collision check; Down never deletes sentinel; ownership backfill irreversible | No equivalent guard observed in EFCore sources; irreversibility carries over by nature — operator runbook **NOT VERIFIED** |

## 4-table gap list (EFCore-only; no legacy `oc_*` counterpart)

### G1. `OperationalRuns` — disposition: RETAIN (new capability, no legacy to migrate within inspected legacy sources (3 migrations + snapshot + files listed in §Source inventory))

- Rationale: backs `OperationalRunEntityConfiguration` + `OperationalRunEntity` (`Id` PK, `TenantScope`/`WorkloadScope`/`RunId`/`Status` + `FencingVersion` concurrency + unique `(TenantScope, WorkloadScope, RunId)`); no legacy table to deprecate within inspected legacy sources (3 migrations + snapshot + files listed in §Source inventory).
- Evidence preservation: REQUIRED (not observed in this slice): tenant/workload fencing tests; `FencingVersion` concurrency-conflict tests; `TrySaveReceiptAsync`/`GetReceiptAsync` operational-receipt round-trip tests (tenant-scoped, principal==tenant); `PostgresExpiredLeaseSweeper`-analog retention policy if runs gain expiry. Security invariants: `TenantScope`+`WorkloadScope` always bound server-side; `RunId` never trusted as tenant; receipt `Completed`-only writes.
- Open items (**NOT VERIFIED**): actual store/test names beyond inspected files; Postgres DDL for the new table; expiry/sweeper coverage.

### G2. `NativeAgentSessions` — disposition: RETAIN (new capability, no legacy to migrate within inspected legacy sources (3 migrations + snapshot + files listed in §Source inventory))

- Rationale: opaque encrypted MAF binding (`BindingKey PK(64, ascii)`, `SessionId/TenantId/PrincipalId ULID strings(26, ascii)`, `AgentId/DefinitionRevision/MafVersion/SchemaVersion/ProviderId` req, `Version req`, `Ciphertext req`); `EfNativeAgentSessionStore` verifies `ProviderId` ordinal match and binding-tuple match.
- Evidence preservation: REQUIRED (not observed in this slice): binding-key uniqueness tests; cross-tenant/cross-principal negative reads; provider-mismatch rejection tests; ciphertext-opaque (no plaintext log) tests; `Version` overwrite/rollback tests.
- Open items (**NOT VERIFIED**): key-management/rotation story; `Ciphertext` size limits; index needs on `(TenantId, SessionId)`.

### G3. `PublicSessionMappings` — disposition: RETAIN (new capability, contract `SESSION-UUID-MAPPING`)

- Rationale: immutable public-UUID→runtime-ULID map; PK `(TenantId, PublicId)`; `InternalSessionId(26, ascii)` globally unique; `PrincipalId` + `CreatedAtUtc` + `Version` concurrency token; fail-closed tenant/principal mismatch → null.
- Evidence preservation: REQUIRED (not observed in this slice): immutability (no update/delete path) tests; `(TenantId, PublicId)` uniqueness tests; `InternalSessionId` global-unique tests; cross-tenant mismatch→not-found tests; genuine-UUID (no ULID→UUID derivation) tests.
- Open items (**NOT VERIFIED**): resolver dual-ownership checks live with caller (per remarks) — caller enforcement tests needed; no legacy migration required within inspected legacy sources (3 migrations + snapshot + files listed in §Source inventory).

### G4. `ApprovalNonces` — disposition: RETAIN + MIGRATE legacy `command_id` linkage forward (do not silently drop)

- Rationale: durable single-use nonce binding (`TenantId(26, ascii)`, `Nonce(64, ascii)` → PK `(TenantId, Nonce)`; `ApprovalId Guid`; `RequestId(256, ascii)` verbatim, `""`-defaulted for expand-compat; `PrincipalId`; `CreatedAtUtc`; indexes on `ApprovalId`, `RequestId`); insert-only, never updated/deleted in this slice; `RequestId ""` treated as corrupt/absent (fail-closed).
- Evidence preservation: REQUIRED (not observed in this slice): `(TenantId, Nonce)` replay-rejection tests; `RequestId` verbatim (non-GUID) round-trip tests; `Guid.Empty`-approval fail-closed tests; approval↔nonce audit-join tests; legacy `oc_approvals.command_id/command_digest/consumed_at` → nonce-binding backfill plan with idempotent rerun.
- Open items (**NOT VERIFIED**): backfill SQL does not exist in inspected sources; legacy `uq_approval_command` semantics have no direct EFCore unique index — replacement guarantee needs explicit test.

## Navigator lease raw-SQL seam (reference only — no fix applied)

- `src/OpenConnect.Navigator.Persistence.Postgres/PostgresDurableNavigatorStore.cs:220` — `ExecuteSqlInterpolatedAsync` single-statement `INSERT INTO oc_leases … ON CONFLICT (tenant_id, name) DO UPDATE … WHERE oc_leases.expires_at <= now` (fencing token = winner's `version + 1`); followed by read-back at `:228`.
- Related read paths (no raw SQL): `:211` (`IsFenceCurrentAsync` via `AnyAsync` on `TenantId/Name/Version/ExpiresAt`), `:235-249` (`RenewLeaseAsync` read-modify-`SaveChanges` with `DbUpdateConcurrencyException` → null), `:252-253` (`IsLeaseCurrentAsync`), `:255-267` (`ReleaseLeaseAsync` expires to `MinValue`, retains row), `PostgresExpiredLeaseSweeper.cs:32-33` (deletes `ExpiresAt < cutoff`; retention default 7 days per remarks).
- EFCore contrast: `EfLeaseStore.cs:17-28` uses read-then-insert/update + `SaveChanges` (no `ON CONFLICT` upsert observed); `ReleaseAsync (:39-45)` deletes instead of expiring. Atomicity/fencing equivalence **NOT VERIFIED**.

## Validators, stores, and security seams (observed, not executed)

- `OpenConnectEfOptionsValidator.cs:13-41` — validates schema name, SQLite prefix, migration-mode conflict, ValidateSchema enum defined, and outbox polling/lease/batch/dispatcher/attempt/bounds ranges.
- `EfStartupValidator.cs:16-29` + `EfRuntimeModelValidator.cs:9-17` — require all 12 tables; missing mapping throws `EfModelMissing`.
- `EfSessionUuidResolver` / `EfPublicSessionMappingStore` / `EfApprovalNonceStore.ValidateKey/ValidateRequestId` — server-side tenant/principal/nonce validation observed; bypass coverage **NOT VERIFIED**.
- Legacy `OwnershipStores.cs:16-47` — session/task reads filter `(Id, TenantId, OwnerId/PrincipalId)` via `UlidGuidConverter`; EFCore store equivalents use string scopes — ULID↔GUID mapping equivalence **NOT VERIFIED**.

## Explicit NOT VERIFIED list (must not be claimed elsewhere)

- No build, test, migration-apply, or database run was performed; no pass/fail, coverage, performance, or provider-support claim is made.
- Npgsql vs SQLite translation of the EFCore model (schema prefixes, string length enforcement, concurrency tokens, `DateTime` vs `timestamptz`, JSON handling), .NET 9/10 compatibility, and migration ordering/safety are **NOT VERIFIED**.
- Whether an EFCore migration bundle exists, whether it reproduces 001–003 DDL, and whether legacy data (sentinel row, quarantined events, receipts, leases) survives a transition are **NOT VERIFIED**.
- Concurrency/idempotency equivalence (outbox claim, lease fencing, approval consume, receipt replay, nonce single-use) under contention is **NOT VERIFIED**.
- Tenant-isolation negative cases and timing/expiry edge cases for all 12 entities are **NOT VERIFIED**.
