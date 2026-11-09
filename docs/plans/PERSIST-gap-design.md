# PERSIST Gap Design — Disposition of the 4 EFCore-Only Tables

> Scope: decision document only. No implementation, migration SQL, or test changes.
> Authority: OC-PERSIST-001 substance (orchestrator-APPROVED 2026-11-09); hard input `docs/plans/PERSIST-parity-matrix.md` §§G1–G4 + 001/002/003 analysis.
> Method: read-only static inspection. Every claim not directly observed is marked **NOT VERIFIED**. No builds, tests, migration applies, or database runs were performed.

## 1. Disposition table

| # | Table (EFCore entity) | Disposition | One-line rationale |
|---|---|---|---|
| G1 | `OperationalRuns` (`OperationalRunEntity`: `Id` PK, `TenantScope`/`WorkloadScope`/`RunId`/`Status`, `FencingVersion` concurrency, unique `(TenantScope, WorkloadScope, RunId)`) | **RETAIN** | New capability; no legacy `oc_*` counterpart in the 3 inspected migrations + snapshot — nothing to migrate or deprecate. |
| G2 | `NativeAgentSessions` (`NativeAgentSessionEntity`: `BindingKey` PK(64 ascii), ULID-string `SessionId`/`TenantId`/`PrincipalId`(26 ascii), `AgentId`/`DefinitionRevision`/`MafVersion`/`SchemaVersion`/`ProviderId`, `Version`, `Ciphertext`) | **RETAIN** | New capability (opaque encrypted MAF binding with provider + binding-tuple verification); no legacy counterpart. |
| G3 | `PublicSessionMappings` (`PublicSessionMappingEntity`: PK `(TenantId, PublicId)` genuine UUID, `InternalSessionId`(26 ascii) globally unique, `PrincipalId`, `CreatedAtUtc`, `Version`; contract SESSION-UUID-MAPPING, immutable, fail-closed mismatch→null) | **RETAIN** | New capability; immutable public-UUID→runtime-ULID map with no legacy counterpart. |
| G4 | `ApprovalNonces` (`ApprovalNonceEntity`: PK `(TenantId, Nonce)`, `ApprovalId Guid`, `RequestId`(256 ascii, verbatim, `""`-defaulted expand-compat), `PrincipalId`, `CreatedAtUtc`; insert-only) | **RETAIN + MIGRATE legacy `command_id` linkage forward** | Durable single-use nonce binding is new and retained; legacy `oc_approvals.command_id/command_digest/consumed_at` (+ `uq_approval_command`) must be carried forward by an explicit backfill plan — must never be silently dropped. |

No table is marked DEPRECATE: within the inspected legacy sources (3 migrations + snapshot + files in the parity-matrix §Source inventory) there is no legacy table for any of the four to deprecate. DEPRECATE applies only to legacy columns superseded by these tables (see §3), and only via the approved backfill.

## 2. Rationale per table (with 001/002/003 grounding)

- **G1 RETAIN.** 003 added receipts to `oc_tasks` (`receipt_payload`/`receipt_status`) and tenant leases, but no `OperationalRuns` equivalent exists in 001–003. The EFCore `TaskEntity` inspected this slice has **no** `ReceiptPayload`/`ReceiptStatus` properties, so operational-receipt durability needs an explicit carrier (candidate: `OperationalRuns` per parity-matrix G1 round-trip note) rather than assuming task-column parity. DDL, expiry/sweeper coverage, and store/test names beyond inspected files are **NOT VERIFIED**.
- **G2 RETAIN.** No 001–003 behavior covers opaque MAF session bindings. `EfNativeAgentSessionStore` provider-ordinal + binding-tuple verification (per matrix) is new security surface, not a migration of any legacy column. Key-management/rotation, `Ciphertext` size limits, and `(TenantId, SessionId)` index needs are **NOT VERIFIED**.
- **G3 RETAIN.** No 001–003 behavior covers public-UUID mapping. Immutability + `(TenantId, PublicId)` PK + globally-unique `InternalSessionId` + genuine-UUID (never ULID-derived) rule are the design; post-resolve dual-ownership checks live with the caller per entity remarks. Caller-enforcement tests are **NOT VERIFIED** to exist.
- **G4 RETAIN + MIGRATE.** 003 introduced `oc_approvals.command_id/command_digest/consumed_at/expires_at` + `uq_approval_command` unique + `idx_approvals_pending` partial. EFCore splits this into `ApprovalEntity.BoundArgumentsHash` plus a separate `ApprovalNonceEntity` with `ApprovalId`/`RequestId` indexes (per matrix; ApprovalEntity.cs not re-read in this slice) — but the unique-command index and pending-filter index were **not observed** in inspected EFCore configs, and backfill SQL does not exist in inspected sources. Disposition therefore couples RETAIN (nonce table stays) with a mandatory MIGRATE (legacy command linkage carried forward by plan, with a replacement uniqueness guarantee). Equivalence under contention is **NOT VERIFIED**.

## 3. Evidence-preservation list (tests + security invariants that must survive)

These are REQUIRED regardless of which future slice implements them (none observed in this slice unless noted):

- **G1:** tenant/workload fencing tests; `FencingVersion` concurrency-conflict tests; operational-receipt round-trip tests (`TrySaveReceiptAsync`/`GetReceiptAsync`, tenant-scoped, principal==tenant); retention/expiry policy incl. `PostgresExpiredLeaseSweeper`-analog if runs gain expiry. Invariants: `TenantScope`+`WorkloadScope` bound server-side; `RunId` never trusted as tenant; receipt `Completed`-only writes.
- **G2:** binding-key uniqueness tests; cross-tenant/cross-principal negative reads; provider-mismatch rejection tests; ciphertext-opaque (no plaintext in logs) tests; `Version` overwrite/rollback tests.
- **G3:** immutability tests (no update/delete path); `(TenantId, PublicId)` uniqueness tests; `InternalSessionId` global-uniqueness tests; cross-tenant mismatch→not-found tests; genuine-UUID (no ULID→UUID derivation) tests; caller-side dual-ownership check tests.
- **G4:** `(TenantId, Nonce)` replay-rejection tests; `RequestId` verbatim (non-GUID) round-trip tests; `Guid.Empty`-approval fail-closed tests; approval↔nonce audit-join tests; legacy `command_id/command_digest/consumed_at` → nonce-binding **backfill plan with idempotent rerun**; replacement guarantee for `uq_approval_command` semantics as an explicit test.
- **Cross-cutting (from 001–003 analysis):** alternate keys (`uq_session_tenant_owner`, `uq_task_tenant_principal_id`), composite FKs (sessions→tasks/turns/events `Restrict`; tasks→approvals `Cascade`), `idx_tasks_state`, partial-filter pending indexes, and `jsonb`/`timestamptz` translation behavior must each have an explicit keep/drop decision with test or waiver before any migration bundle is accepted; sentinel/quarantine carryover (sentinel `00000000-0000-0000-0000-000000000001`, `reserved_range ≥ 2_000_000_000`, `jsonb_set` provenance, irreversibility of the ownership backfill) likewise requires an explicit keep/drop decision with operator-runbook test or waiver — all currently **NOT VERIFIED**.

## 4. G4 backfill gap + Navigator receipt path (explicit handling)

- **The gap.** Legacy 003 receipt state (`oc_tasks.receipt_payload jsonb null` + `receipt_status text null`) and approval fencing (`command_id/command_digest/consumed_at`, `uq_approval_command`) have no observed EFCore counterpart: inspected `TaskEntity` carries no receipt properties; inspected `ApprovalEntity` (per matrix) carries no `CommandId`/`ConsumedAt`. Navigator's receipt path (`PostgresDurableNavigatorStore.ConsumeApprovalAndSaveReceiptAsync`, `GetReceiptAsync`, `TrySaveReceiptAsync`) depends on the legacy columns (per matrix §§003/G1; store source not re-read this slice — **NOT VERIFIED** beyond matrix citation). Backfill SQL does not exist in inspected sources.
- **Handling (decision, not implementation).**
  1. **Do not silently drop.** Any migration that omits `command_id`-linkage or receipt columns without the backfill plan below is rejected by this design.
  2. **Backfill plan is mandatory future work:** map each legacy `oc_approvals.command_id` (+ `command_digest`, `consumed_at`, `expires_at`) to an `ApprovalNonces` row (`TenantId`, `Nonce` = legacy `command_id` verbatim, `ApprovalId`, `RequestId` verbatim, `PrincipalId`, `CreatedAtUtc` ← legacy timestamps where defined, `expires_at` → <fail-closed expiry handling: carry as absolute expiry or waive with justification + test — both options open, no decision made here>); specify idempotent rerun (PK `(TenantId, Nonce)` natural dedupe; define behavior for duplicates/conflicts — **open, see §5**); specify treatment of `""`-vs-null and `Guid.Empty` rows as corrupt/absent fail-closed (consistent with entity remarks).
  3. **Receipt carrier must be named explicitly:** either (a) reintroduce receipt columns on the EFCore task model, or (b) designate `OperationalRuns` as the receipt carrier with a stated schema mapping to `contracts/navigator/v1/receipt.json` required fields (`receiptId`, `commandId`, `sessionId`, `principalId`, `tenantId`, `argumentsHash`, `outcome`, `issuedAt`). This design recommends (b) be evaluated first (it preserves the new-capability topology) but does **not** decide it — orchestrator decision required (see §5 Q1). Until decided, Navigator receipt parity is **NOT VERIFIED**.
  4. **Uniqueness replacement:** the backfill plan must state what replaces `uq_approval_command (tenant_id, principal_id, task_id, command_id)` — options include a unique index on the nonce binding and/or a task-scoped constraint — with a contention test. No option is pre-approved here.
  5. **Quarantine/sentinel carryover:** 003's event-ownership backfill (sentinel `00000000-…-000001`, `reserved_range ≥ 2_000_000_000`, `jsonb_set` provenance) has no observed EFCore equivalent; the backfill plan must state keep/drop with operator-runbook consequences. Irreversibility of the ownership backfill carries over by nature.

## 5. Open questions for orchestrator approval

1. Receipt carrier: `OperationalRuns` (recommended for evaluation) vs reintroduced task receipt columns? (Blocks Navigator receipt-path work.)
2. Backfill source-of-truth mapping for `command_id → (TenantId, Nonce)` + `command_digest → BoundArgumentsHash vs argumentsHash` + `consumed_at → ?` + `expires_at → <fail-closed expiry handling: carry as absolute expiry or waive with justification + test — both options open>`, incl. duplicate/conflict and null/empty-row handling? Non-nullable target-field sources: `TenantId ← UNKNOWN (to be defined by backfill plan)`; `ApprovalId ← UNKNOWN (to be defined by backfill plan)`; `RequestId ← UNKNOWN (to be defined by backfill plan)` (must remain verbatim per entity remarks — must NOT be lossy Guid coercion); `PrincipalId ← UNKNOWN (to be defined by backfill plan)`; `CreatedAtUtc ← legacy timestamps where defined, otherwise UNKNOWN (to be defined by backfill plan)`; existing fail-closed corrupt-row rule (`""`-vs-null, `Guid.Empty`) is unchanged.
3. What replaces `uq_approval_command` uniqueness, and what contention test proves it?
4. Retention/sweeper ownership for `OperationalRuns` (and any expiring new-table rows): sweeper-analog required or explicitly waived?
5. Key-management/rotation story + `Ciphertext` size limits for `NativeAgentSessions`?
6. Caller-side dual-ownership enforcement tests for `PublicSessionMappings` resolvers — owned by which slice?
7. Disposition of cross-cutting 001–003 gaps (alternate keys, composite FKs, `idx_tasks_state`, partial pending filters, type translations): keep/drop/waive each before migration-bundle acceptance?
8. Sentinel/quarantine carryover (sentinel `00000000-0000-0000-0000-000000000001`, `reserved_range ≥ 2_000_000_000`, `jsonb_set` provenance keys, irreversibility of the ownership backfill): keep/drop with operator-runbook consequences, with test or waiver before any migration bundle is accepted?

## 6. Explicit NOT VERIFIED list

No build, test, migration-apply, or database run performed. Npgsql vs SQLite translation, .NET 9/10 compatibility, migration ordering/safety, EFCore migration-bundle existence, legacy-data survival, contention equivalence, tenant-isolation negatives, and timing/expiry edges are all **NOT VERIFIED**. Store/test names beyond files listed in §Sources are **NOT VERIFIED**. Contract-header status reconciliation (OC-PERSIST-001 file still reads PROPOSED) is orchestrator-owned.

## 7. Sources

`docs/plans/PERSIST-parity-matrix.md` (full); `docs/contracts/OC-PERSIST-001.md`; `src/OpenConnect.Persistence.EFCore/Entities/{OperationalRun,NativeAgentSession,PublicSessionMapping,ApprovalNonce,Task}Entity.cs`; `contracts/navigator/v1/receipt.json`; matrix-cited but not re-read: `PostgresDurableNavigatorStore.cs`, `PostgresExpiredLeaseSweeper.cs`, `EfLeaseStore.cs`, `OwnershipStores.cs`.
