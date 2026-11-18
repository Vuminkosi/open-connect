# ADR-003: MAF Native Checkpoint Format and Version Scope

**Status:** Accepted

**Date:** 2026-11-09

**Deciders:** Master orchestrator (documentation slice AG-DOC-014; checkpoint behavior per AG-IMPL-016B / AG-LAB-VERIFY-010)

## Context

MAF-016 restart work runs on pinned MAF `1.27.0` (`Microsoft.Agents.AI`,
`Microsoft.Agents.AI.Workflows`, `Microsoft.Agents.AI.Harness`, and
`Microsoft.Agents.AI.Workflows.Declarative.Mcp` all at `1.27.0` in
`Directory.Packages.props`, observed 2026-11-09). Two lab test files record
the observed native checkpoint behavior on that pin:

- `tests/Unit/OpenConnect.Agents.Tests/NativeWorkflowCheckpointTests.cs` —
  commit + retrieve + rehydrate of a native workflow checkpoint.
- `tests/Unit/OpenConnect.Agents.Tests/NativeHitlCheckpointLabTests.cs` —
  checkpointed HITL pause, authorized/denied response handling, and resume
  from a rehydrated checkpoint via `InProcessExecution.ResumeStreamingAsync`.

Both labs use the exact MAF-owned members `CheckpointManager.CreateJson` and
`ICheckpointStore<JsonElement>` (namespace
`Microsoft.Agents.AI.Workflows.Checkpointing`; exact member names verified in
AG-IMPL-016B / AG-LAB-VERIFY-010 and confirmed by re-reading the two lab
files for this ADR). The JSON payload stored through those members is
MAF-owned and opaque to this repo: no test or source in scope asserts on its
internal wire shape, and this ADR does not describe one.

Separately, the repo owns an application-level checkpoint metadata type in
`src/OpenConnect.Agents/Execution/ConversationCheckpointCodec.cs` with wire
version `oc-checkpoint/v1`. That type carries lead/session/turn/children
metadata only (`ConversationCheckpoint`: `Version`, `LeadAgentId`,
`SessionKey`, `TurnSequence`, `ChildSessions`; `ChildSessionCheckpoint`:
`AgentId`, `SessionKey`, `HistoryCount`; bound `MaxCheckpointChildren = 64`)
and explicitly includes no MAF transcript content. Its store and purpose are
distinct from the MAF-native checkpoint store.

Execution evidence supplied for the restart smoke (baseline commit `4b90286`,
restart smoke PASS): `FromSerialized` rehydration preserves the same
`CheckpointId`, and `ResumeStreamingAsync` re-emits the same pending request
(evidence `4/4 + 526/0/1`; commands not re-executed by this documentation
slice).

## Decision

- **MAF-native checkpoint format is MAF-owned opaque JSON**, written and read
  through `CheckpointManager.CreateJson` + `ICheckpointStore<JsonElement>` on
  pinned MAF `1.27.0`. The repo treats the payload as opaque bytes: it stores,
  retrieves, and rehydrates it (via each lab's `FromSerialized` helper) without
  parsing or depending on its internal fields.
- **Stored native checkpoints are version-scoped to the MAF package revision.**
  Minor-version drift in MAF may change the wire shape, so no cross-version
  restore is claimed: a checkpoint written under `1.27.0` is restorable under
  `1.27.0` per the lab evidence; restore under any other MAF version is
  NOT VERIFIED.
- **The native checkpoint store is NOT the same as `oc-checkpoint/v1`.**
  `ConversationCheckpointCodec` (`oc-checkpoint/v1`) is application-owned
  opaque metadata for lead/session/turn/children with its own store and
  purpose; it carries no MAF transcript content and cannot substitute for a
  MAF-native checkpoint, and vice versa. The codec additionally rejects
  cross-version restore of its own metadata (`NotSupportedException` on
  version mismatch; `InvalidOperationException` on lead/session mismatch).
- **Proven scope is limited to what the labs show:** commit + retrieve +
  rehydrate with identical `CheckpointId` (`NativeWorkflowCheckpointTests`),
  and resume via `ResumeStreamingAsync` re-emitting the same pending request
  with no downstream progress (`NativeHitlCheckpointLabTests`,
  `Resumed_run_reemits_pending_request_without_downstream_progress`). Anything
  beyond that (cross-version restore, cross-host portability, wire-format
  stability promises) is NOT VERIFIED.

## Alternatives Considered

1. **Document the MAF checkpoint JSON wire fields — Rejected.** No in-scope
   source or test asserts on the internal wire shape; documenting fields would
   invent claims beyond inspected evidence.
2. **Claim cross-version restore or wire-format stability — Rejected.** No lab
   evidence covers restore under any MAF version other than the pinned
   `1.27.0`; such a claim would be unverified.
3. **Conflate native checkpoints with `oc-checkpoint/v1` — Rejected.** The two
   have distinct owners (MAF vs. application), distinct stores, and distinct
   purposes (workflow state vs. conversation turn/children metadata); merging
   them would mislead implementers.

## Consequences

- Implementers use `CheckpointManager.CreateJson` + `ICheckpointStore<JsonElement>`
  and treat the payload as opaque; no code may depend on undocumented JSON fields.
- Stored native checkpoints must be treated as scoped to MAF `1.27.0` until a
  lab proves otherwise; upgrades require a fresh checkpoint lab before any
  cross-version restore claim.
- `oc-checkpoint/v1` metadata continues to serve conversation resume bookkeeping
  only; MAF workflow resume continues to require the native checkpoint path.
- No checklist is flipped by this ADR (per assignment: checklist flip stays with
  the follow-up slice).

## Evidence

- Canonical repo `C:\DevOps\open-connect`, master HEAD `28ba141` (observed by
  coordinator 2026-11-09; documentation slice did not re-execute tests).
- MAF pin: `Directory.Packages.props` lines 7–9, 25 —
  `Microsoft.Agents.AI.Harness`, `Microsoft.Agents.AI.Workflows`,
  `Microsoft.Agents.AI.Workflows.Declarative.Mcp`, `Microsoft.Agents.AI`, all
  `1.27.0` (inspected 2026-11-09).
- `tests/Unit/OpenConnect.Agents.Tests/NativeWorkflowCheckpointTests.cs`
  (inspected): `CheckpointManager.CreateJson(store)` (line 19),
  `ICheckpointStore<JsonElement>` test store (line 91),
  `FromSerialized` rehydration + `GetLatestCheckpointAsync` returning the same
  `CheckpointId`/`SessionId` (lines 45–52).
- `tests/Unit/OpenConnect.Agents.Tests/NativeHitlCheckpointLabTests.cs`
  (inspected): `CheckpointManager.CreateJson(store)` (lines 30, 58, 85, 122),
  fresh `CheckpointManager.CreateJson(freshStore)` rehydration (line 139),
  `InProcessExecution.ResumeStreamingAsync(resumedWorkflow, restored,
  freshManager, ...)` re-emitting the same pending request with no downstream
  progress (lines 144–159); test-store `ICheckpointStore<JsonElement>` (line 215).
- `src/OpenConnect.Agents/Execution/ConversationCheckpointCodec.cs`
  (inspected): `CurrentVersion = "oc-checkpoint/v1"` (line 18),
  `MaxCheckpointChildren = 64` (line 23), "No MAF transcript content is
  included" (line 25), cross-version rejection (lines 52–55).
- Execution evidence baseline commit `4b90286` (restart smoke PASS:
  `FromSerialized` rehydration, same `CheckpointId`, `ResumeStreamingAsync`
  re-emits same request; evidence `4/4 + 526/0/1`; supplied per task packet,
  commands not re-executed by this slice).
- Format/style reference: `docs/architecture/adr/ADR-001-agent-execution-profiles.md`
  and `docs/architecture/adr/ADR-002-durable-a2a-deferral.md` (inspected for
  structure only; no claims inherited).

## Resumption Condition

Re-evaluate this ADR when the MAF package revision changes or when a new lab
proves restore behavior beyond the current evidence (cross-version restore,
cross-host portability, or wire-shape stability). Until then, stored native
checkpoints remain version-scoped to MAF `1.27.0` and any broader claim stays
NOT VERIFIED.
