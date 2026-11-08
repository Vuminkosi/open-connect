# OpenConnect — Exhaustive MAF-First Implementation Plan

**Version:** 2.1-draft · **Date:** 2026-11-08 · **Status:** normative engineering plan; execution evidence remains NOT RUN until validated.  
**Companions:** `requirements.md`, `spec.md`, `checklist.md`, `agents-requirements.md`, `agents-spec.md`.  
**Traceability:** every one of the **356** `OC2-D-*` tasks plus `OC2-IMP-001–020` and `OC2-AT-001–071`.

## 0. Product and implementation authority

OpenConnect is a **.NET-first production application harness around native Microsoft Agent Framework**. MAF supplies `AIAgent`, Harness Agent, native workflow graphs, streaming, tools, Skills, session/context providers, workflow HITL and selected durable execution. OpenConnect supplies typed composition, identity/workload scoping, application task coordination, policy/egress enforcement, consent/approvals at the effect boundary, durable business outbox, accurate progress and client integrations. **Do not implement a second planner, todo engine, LLM agent loop or workflow replayer.**

The *four execution profiles* (`Direct`, `Harness`, `Workflow`, `DurableWorkflow`) are independent of the *two authority modes* (`Interactive`, `Operational`). Lead delegation depth is 0→1→2, maximum 2 edges. An optional absence is not a global failure except when a configured security/durability policy requires that capability for the requested operation.

### Document ownership

| Document | Authoritative purpose |
|---|---|
| `requirements.md` | What the framework SHALL do (356 explicit mapped requirements) |
| `spec.md` | How contracts, algorithms and data boundaries work (task-by-task §20) |
| **`implementation.md`** | Ordered coding PRs, prerequisite proofs, target artifacts, negative tests and evidence |
| `checklist.md` | Independent task status and accepted execution/CI evidence |
| `agents-requirements.md` / `agents-spec.md` | Agent/harness/workflow and subagent details consistent with the master documents |

Do not revive old PostgreSQL-only persistence, GHCR-only worker registry or custom agent orchestration examples. Preserve their functional intent through EF Core provider-neutral stores, Docker Hub release, and native MAF orchestration. No claim of implementation based solely on these documents.

## 1. Dependency and execution stages

| Stage | Scope | Prerequisite | Release-blocking proof |
|---|---|---|---|
| `S00` | Source governance, MAF/SDK baseline and legal proof; 16 tasks | none | Verify stale document paths or incompatible decisions are detected in doc lint; no quiet overwrites |
| `S01` | Compile and execute native MAF capability laboratory; 22 tasks | S00 | Compile/run positive native MAF execution and a feature-absent or misuse negative case |
| `S02` | Typed composition, normalized catalog and native profiles; 21 tasks | S01 | Exercise fluent + JSON/YAML parity, invalid combination and duplicate-registration failure |
| `S03` | Identity, grants and delegation constraints; 16 tasks | S02 | Test cross-tenant denial, auth revocation, delegated child downgrade and audit-safe errors |
| `S04` | Native tools, approvals, MCP and skills; 25 tasks | S03 | Test unauthorized or new MCP tool, malformed arguments, SSRF and forged approval |
| `S05` | MAF-owned session, context, compaction and memory; 18 tasks | S01,S03,S04 | Test 10-turn restore, concurrent branches, cross-tenant context and memory deletion |
| `S06` | EF-neutral storage and application outbox; 28 tasks | S03 | Test 10-turn restore, concurrent branches, cross-tenant context and memory deletion |
| `S07` | MAF durable workflows and independent operational triggers; 17 tasks | S05,S06 | Crash before/after effect, checkpoint/version mismatch, stale fence and restart without double action |
| `S08` | Truthful durable tools, progress, cancel and resume; 19 tasks | S04,S06,S07 | Fault tests for disconnected client, 100% before commit, cancel race and uncertain external effect |
| `S09` | Host privacy interlock, vault and detector integrations; 23 tasks | S03,S04,S06 | Synthetic canary exfiltration, missing required detector, cross-scope restoration and model-output bypass |
| `S10` | Committed transport and public HTTP/WS/AG-UI; 16 tasks | S06,S08,S09 | Broker restart/redelivery, cursor expiry, WS auth and optional AG-UI interop |
| `S11` | Blazor/TS/Node clients and MiniComposer; 21 tasks | S10 | bUnit/Playwright, keyboard/IME, reconnect, XSS, CSS isolation and no-speech fallback |
| `S12` | Client/server VAD and speech safety; 14 tasks | S11,S09 | ONNX/browser waveform/silence tests, resampling, invalid frames, privacy and interruption |
| `S13` | Optional isolated model workers and production images; 18 tasks | S09,S10 | uv/pytest plus Docker start/stop, checksum mismatch, offline reuse, real model smoke only when advertised |
| `S14` | Navigator, Playwright and .NET worker host composition; 14 tasks | S11,S04 | No-Playwright fallback, selector spoof, SSRF, consent revocation, worker lease and UI capture tests |
| `S15` | Optional semantic memory, KMS, vector subscribers; 11 tasks | S05,S06,S09 | Outage without core regression, tenant isolation, deletion/tombstone and KMS key rotation |
| `S16` | Telemetry, financial metering, evaluations and admin; 17 tasks | S03,S06,S10 | Canary-free telemetry, failed exporter, duplicate attempts, budgets/evaluations and RBAC |
| `S17` | Cross-framework compatibility and clean consumers; 13 tasks | S01-S16 | Clean net8/net9/net10 package consumers and MAF/EF provider compatibility checks |
| `S18` | Release pipelines and documentation; 27 tasks | S01-S17 | No PR publishing, registry clean pulls by digest, SBOM/rollback, doc offline smoke |

## 2. Implementation unit rules
### Standard delivery contract for each atomic task

1. **Inspect first.** Read the matching `OC2-D-*` checklist item, matching `OC2-REQ-*` requirements and `spec.md` §20 contract; inspect existing repository code and the pinned MAF/EF/provider SDK. Record whether relevant API is verified or only proposed.
2. **Write an executable negative case before or alongside implementation.** Security decisions need explicit deny tests; external operations need interrupted, timed-out and duplicate/replay cases; optional capabilities need absent/unready cases.
3. **Implement at the authoritative boundary.** Native MAF executes agents, harnesses, tools, skills and workflow checkpoints. OpenConnect integrates identity, authorization, privacy, app task/outbox, durable effect receipts, client event projections, metering and worker routing. Never introduce a second engine to make the test easier.
4. **Package and compatibility check.** Verify dependency isolation, the supported target-framework subset, cancellation and bounded resources. Record any migration, contract or license implications.
5. **Close with real evidence.** Attach source paths, commit SHA, CI/run URI, exact test command, environment (TFM, database, model, worker or browser), outcome, reviewer, and the related acceptance IDs. Keep checklist unchecked until observed passing evidence.

## 3. Native MAF integration sequence (must happen before OpenConnect API freeze)

1. Pin exact Microsoft Agent Framework package versions and runtime/TFM support in a clean test solution. Record concrete namespaces/types, constructor signatures and which functionality is prerelease or unsupported.
2. Execute a Direct `AIAgent` with an actual deterministic `IChatClient`, native function invocation, streaming output and the expected middleware order.
3. Execute a Harness agent using verified `AsHarnessAgent`/equivalent, confirm todo/planning, tool approval, compaction, Skills and native `AgentSession` save/restore. Do **not** persist an OpenConnect-invented plan JSON as authoritative state.
4. Execute actual MAF sequential, concurrent, handoff and request/HITL workflows. Validate sessions per participant; do not copy all history across branches.
5. Test Durable Extension checkpoint/restore and absent backend behavior. Decide and document exactly one workflow state/retry/lease owner; OpenConnect may own trigger schedules and business outbox, not duplicate graph replay.
6. Only then freeze `AgentExecutionProfile`/`AgentExecutionMode`, typed options and OpenConnect adapters. A missing native API yields an explicit unsupported profile for that pinned version/TFM, not a synthetic substitute.

## 4. Durable tool / workflow continuation protocol

`Proposed → AwaitingApproval? → Accepted → Queued → Starting → Running → Finalizing → Completed|Failed|Cancelled|OutcomeUnknown`. Persist approval before authorization to execute; persist accepted invocation + status event + outbox in **one** DB transaction before a declared long-running tool executes. `tool.started` requires a real execution-start receipt. `tool.progress` requires verified counts/phase; a watchdog emits `tool.heartbeat` only when lease/liveness evidence exists. `100%` is not terminal until result/commit succeeds. Cancellation requested is distinct from cancellation confirmed, and non-idempotent uncertain effects must be reconciled rather than blindly retried. Preserve native MAF continuation/checkpoint authority and map one logical invocation result to the resumed workflow.

## 5. Ordered atomic engineering workstreams


### S00 — Source governance, MAF/SDK baseline and legal proof

**Prerequisites:** `none`. **Checklist section:** 3.1 Source governance, design authority, legal and architectural evidence. **Scope:** 16 atomic deliverables. **Primary source paths:** `docs/architecture/adr/, docs/migration/, .github/CODEOWNERS, AGENTS.md`.

**Authority:** Record immutable versioned decision with owner, affected APIs, supersession and threat/compatibility effect. **Required verification pattern:** Verify stale document paths or incompatible decisions are detected in doc lint; no quiet overwrites.

#### OC2-D-GOV-001 — Create and verify the six canonical docs (requirements/spec/implementation/checklist/agents-requirements/agents-spec); give each a stable version and matching revision note.

1. **Repository and prerequisites:** Work in `docs/architecture/adr/, docs/migration/, .github/CODEOWNERS, AGENTS.md` or the existing equivalent; record the package/adapter owner. Check `none` gate and the current source API/contract version.
2. **Concrete implementation:** Create and verify the six canonical docs (requirements/spec/implementation/checklist/agents-requirements/agents-spec); give each a stable version and matching revision note. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Record immutable versioned decision with owner, affected APIs, supersession and threat/compatibility effect. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Prove commit/rollback behavior against an actual supported relational provider. Inject lost ACK, stale fencing token and concurrent writer where the contract touches durable state.
5. **Acceptance artifact:** Verify stale document paths or incompatible decisions are detected in doc lint; no quiet overwrites. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-GOV-001` only after verified pass.

#### OC2-D-GOV-002 — Define document precedence: requirements behavior; spec interfaces and architecture; implementation ordered work; checklist task and evidence status; agents documents specialize without contradicting the four master files.

1. **Repository and prerequisites:** Work in `docs/architecture/adr/, docs/migration/, .github/CODEOWNERS, AGENTS.md` or the existing equivalent; record the package/adapter owner. Check `none` gate and the current source API/contract version.
2. **Concrete implementation:** Define document precedence: requirements behavior; spec interfaces and architecture; implementation ordered work; checklist task and evidence status; agents documents specialize without contradicting the four master files. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Record immutable versioned decision with owner, affected APIs, supersession and threat/compatibility effect. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Validate generated/readme/code snippet claims against built assemblies or referenced source. Detect references to superseded APIs, stale paths and unsupported capabilities.
5. **Acceptance artifact:** Verify stale document paths or incompatible decisions are detected in doc lint; no quiet overwrites. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-GOV-002` only after verified pass.

#### OC2-D-GOV-003 — Create a single ADR registry covering MAF ownership, harness profile, durable backend, EF transaction ownership, MCP transport, privacy trust zones, event ownership, client protocol and release registries.

1. **Repository and prerequisites:** Work in `docs/architecture/adr/, docs/migration/, .github/CODEOWNERS, AGENTS.md` or the existing equivalent; record the package/adapter owner. Check `none` gate and the current source API/contract version.
2. **Concrete implementation:** Create a single ADR registry covering MAF ownership, harness profile, durable backend, EF transaction ownership, MCP transport, privacy trust zones, event ownership, client protocol and release registries. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Record immutable versioned decision with owner, affected APIs, supersession and threat/compatibility effect. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Prove commit/rollback behavior against an actual supported relational provider. Inject lost ACK, stale fencing token and concurrent writer where the contract touches durable state. Verify unregistered/malicious MCP tool schemas cannot expand permissions, redirect network access or bypass approval.
5. **Acceptance artifact:** Verify stale document paths or incompatible decisions are detected in doc lint; no quiet overwrites. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-GOV-003` only after verified pass.

#### OC2-D-GOV-004 — Record each architecture decision with alternatives, rationale, compatibility impact, approving reviewer and superseded IDs.

1. **Repository and prerequisites:** Work in `docs/architecture/adr/, docs/migration/, .github/CODEOWNERS, AGENTS.md` or the existing equivalent; record the package/adapter owner. Check `none` gate and the current source API/contract version.
2. **Concrete implementation:** Record each architecture decision with alternatives, rationale, compatibility impact, approving reviewer and superseded IDs. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Record immutable versioned decision with owner, affected APIs, supersession and threat/compatibility effect. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Run targeted success and deliberate negative regression in CI. Save machine-readable test artifacts and versioned audit evidence.
5. **Acceptance artifact:** Verify stale document paths or incompatible decisions are detected in doc lint; no quiet overwrites. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-GOV-004` only after verified pass.

#### OC2-D-GOV-005 — Inventory current repository projects and actual public APIs before adding new assemblies; map each proposed package to existing code or a concrete deliverable.

1. **Repository and prerequisites:** Work in `docs/architecture/adr/, docs/migration/, .github/CODEOWNERS, AGENTS.md` or the existing equivalent; record the package/adapter owner. Check `none` gate and the current source API/contract version.
2. **Concrete implementation:** Inventory current repository projects and actual public APIs before adding new assemblies; map each proposed package to existing code or a concrete deliverable. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Record immutable versioned decision with owner, affected APIs, supersession and threat/compatibility effect. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify successful integration with canonical contract fixtures. Inject malformed input, cancellation and unavailable dependency; assert typed error/degraded behavior.
5. **Acceptance artifact:** Verify stale document paths or incompatible decisions are detected in doc lint; no quiet overwrites. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-GOV-005` only after verified pass.

#### OC2-D-GOV-006 — Pin the actual Microsoft Agent Framework packages and NuGet dependency graph for net8, net9 and net10; record exact supported features, not just concepts from documentation.

1. **Repository and prerequisites:** Work in `docs/architecture/adr/, docs/migration/, .github/CODEOWNERS, AGENTS.md` or the existing equivalent; record the package/adapter owner. Check `none` gate and the current source API/contract version.
2. **Concrete implementation:** Pin the actual Microsoft Agent Framework packages and NuGet dependency graph for net8, net9 and net10; record exact supported features, not just concepts from documentation. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Record immutable versioned decision with owner, affected APIs, supersession and threat/compatibility effect. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Validate generated/readme/code snippet claims against built assemblies or referenced source. Detect references to superseded APIs, stale paths and unsupported capabilities. Build and run matching TFM consumers on net8.0/net9.0/net10.0 as claimed, with EOS compatibility labels.
5. **Acceptance artifact:** Verify stale document paths or incompatible decisions are detected in doc lint; no quiet overwrites. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-GOV-006` only after verified pass.

#### OC2-D-GOV-007 — Pin a Swift-Aid reference commit SHA and audit copyright/licensing before adapting code; preserve attribution in legal notices where required.

1. **Repository and prerequisites:** Work in `docs/architecture/adr/, docs/migration/, .github/CODEOWNERS, AGENTS.md` or the existing equivalent; record the package/adapter owner. Check `none` gate and the current source API/contract version.
2. **Concrete implementation:** Pin a Swift-Aid reference commit SHA and audit copyright/licensing before adapting code; preserve attribution in legal notices where required. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Record immutable versioned decision with owner, affected APIs, supersession and threat/compatibility effect. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify successful integration with canonical contract fixtures. Inject malformed input, cancellation and unavailable dependency; assert typed error/degraded behavior.
5. **Acceptance artifact:** Verify stale document paths or incompatible decisions are detected in doc lint; no quiet overwrites. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-GOV-007` only after verified pass.

#### OC2-D-GOV-008 — Build source-to-destination migration ledger for composer, speech/VAD, gateway, NATS, session persistence and classification worker, with each file's disposition.

1. **Repository and prerequisites:** Work in `docs/architecture/adr/, docs/migration/, .github/CODEOWNERS, AGENTS.md` or the existing equivalent; record the package/adapter owner. Check `none` gate and the current source API/contract version.
2. **Concrete implementation:** Build source-to-destination migration ledger for composer, speech/VAD, gateway, NATS, session persistence and classification worker, with each file's disposition. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Record immutable versioned decision with owner, affected APIs, supersession and threat/compatibility effect. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Prove commit/rollback behavior against an actual supported relational provider. Inject lost ACK, stale fencing token and concurrent writer where the contract touches durable state. Check sequence gaps, reconnect epochs and speech/privacy transitions without duplicate accepted turns.
5. **Acceptance artifact:** Verify stale document paths or incompatible decisions are detected in doc lint; no quiet overwrites. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-GOV-008` only after verified pass.

#### OC2-D-GOV-009 — Inventory third-party model weights, ONNX/WASM assets, tokenizer packs, speech voices, code license, commercial-use terms and redistribution restrictions.

1. **Repository and prerequisites:** Work in `docs/architecture/adr/, docs/migration/, .github/CODEOWNERS, AGENTS.md` or the existing equivalent; record the package/adapter owner. Check `none` gate and the current source API/contract version.
2. **Concrete implementation:** Inventory third-party model weights, ONNX/WASM assets, tokenizer packs, speech voices, code license, commercial-use terms and redistribution restrictions. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Record immutable versioned decision with owner, affected APIs, supersession and threat/compatibility effect. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Exercise authorized and explicitly unauthorized principals/tenants; assert no side effect on denial. Verify no credentials, plaintext or raw user content leave the intended trust boundary.
5. **Acceptance artifact:** Verify stale document paths or incompatible decisions are detected in doc lint; no quiet overwrites. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-GOV-009` only after verified pass.

#### OC2-D-GOV-010 — Publish a full trust-boundary threat model for browser, gateway, agents, MAF providers, MCP, SQL, NATS, workers, vault, memory, and observability.

1. **Repository and prerequisites:** Work in `docs/architecture/adr/, docs/migration/, .github/CODEOWNERS, AGENTS.md` or the existing equivalent; record the package/adapter owner. Check `none` gate and the current source API/contract version.
2. **Concrete implementation:** Publish a full trust-boundary threat model for browser, gateway, agents, MAF providers, MCP, SQL, NATS, workers, vault, memory, and observability. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Record immutable versioned decision with owner, affected APIs, supersession and threat/compatibility effect. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Prove commit/rollback behavior against an actual supported relational provider. Inject lost ACK, stale fencing token and concurrent writer where the contract touches durable state. Verify unregistered/malicious MCP tool schemas cannot expand permissions, redirect network access or bypass approval.
5. **Acceptance artifact:** Verify stale document paths or incompatible decisions are detected in doc lint; no quiet overwrites. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-GOV-010` only after verified pass.

#### OC2-D-GOV-011 — Define what counts as a released capability versus experimental, disabled, degraded, blocked or planned; publish it in capability manifests.

1. **Repository and prerequisites:** Work in `docs/architecture/adr/, docs/migration/, .github/CODEOWNERS, AGENTS.md` or the existing equivalent; record the package/adapter owner. Check `none` gate and the current source API/contract version.
2. **Concrete implementation:** Define what counts as a released capability versus experimental, disabled, degraded, blocked or planned; publish it in capability manifests. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Record immutable versioned decision with owner, affected APIs, supersession and threat/compatibility effect. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Prove commit/rollback behavior against an actual supported relational provider. Inject lost ACK, stale fencing token and concurrent writer where the contract touches durable state.
5. **Acceptance artifact:** Verify stale document paths or incompatible decisions are detected in doc lint; no quiet overwrites. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-GOV-011` only after verified pass.

#### OC2-D-GOV-012 — Create reproducible baseline solution/CI with global.json, central package versions, lockfiles, .editorconfig and deterministic restore.

1. **Repository and prerequisites:** Work in `docs/architecture/adr/, docs/migration/, .github/CODEOWNERS, AGENTS.md` or the existing equivalent; record the package/adapter owner. Check `none` gate and the current source API/contract version.
2. **Concrete implementation:** Create reproducible baseline solution/CI with global.json, central package versions, lockfiles, .editorconfig and deterministic restore. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Record immutable versioned decision with owner, affected APIs, supersession and threat/compatibility effect. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Run targeted success and deliberate negative regression in CI. Save machine-readable test artifacts and versioned audit evidence.
5. **Acceptance artifact:** Verify stale document paths or incompatible decisions are detected in doc lint; no quiet overwrites. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-GOV-012` only after verified pass.

#### OC2-D-GOV-013 — Set CODEOWNERS and mandatory review for security, workflow graph, public protocol, migrations, release pipeline and external source incorporation.

1. **Repository and prerequisites:** Work in `docs/architecture/adr/, docs/migration/, .github/CODEOWNERS, AGENTS.md` or the existing equivalent; record the package/adapter owner. Check `none` gate and the current source API/contract version.
2. **Concrete implementation:** Set CODEOWNERS and mandatory review for security, workflow graph, public protocol, migrations, release pipeline and external source incorporation. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Record immutable versioned decision with owner, affected APIs, supersession and threat/compatibility effect. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Prove commit/rollback behavior against an actual supported relational provider. Inject lost ACK, stale fencing token and concurrent writer where the contract touches durable state.
5. **Acceptance artifact:** Verify stale document paths or incompatible decisions are detected in doc lint; no quiet overwrites. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-GOV-013` only after verified pass.

#### OC2-D-GOV-014 — Prohibit secrets, personal data, hardcoded reference-app branding and unlicensed weights in packages, documentation, fixture recordings and container layers.

1. **Repository and prerequisites:** Work in `docs/architecture/adr/, docs/migration/, .github/CODEOWNERS, AGENTS.md` or the existing equivalent; record the package/adapter owner. Check `none` gate and the current source API/contract version.
2. **Concrete implementation:** Prohibit secrets, personal data, hardcoded reference-app branding and unlicensed weights in packages, documentation, fixture recordings and container layers. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Record immutable versioned decision with owner, affected APIs, supersession and threat/compatibility effect. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Validate generated/readme/code snippet claims against built assemblies or referenced source. Detect references to superseded APIs, stale paths and unsupported capabilities.
5. **Acceptance artifact:** Verify stale document paths or incompatible decisions are detected in doc lint; no quiet overwrites. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-GOV-014` only after verified pass.

#### OC2-D-GOV-015 — Create an append-only verification ledger containing task ID, requirement IDs, test ID, commit, reviewer, environment, exact command, outcome and artifacts.

1. **Repository and prerequisites:** Work in `docs/architecture/adr/, docs/migration/, .github/CODEOWNERS, AGENTS.md` or the existing equivalent; record the package/adapter owner. Check `none` gate and the current source API/contract version.
2. **Concrete implementation:** Create an append-only verification ledger containing task ID, requirement IDs, test ID, commit, reviewer, environment, exact command, outcome and artifacts. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Record immutable versioned decision with owner, affected APIs, supersession and threat/compatibility effect. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Run targeted success and deliberate negative regression in CI. Save machine-readable test artifacts and versioned audit evidence.
5. **Acceptance artifact:** Verify stale document paths or incompatible decisions are detected in doc lint; no quiet overwrites. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-GOV-015` only after verified pass.

#### OC2-D-GOV-016 — Audit prior reported PASS evidence against actual repo commit/CI; preserve evidence as historically claimed until verified and never automatically promote it to v2 PASS.

1. **Repository and prerequisites:** Work in `docs/architecture/adr/, docs/migration/, .github/CODEOWNERS, AGENTS.md` or the existing equivalent; record the package/adapter owner. Check `none` gate and the current source API/contract version.
2. **Concrete implementation:** Audit prior reported PASS evidence against actual repo commit/CI; preserve evidence as historically claimed until verified and never automatically promote it to v2 PASS. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Record immutable versioned decision with owner, affected APIs, supersession and threat/compatibility effect. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Run targeted success and deliberate negative regression in CI. Save machine-readable test artifacts and versioned audit evidence.
5. **Acceptance artifact:** Verify stale document paths or incompatible decisions are detected in doc lint; no quiet overwrites. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-GOV-016` only after verified pass.


### S01 — Compile and execute native MAF capability laboratory

**Prerequisites:** `S00`. **Checklist section:** 3.2 MAF native capability audit and executable integration laboratory. **Scope:** 22 atomic deliverables. **Primary source paths:** `tests/MafCompatibility/, src/OpenConnect.Agents.Microsoft/, Directory.Packages.props`.

**Authority:** Invoke the real supported MAF type, preserve native session/workflow ownership, and record exact package/version/TFM. Never substitute a simulated OpenConnect agent loop. **Required verification pattern:** Compile/run positive native MAF execution and a feature-absent or misuse negative case.

#### OC2-D-MAF-001 — Create standalone compile/run sample using a real MAF AIAgent and deterministic IChatClient; capture streaming text and structured updates.

1. **Repository and prerequisites:** Work in `tests/MafCompatibility/, src/OpenConnect.Agents.Microsoft/, Directory.Packages.props` or the existing equivalent; record the package/adapter owner. Check `S00` gate and the current source API/contract version.
2. **Concrete implementation:** Create standalone compile/run sample using a real MAF AIAgent and deterministic IChatClient; capture streaming text and structured updates. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Invoke the real supported MAF type, preserve native session/workflow ownership, and record exact package/version/TFM. Never substitute a simulated OpenConnect agent loop. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Invoke an actual pinned MAF type and inspect real lifecycle/session data. Prove unsupported versions/profiles report unavailable instead of fabricating equivalent behavior.
5. **Acceptance artifact:** Compile/run positive native MAF execution and a feature-absent or misuse negative case. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-MAF-001` only after verified pass.

#### OC2-D-MAF-002 — Verify MAF native function-tool invocation, tool result handling and middleware invocation order against pinned assemblies.

1. **Repository and prerequisites:** Work in `tests/MafCompatibility/, src/OpenConnect.Agents.Microsoft/, Directory.Packages.props` or the existing equivalent; record the package/adapter owner. Check `S00` gate and the current source API/contract version.
2. **Concrete implementation:** Verify MAF native function-tool invocation, tool result handling and middleware invocation order against pinned assemblies. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Invoke the real supported MAF type, preserve native session/workflow ownership, and record exact package/version/TFM. Never substitute a simulated OpenConnect agent loop. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Invoke an actual pinned MAF type and inspect real lifecycle/session data. Prove unsupported versions/profiles report unavailable instead of fabricating equivalent behavior.
5. **Acceptance artifact:** Compile/run positive native MAF execution and a feature-absent or misuse negative case. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-MAF-002` only after verified pass.

#### OC2-D-MAF-003 — Create a native HarnessAgent using AsHarnessAgent from a real IChatClient and verify documented configuration hooks.

1. **Repository and prerequisites:** Work in `tests/MafCompatibility/, src/OpenConnect.Agents.Microsoft/, Directory.Packages.props` or the existing equivalent; record the package/adapter owner. Check `S00` gate and the current source API/contract version.
2. **Concrete implementation:** Create a native HarnessAgent using AsHarnessAgent from a real IChatClient and verify documented configuration hooks. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Invoke the real supported MAF type, preserve native session/workflow ownership, and record exact package/version/TFM. Never substitute a simulated OpenConnect agent loop. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Validate generated/readme/code snippet claims against built assemblies or referenced source. Detect references to superseded APIs, stale paths and unsupported capabilities.
5. **Acceptance artifact:** Compile/run positive native MAF execution and a feature-absent or misuse negative case. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-MAF-003` only after verified pass.

#### OC2-D-MAF-004 — Run a native Harness plan/execute sequence and inspect actual plan and todo state events without simulated OpenConnect replacements.

1. **Repository and prerequisites:** Work in `tests/MafCompatibility/, src/OpenConnect.Agents.Microsoft/, Directory.Packages.props` or the existing equivalent; record the package/adapter owner. Check `S00` gate and the current source API/contract version.
2. **Concrete implementation:** Run a native Harness plan/execute sequence and inspect actual plan and todo state events without simulated OpenConnect replacements. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Invoke the real supported MAF type, preserve native session/workflow ownership, and record exact package/version/TFM. Never substitute a simulated OpenConnect agent loop. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Invoke an actual pinned MAF type and inspect real lifecycle/session data. Prove unsupported versions/profiles report unavailable instead of fabricating equivalent behavior.
5. **Acceptance artifact:** Compile/run positive native MAF execution and a feature-absent or misuse negative case. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-MAF-004` only after verified pass.

#### OC2-D-MAF-005 — Verify Harness state survives additional turns in one AgentSession and document ownership of plan/todo/history.

1. **Repository and prerequisites:** Work in `tests/MafCompatibility/, src/OpenConnect.Agents.Microsoft/, Directory.Packages.props` or the existing equivalent; record the package/adapter owner. Check `S00` gate and the current source API/contract version.
2. **Concrete implementation:** Verify Harness state survives additional turns in one AgentSession and document ownership of plan/todo/history. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Invoke the real supported MAF type, preserve native session/workflow ownership, and record exact package/version/TFM. Never substitute a simulated OpenConnect agent loop. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Validate generated/readme/code snippet claims against built assemblies or referenced source. Detect references to superseded APIs, stale paths and unsupported capabilities.
5. **Acceptance artifact:** Compile/run positive native MAF execution and a feature-absent or misuse negative case. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-MAF-005` only after verified pass.

#### OC2-D-MAF-006 — Verify Harness compaction against a constrained context-window fixture and record source/native state changes.

1. **Repository and prerequisites:** Work in `tests/MafCompatibility/, src/OpenConnect.Agents.Microsoft/, Directory.Packages.props` or the existing equivalent; record the package/adapter owner. Check `S00` gate and the current source API/contract version.
2. **Concrete implementation:** Verify Harness compaction against a constrained context-window fixture and record source/native state changes. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Invoke the real supported MAF type, preserve native session/workflow ownership, and record exact package/version/TFM. Never substitute a simulated OpenConnect agent loop. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Invoke an actual pinned MAF type and inspect real lifecycle/session data. Prove unsupported versions/profiles report unavailable instead of fabricating equivalent behavior.
5. **Acceptance artifact:** Compile/run positive native MAF execution and a feature-absent or misuse negative case. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-MAF-006` only after verified pass.

#### OC2-D-MAF-007 — Verify skill source loading, metadata and progressive disclosure under the chosen native MAF skill API; document unsupported targets.

1. **Repository and prerequisites:** Work in `tests/MafCompatibility/, src/OpenConnect.Agents.Microsoft/, Directory.Packages.props` or the existing equivalent; record the package/adapter owner. Check `S00` gate and the current source API/contract version.
2. **Concrete implementation:** Verify skill source loading, metadata and progressive disclosure under the chosen native MAF skill API; document unsupported targets. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Invoke the real supported MAF type, preserve native session/workflow ownership, and record exact package/version/TFM. Never substitute a simulated OpenConnect agent loop. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Validate generated/readme/code snippet claims against built assemblies or referenced source. Detect references to superseded APIs, stale paths and unsupported capabilities.
5. **Acceptance artifact:** Compile/run positive native MAF execution and a feature-absent or misuse negative case. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-MAF-007` only after verified pass.

#### OC2-D-MAF-008 — Verify Harness file-access capability and its sandbox/allowlist boundaries; deny unsafe host filesystem access by default.

1. **Repository and prerequisites:** Work in `tests/MafCompatibility/, src/OpenConnect.Agents.Microsoft/, Directory.Packages.props` or the existing equivalent; record the package/adapter owner. Check `S00` gate and the current source API/contract version.
2. **Concrete implementation:** Verify Harness file-access capability and its sandbox/allowlist boundaries; deny unsafe host filesystem access by default. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Invoke the real supported MAF type, preserve native session/workflow ownership, and record exact package/version/TFM. Never substitute a simulated OpenConnect agent loop. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Exercise authorized and explicitly unauthorized principals/tenants; assert no side effect on denial. Verify no credentials, plaintext or raw user content leave the intended trust boundary.
5. **Acceptance artifact:** Compile/run positive native MAF execution and a feature-absent or misuse negative case. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-MAF-008` only after verified pass.

#### OC2-D-MAF-009 — Verify native Harness tool-approval prompt and dont-ask-again behavior; record where OpenConnect policy must override or narrow approvals.

1. **Repository and prerequisites:** Work in `tests/MafCompatibility/, src/OpenConnect.Agents.Microsoft/, Directory.Packages.props` or the existing equivalent; record the package/adapter owner. Check `S00` gate and the current source API/contract version.
2. **Concrete implementation:** Verify native Harness tool-approval prompt and dont-ask-again behavior; record where OpenConnect policy must override or narrow approvals. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Invoke the real supported MAF type, preserve native session/workflow ownership, and record exact package/version/TFM. Never substitute a simulated OpenConnect agent loop. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Exercise authorized and explicitly unauthorized principals/tenants; assert no side effect on denial. Verify no credentials, plaintext or raw user content leave the intended trust boundary.
5. **Acceptance artifact:** Compile/run positive native MAF execution and a feature-absent or misuse negative case. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-MAF-009` only after verified pass.

#### OC2-D-MAF-010 — Verify native Harness background agent/delegation operation and test constrained child scopes against OpenConnect depth-2 ceiling.

1. **Repository and prerequisites:** Work in `tests/MafCompatibility/, src/OpenConnect.Agents.Microsoft/, Directory.Packages.props` or the existing equivalent; record the package/adapter owner. Check `S00` gate and the current source API/contract version.
2. **Concrete implementation:** Verify native Harness background agent/delegation operation and test constrained child scopes against OpenConnect depth-2 ceiling. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Invoke the real supported MAF type, preserve native session/workflow ownership, and record exact package/version/TFM. Never substitute a simulated OpenConnect agent loop. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Exercise authorized and explicitly unauthorized principals/tenants; assert no side effect on denial. Verify no credentials, plaintext or raw user content leave the intended trust boundary.
5. **Acceptance artifact:** Compile/run positive native MAF execution and a feature-absent or misuse negative case. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-MAF-010` only after verified pass.

#### OC2-D-MAF-011 — Build a MAF typed functional workflow using real executor bind and WorkflowBuilder edges.

1. **Repository and prerequisites:** Work in `tests/MafCompatibility/, src/OpenConnect.Agents.Microsoft/, Directory.Packages.props` or the existing equivalent; record the package/adapter owner. Check `S00` gate and the current source API/contract version.
2. **Concrete implementation:** Build a MAF typed functional workflow using real executor bind and WorkflowBuilder edges. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Invoke the real supported MAF type, preserve native session/workflow ownership, and record exact package/version/TFM. Never substitute a simulated OpenConnect agent loop. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Invoke an actual pinned MAF type and inspect real lifecycle/session data. Prove unsupported versions/profiles report unavailable instead of fabricating equivalent behavior.
5. **Acceptance artifact:** Compile/run positive native MAF execution and a feature-absent or misuse negative case. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-MAF-011` only after verified pass.

#### OC2-D-MAF-012 — Build a native sequential agent workflow and verify actual step ordering, inputs and outputs.

1. **Repository and prerequisites:** Work in `tests/MafCompatibility/, src/OpenConnect.Agents.Microsoft/, Directory.Packages.props` or the existing equivalent; record the package/adapter owner. Check `S00` gate and the current source API/contract version.
2. **Concrete implementation:** Build a native sequential agent workflow and verify actual step ordering, inputs and outputs. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Invoke the real supported MAF type, preserve native session/workflow ownership, and record exact package/version/TFM. Never substitute a simulated OpenConnect agent loop. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Invoke an actual pinned MAF type and inspect real lifecycle/session data. Prove unsupported versions/profiles report unavailable instead of fabricating equivalent behavior.
5. **Acceptance artifact:** Compile/run positive native MAF execution and a feature-absent or misuse negative case. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-MAF-012` only after verified pass.

#### OC2-D-MAF-013 — Build a native concurrent workflow and verify typed merge/fan-in, branch cancellation and no shared unsafe mutable state.

1. **Repository and prerequisites:** Work in `tests/MafCompatibility/, src/OpenConnect.Agents.Microsoft/, Directory.Packages.props` or the existing equivalent; record the package/adapter owner. Check `S00` gate and the current source API/contract version.
2. **Concrete implementation:** Build a native concurrent workflow and verify typed merge/fan-in, branch cancellation and no shared unsafe mutable state. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Invoke the real supported MAF type, preserve native session/workflow ownership, and record exact package/version/TFM. Never substitute a simulated OpenConnect agent loop. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Prove commit/rollback behavior against an actual supported relational provider. Inject lost ACK, stale fencing token and concurrent writer where the contract touches durable state.
5. **Acceptance artifact:** Compile/run positive native MAF execution and a feature-absent or misuse negative case. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-MAF-013` only after verified pass.

#### OC2-D-MAF-014 — Build native handoff/interactive multi-turn workflow and identify participant session ownership and request-port semantics.

1. **Repository and prerequisites:** Work in `tests/MafCompatibility/, src/OpenConnect.Agents.Microsoft/, Directory.Packages.props` or the existing equivalent; record the package/adapter owner. Check `S00` gate and the current source API/contract version.
2. **Concrete implementation:** Build native handoff/interactive multi-turn workflow and identify participant session ownership and request-port semantics. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Invoke the real supported MAF type, preserve native session/workflow ownership, and record exact package/version/TFM. Never substitute a simulated OpenConnect agent loop. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Invoke an actual pinned MAF type and inspect real lifecycle/session data. Prove unsupported versions/profiles report unavailable instead of fabricating equivalent behavior.
5. **Acceptance artifact:** Compile/run positive native MAF execution and a feature-absent or misuse negative case. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-MAF-014` only after verified pass.

#### OC2-D-MAF-015 — Verify native human-in-the-loop checkpoint pause/restore and that pending action is not executed while unapproved.

1. **Repository and prerequisites:** Work in `tests/MafCompatibility/, src/OpenConnect.Agents.Microsoft/, Directory.Packages.props` or the existing equivalent; record the package/adapter owner. Check `S00` gate and the current source API/contract version.
2. **Concrete implementation:** Verify native human-in-the-loop checkpoint pause/restore and that pending action is not executed while unapproved. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Invoke the real supported MAF type, preserve native session/workflow ownership, and record exact package/version/TFM. Never substitute a simulated OpenConnect agent loop. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify disabled/uninstalled, ready, startup failure and shutdown behavior independently. Validate model/asset manifest hash, license, actual capabilities and bounded resources where applicable.
5. **Acceptance artifact:** Compile/run positive native MAF execution and a feature-absent or misuse negative case. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-MAF-015` only after verified pass.

#### OC2-D-MAF-016 — Run native workflow checkpoint restart smoke and document checkpoint format/version constraints.

1. **Repository and prerequisites:** Work in `tests/MafCompatibility/, src/OpenConnect.Agents.Microsoft/, Directory.Packages.props` or the existing equivalent; record the package/adapter owner. Check `S00` gate and the current source API/contract version.
2. **Concrete implementation:** Run native workflow checkpoint restart smoke and document checkpoint format/version constraints. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Invoke the real supported MAF type, preserve native session/workflow ownership, and record exact package/version/TFM. Never substitute a simulated OpenConnect agent loop. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify disabled/uninstalled, ready, startup failure and shutdown behavior independently. Validate model/asset manifest hash, license, actual capabilities and bounded resources where applicable.
5. **Acceptance artifact:** Compile/run positive native MAF execution and a feature-absent or misuse negative case. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-MAF-016` only after verified pass.

#### OC2-D-MAF-017 — Evaluate Microsoft Agent Framework Durable Extension on a real configured durable store, or mark backend unsupported with compile/test evidence.

1. **Repository and prerequisites:** Work in `tests/MafCompatibility/, src/OpenConnect.Agents.Microsoft/, Directory.Packages.props` or the existing equivalent; record the package/adapter owner. Check `S00` gate and the current source API/contract version.
2. **Concrete implementation:** Evaluate Microsoft Agent Framework Durable Extension on a real configured durable store, or mark backend unsupported with compile/test evidence. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Invoke the real supported MAF type, preserve native session/workflow ownership, and record exact package/version/TFM. Never substitute a simulated OpenConnect agent loop. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Invoke an actual pinned MAF type and inspect real lifecycle/session data. Prove unsupported versions/profiles report unavailable instead of fabricating equivalent behavior.
5. **Acceptance artifact:** Compile/run positive native MAF execution and a feature-absent or misuse negative case. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-MAF-017` only after verified pass.

#### OC2-D-MAF-018 — Verify MAF hosting registration, keyed agent services, workflow-as-agent support and its compatibility with OpenConnect hosting.

1. **Repository and prerequisites:** Work in `tests/MafCompatibility/, src/OpenConnect.Agents.Microsoft/, Directory.Packages.props` or the existing equivalent; record the package/adapter owner. Check `S00` gate and the current source API/contract version.
2. **Concrete implementation:** Verify MAF hosting registration, keyed agent services, workflow-as-agent support and its compatibility with OpenConnect hosting. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Invoke the real supported MAF type, preserve native session/workflow ownership, and record exact package/version/TFM. Never substitute a simulated OpenConnect agent loop. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Invoke an actual pinned MAF type and inspect real lifecycle/session data. Prove unsupported versions/profiles report unavailable instead of fabricating equivalent behavior.
5. **Acceptance artifact:** Compile/run positive native MAF execution and a feature-absent or misuse negative case. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-MAF-018` only after verified pass.

#### OC2-D-MAF-019 — Inspect MAF MCP integration, remote A2A agent adapters and AG-UI protocol adapters; classify reuse, extension and incompatibilities.

1. **Repository and prerequisites:** Work in `tests/MafCompatibility/, src/OpenConnect.Agents.Microsoft/, Directory.Packages.props` or the existing equivalent; record the package/adapter owner. Check `S00` gate and the current source API/contract version.
2. **Concrete implementation:** Inspect MAF MCP integration, remote A2A agent adapters and AG-UI protocol adapters; classify reuse, extension and incompatibilities. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Invoke the real supported MAF type, preserve native session/workflow ownership, and record exact package/version/TFM. Never substitute a simulated OpenConnect agent loop. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Invoke an actual pinned MAF type and inspect real lifecycle/session data. Prove unsupported versions/profiles report unavailable instead of fabricating equivalent behavior. Verify unregistered/malicious MCP tool schemas cannot expand permissions, redirect network access or bypass approval.
5. **Acceptance artifact:** Compile/run positive native MAF execution and a feature-absent or misuse negative case. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-MAF-019` only after verified pass.

#### OC2-D-MAF-020 — Measure which MAF-native metrics/ActivitySources and evaluation hooks are reusable; prevent duplicate instrumentation.

1. **Repository and prerequisites:** Work in `tests/MafCompatibility/, src/OpenConnect.Agents.Microsoft/, Directory.Packages.props` or the existing equivalent; record the package/adapter owner. Check `S00` gate and the current source API/contract version.
2. **Concrete implementation:** Measure which MAF-native metrics/ActivitySources and evaluation hooks are reusable; prevent duplicate instrumentation. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Invoke the real supported MAF type, preserve native session/workflow ownership, and record exact package/version/TFM. Never substitute a simulated OpenConnect agent loop. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Invoke an actual pinned MAF type and inspect real lifecycle/session data. Prove unsupported versions/profiles report unavailable instead of fabricating equivalent behavior.
5. **Acceptance artifact:** Compile/run positive native MAF execution and a feature-absent or misuse negative case. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-MAF-020` only after verified pass.

#### OC2-D-MAF-021 — Produce a MAF capability-adoption matrix with exact assembly/API, available TFM, test case, OpenConnect integration point and feature flag.

1. **Repository and prerequisites:** Work in `tests/MafCompatibility/, src/OpenConnect.Agents.Microsoft/, Directory.Packages.props` or the existing equivalent; record the package/adapter owner. Check `S00` gate and the current source API/contract version.
2. **Concrete implementation:** Produce a MAF capability-adoption matrix with exact assembly/API, available TFM, test case, OpenConnect integration point and feature flag. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Invoke the real supported MAF type, preserve native session/workflow ownership, and record exact package/version/TFM. Never substitute a simulated OpenConnect agent loop. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Invoke an actual pinned MAF type and inspect real lifecycle/session data. Prove unsupported versions/profiles report unavailable instead of fabricating equivalent behavior.
5. **Acceptance artifact:** Compile/run positive native MAF execution and a feature-absent or misuse negative case. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-MAF-021` only after verified pass.

#### OC2-D-MAF-022 — Fail startup or selected profile explicitly when required native MAF capability is unsupported; no fake Harness/Workflow execution.

1. **Repository and prerequisites:** Work in `tests/MafCompatibility/, src/OpenConnect.Agents.Microsoft/, Directory.Packages.props` or the existing equivalent; record the package/adapter owner. Check `S00` gate and the current source API/contract version.
2. **Concrete implementation:** Fail startup or selected profile explicitly when required native MAF capability is unsupported; no fake Harness/Workflow execution. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Invoke the real supported MAF type, preserve native session/workflow ownership, and record exact package/version/TFM. Never substitute a simulated OpenConnect agent loop. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Invoke an actual pinned MAF type and inspect real lifecycle/session data. Prove unsupported versions/profiles report unavailable instead of fabricating equivalent behavior.
5. **Acceptance artifact:** Compile/run positive native MAF execution and a feature-absent or misuse negative case. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-MAF-022` only after verified pass.


### S02 — Typed composition, normalized catalog and native profiles

**Prerequisites:** `S01`. **Checklist section:** 3.3 Strongly typed DI registration, configuration, templates and compiled profiles. **Scope:** 21 atomic deliverables. **Primary source paths:** `src/OpenConnect.Abstractions/, src/OpenConnect.Core/, contracts/config/, examples/`.

**Authority:** Bind one immutable typed configuration snapshot, validate all referenced IDs/capabilities, and reject invalid enums or grant escalation before runtime. **Required verification pattern:** Exercise fluent + JSON/YAML parity, invalid combination and duplicate-registration failure.

#### OC2-D-CONFIG-001 — Define a single canonical enum/type assembly for execution profile Direct/Harness/Workflow/DurableWorkflow and identity mode Interactive/Operational.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Abstractions/, src/OpenConnect.Core/, contracts/config/, examples/` or the existing equivalent; record the package/adapter owner. Check `S01` gate and the current source API/contract version.
2. **Concrete implementation:** Define a single canonical enum/type assembly for execution profile Direct/Harness/Workflow/DurableWorkflow and identity mode Interactive/Operational. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Bind one immutable typed configuration snapshot, validate all referenced IDs/capabilities, and reject invalid enums or grant escalation before runtime. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Invoke an actual pinned MAF type and inspect real lifecycle/session data. Prove unsupported versions/profiles report unavailable instead of fabricating equivalent behavior.
5. **Acceptance artifact:** Exercise fluent + JSON/YAML parity, invalid combination and duplicate-registration failure. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-CONFIG-001` only after verified pass.

#### OC2-D-CONFIG-002 — Enforce profile and identity as orthogonal options; reject invalid lifecycle + authority combinations before first request.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Abstractions/, src/OpenConnect.Core/, contracts/config/, examples/` or the existing equivalent; record the package/adapter owner. Check `S01` gate and the current source API/contract version.
2. **Concrete implementation:** Enforce profile and identity as orthogonal options; reject invalid lifecycle + authority combinations before first request. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Bind one immutable typed configuration snapshot, validate all referenced IDs/capabilities, and reject invalid enums or grant escalation before runtime. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify successful integration with canonical contract fixtures. Inject malformed input, cancellation and unavailable dependency; assert typed error/degraded behavior.
5. **Acceptance artifact:** Exercise fluent + JSON/YAML parity, invalid combination and duplicate-registration failure. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-CONFIG-002` only after verified pass.

#### OC2-D-CONFIG-003 — Define stable kebab-case wire names and reject unknown or numeric enum values in JSON, YAML, C# fixture imports and TS/Pydantic parity checks.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Abstractions/, src/OpenConnect.Core/, contracts/config/, examples/` or the existing equivalent; record the package/adapter owner. Check `S01` gate and the current source API/contract version.
2. **Concrete implementation:** Define stable kebab-case wire names and reject unknown or numeric enum values in JSON, YAML, C# fixture imports and TS/Pydantic parity checks. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Bind one immutable typed configuration snapshot, validate all referenced IDs/capabilities, and reject invalid enums or grant escalation before runtime. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Prove commit/rollback behavior against an actual supported relational provider. Inject lost ACK, stale fencing token and concurrent writer where the contract touches durable state.
5. **Acceptance artifact:** Exercise fluent + JSON/YAML parity, invalid combination and duplicate-registration failure. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-CONFIG-003` only after verified pass.

#### OC2-D-CONFIG-004 — Implement AddOpenConnect registration that creates native MAF default interactive lead and host privacy policy with no DB, Docker or model side effects.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Abstractions/, src/OpenConnect.Core/, contracts/config/, examples/` or the existing equivalent; record the package/adapter owner. Check `S01` gate and the current source API/contract version.
2. **Concrete implementation:** Implement AddOpenConnect registration that creates native MAF default interactive lead and host privacy policy with no DB, Docker or model side effects. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Bind one immutable typed configuration snapshot, validate all referenced IDs/capabilities, and reject invalid enums or grant escalation before runtime. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Exercise authorized and explicitly unauthorized principals/tenants; assert no side effect on denial. Verify no credentials, plaintext or raw user content leave the intended trust boundary.
5. **Acceptance artifact:** Exercise fluent + JSON/YAML parity, invalid combination and duplicate-registration failure. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-CONFIG-004` only after verified pass.

#### OC2-D-CONFIG-005 — Ensure AddOpenConnect is idempotent and conflict-aware; repeated different policy registrations fail with precise diagnostics.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Abstractions/, src/OpenConnect.Core/, contracts/config/, examples/` or the existing equivalent; record the package/adapter owner. Check `S01` gate and the current source API/contract version.
2. **Concrete implementation:** Ensure AddOpenConnect is idempotent and conflict-aware; repeated different policy registrations fail with precise diagnostics. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Bind one immutable typed configuration snapshot, validate all referenced IDs/capabilities, and reject invalid enums or grant escalation before runtime. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Run targeted success and deliberate negative regression in CI. Save machine-readable test artifacts and versioned audit evidence.
5. **Acceptance artifact:** Exercise fluent + JSON/YAML parity, invalid combination and duplicate-registration failure. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-CONFIG-005` only after verified pass.

#### OC2-D-CONFIG-006 — Implement validated AddOpenConnectLeadAgent, AddOpenConnectAgent and template APIs that reuse the MAF hosting builder where appropriate.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Abstractions/, src/OpenConnect.Core/, contracts/config/, examples/` or the existing equivalent; record the package/adapter owner. Check `S01` gate and the current source API/contract version.
2. **Concrete implementation:** Implement validated AddOpenConnectLeadAgent, AddOpenConnectAgent and template APIs that reuse the MAF hosting builder where appropriate. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Bind one immutable typed configuration snapshot, validate all referenced IDs/capabilities, and reject invalid enums or grant escalation before runtime. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Invoke an actual pinned MAF type and inspect real lifecycle/session data. Prove unsupported versions/profiles report unavailable instead of fabricating equivalent behavior.
5. **Acceptance artifact:** Exercise fluent + JSON/YAML parity, invalid combination and duplicate-registration failure. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-CONFIG-006` only after verified pass.

#### OC2-D-CONFIG-007 — Implement typed template definitions with stable ID, revision, instructions, capability requirements, model route, tools, budgets and context sharing.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Abstractions/, src/OpenConnect.Core/, contracts/config/, examples/` or the existing equivalent; record the package/adapter owner. Check `S01` gate and the current source API/contract version.
2. **Concrete implementation:** Implement typed template definitions with stable ID, revision, instructions, capability requirements, model route, tools, budgets and context sharing. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Bind one immutable typed configuration snapshot, validate all referenced IDs/capabilities, and reject invalid enums or grant escalation before runtime. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Prove commit/rollback behavior against an actual supported relational provider. Inject lost ACK, stale fencing token and concurrent writer where the contract touches durable state.
5. **Acceptance artifact:** Exercise fluent + JSON/YAML parity, invalid combination and duplicate-registration failure. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-CONFIG-007` only after verified pass.

#### OC2-D-CONFIG-008 — Support deterministic single-base template inheritance, including policy narrowing and prevention of privilege expansion by derived definitions.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Abstractions/, src/OpenConnect.Core/, contracts/config/, examples/` or the existing equivalent; record the package/adapter owner. Check `S01` gate and the current source API/contract version.
2. **Concrete implementation:** Support deterministic single-base template inheritance, including policy narrowing and prevention of privilege expansion by derived definitions. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Bind one immutable typed configuration snapshot, validate all referenced IDs/capabilities, and reject invalid enums or grant escalation before runtime. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify successful integration with canonical contract fixtures. Inject malformed input, cancellation and unavailable dependency; assert typed error/degraded behavior.
5. **Acceptance artifact:** Exercise fluent + JSON/YAML parity, invalid combination and duplicate-registration failure. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-CONFIG-008` only after verified pass.

#### OC2-D-CONFIG-009 — Implement trusted template variable substitution with typed allowlist, escaping and zero secret interpolation in instructions.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Abstractions/, src/OpenConnect.Core/, contracts/config/, examples/` or the existing equivalent; record the package/adapter owner. Check `S01` gate and the current source API/contract version.
2. **Concrete implementation:** Implement trusted template variable substitution with typed allowlist, escaping and zero secret interpolation in instructions. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Bind one immutable typed configuration snapshot, validate all referenced IDs/capabilities, and reject invalid enums or grant escalation before runtime. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify successful integration with canonical contract fixtures. Inject malformed input, cancellation and unavailable dependency; assert typed error/degraded behavior.
5. **Acceptance artifact:** Exercise fluent + JSON/YAML parity, invalid combination and duplicate-registration failure. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-CONFIG-009` only after verified pass.

#### OC2-D-CONFIG-010 — Load agent-runtime.md and per-agent files safely with path containment, UTF8/size limits, immutable SHA revision and atomic reload.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Abstractions/, src/OpenConnect.Core/, contracts/config/, examples/` or the existing equivalent; record the package/adapter owner. Check `S01` gate and the current source API/contract version.
2. **Concrete implementation:** Load agent-runtime.md and per-agent files safely with path containment, UTF8/size limits, immutable SHA revision and atomic reload. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Bind one immutable typed configuration snapshot, validate all referenced IDs/capabilities, and reject invalid enums or grant escalation before runtime. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Prove commit/rollback behavior against an actual supported relational provider. Inject lost ACK, stale fencing token and concurrent writer where the contract touches durable state.
5. **Acceptance artifact:** Exercise fluent + JSON/YAML parity, invalid combination and duplicate-registration failure. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-CONFIG-010` only after verified pass.

#### OC2-D-CONFIG-011 — Validate static delegation DAG with depth0 lead, depth1 child and depth2 grandchild; reject depth3, cycles and self recursion.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Abstractions/, src/OpenConnect.Core/, contracts/config/, examples/` or the existing equivalent; record the package/adapter owner. Check `S01` gate and the current source API/contract version.
2. **Concrete implementation:** Validate static delegation DAG with depth0 lead, depth1 child and depth2 grandchild; reject depth3, cycles and self recursion. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Bind one immutable typed configuration snapshot, validate all referenced IDs/capabilities, and reject invalid enums or grant escalation before runtime. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify successful integration with canonical contract fixtures. Inject malformed input, cancellation and unavailable dependency; assert typed error/degraded behavior.
5. **Acceptance artifact:** Exercise fluent + JSON/YAML parity, invalid combination and duplicate-registration failure. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-CONFIG-011` only after verified pass.

#### OC2-D-CONFIG-012 — Enforce depth and fan-out limits again at runtime even if native Harness/MAF dynamically proposes new background delegates.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Abstractions/, src/OpenConnect.Core/, contracts/config/, examples/` or the existing equivalent; record the package/adapter owner. Check `S01` gate and the current source API/contract version.
2. **Concrete implementation:** Enforce depth and fan-out limits again at runtime even if native Harness/MAF dynamically proposes new background delegates. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Bind one immutable typed configuration snapshot, validate all referenced IDs/capabilities, and reject invalid enums or grant escalation before runtime. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Invoke an actual pinned MAF type and inspect real lifecycle/session data. Prove unsupported versions/profiles report unavailable instead of fabricating equivalent behavior.
5. **Acceptance artifact:** Exercise fluent + JSON/YAML parity, invalid combination and duplicate-registration failure. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-CONFIG-012` only after verified pass.

#### OC2-D-CONFIG-013 — Enforce independent limits for model calls, tool calls, depth, concurrency, tokens, wall-clock time, queue size and cost.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Abstractions/, src/OpenConnect.Core/, contracts/config/, examples/` or the existing equivalent; record the package/adapter owner. Check `S01` gate and the current source API/contract version.
2. **Concrete implementation:** Enforce independent limits for model calls, tool calls, depth, concurrency, tokens, wall-clock time, queue size and cost. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Bind one immutable typed configuration snapshot, validate all referenced IDs/capabilities, and reject invalid enums or grant escalation before runtime. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Exercise authorized and explicitly unauthorized principals/tenants; assert no side effect on denial. Verify no credentials, plaintext or raw user content leave the intended trust boundary.
5. **Acceptance artifact:** Exercise fluent + JSON/YAML parity, invalid combination and duplicate-registration failure. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-CONFIG-013` only after verified pass.

#### OC2-D-CONFIG-014 — Make model capability selection explicit (tools, streaming, structured output, vision, locality and fallback).

1. **Repository and prerequisites:** Work in `src/OpenConnect.Abstractions/, src/OpenConnect.Core/, contracts/config/, examples/` or the existing equivalent; record the package/adapter owner. Check `S01` gate and the current source API/contract version.
2. **Concrete implementation:** Make model capability selection explicit (tools, streaming, structured output, vision, locality and fallback). Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Bind one immutable typed configuration snapshot, validate all referenced IDs/capabilities, and reject invalid enums or grant escalation before runtime. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Run targeted success and deliberate negative regression in CI. Save machine-readable test artifacts and versioned audit evidence.
5. **Acceptance artifact:** Exercise fluent + JSON/YAML parity, invalid combination and duplicate-registration failure. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-CONFIG-014` only after verified pass.

#### OC2-D-CONFIG-015 — Validate structured outputs before use, including bounded repair/retry and prohibition on invalid output triggering external effects.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Abstractions/, src/OpenConnect.Core/, contracts/config/, examples/` or the existing equivalent; record the package/adapter owner. Check `S01` gate and the current source API/contract version.
2. **Concrete implementation:** Validate structured outputs before use, including bounded repair/retry and prohibition on invalid output triggering external effects. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Bind one immutable typed configuration snapshot, validate all referenced IDs/capabilities, and reject invalid enums or grant escalation before runtime. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify successful integration with canonical contract fixtures. Inject malformed input, cancellation and unavailable dependency; assert typed error/degraded behavior.
5. **Acceptance artifact:** Exercise fluent + JSON/YAML parity, invalid combination and duplicate-registration failure. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-CONFIG-015` only after verified pass.

#### OC2-D-CONFIG-016 — Freeze catalog revision per run and enforce compatibility on resume; hot reload cannot silently add rights or mutate pending approvals.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Abstractions/, src/OpenConnect.Core/, contracts/config/, examples/` or the existing equivalent; record the package/adapter owner. Check `S01` gate and the current source API/contract version.
2. **Concrete implementation:** Freeze catalog revision per run and enforce compatibility on resume; hot reload cannot silently add rights or mutate pending approvals. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Bind one immutable typed configuration snapshot, validate all referenced IDs/capabilities, and reject invalid enums or grant escalation before runtime. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Exercise authorized and explicitly unauthorized principals/tenants; assert no side effect on denial. Verify no credentials, plaintext or raw user content leave the intended trust boundary.
5. **Acceptance artifact:** Exercise fluent + JSON/YAML parity, invalid combination and duplicate-registration failure. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-CONFIG-016` only after verified pass.

#### OC2-D-CONFIG-017 — Provide developer quickstart for Direct agent with one tool and no database/broker/optional workers.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Abstractions/, src/OpenConnect.Core/, contracts/config/, examples/` or the existing equivalent; record the package/adapter owner. Check `S01` gate and the current source API/contract version.
2. **Concrete implementation:** Provide developer quickstart for Direct agent with one tool and no database/broker/optional workers. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Bind one immutable typed configuration snapshot, validate all referenced IDs/capabilities, and reject invalid enums or grant escalation before runtime. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Prove commit/rollback behavior against an actual supported relational provider. Inject lost ACK, stale fencing token and concurrent writer where the contract touches durable state.
5. **Acceptance artifact:** Exercise fluent + JSON/YAML parity, invalid combination and duplicate-registration failure. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-CONFIG-017` only after verified pass.

#### OC2-D-CONFIG-018 — Provide developer quickstart for Harness with native plan/todo and AgentSession across multiple turns.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Abstractions/, src/OpenConnect.Core/, contracts/config/, examples/` or the existing equivalent; record the package/adapter owner. Check `S01` gate and the current source API/contract version.
2. **Concrete implementation:** Provide developer quickstart for Harness with native plan/todo and AgentSession across multiple turns. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Bind one immutable typed configuration snapshot, validate all referenced IDs/capabilities, and reject invalid enums or grant escalation before runtime. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Invoke an actual pinned MAF type and inspect real lifecycle/session data. Prove unsupported versions/profiles report unavailable instead of fabricating equivalent behavior.
5. **Acceptance artifact:** Exercise fluent + JSON/YAML parity, invalid combination and duplicate-registration failure. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-CONFIG-018` only after verified pass.

#### OC2-D-CONFIG-019 — Provide developer quickstart for typed Workflow with sequential, parallel and HITL nodes.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Abstractions/, src/OpenConnect.Core/, contracts/config/, examples/` or the existing equivalent; record the package/adapter owner. Check `S01` gate and the current source API/contract version.
2. **Concrete implementation:** Provide developer quickstart for typed Workflow with sequential, parallel and HITL nodes. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Bind one immutable typed configuration snapshot, validate all referenced IDs/capabilities, and reject invalid enums or grant escalation before runtime. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Invoke an actual pinned MAF type and inspect real lifecycle/session data. Prove unsupported versions/profiles report unavailable instead of fabricating equivalent behavior.
5. **Acceptance artifact:** Exercise fluent + JSON/YAML parity, invalid combination and duplicate-registration failure. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-CONFIG-019` only after verified pass.

#### OC2-D-CONFIG-020 — Provide developer quickstart for DurableWorkflow with verified backend or explicit unsupported feature diagnostic.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Abstractions/, src/OpenConnect.Core/, contracts/config/, examples/` or the existing equivalent; record the package/adapter owner. Check `S01` gate and the current source API/contract version.
2. **Concrete implementation:** Provide developer quickstart for DurableWorkflow with verified backend or explicit unsupported feature diagnostic. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Bind one immutable typed configuration snapshot, validate all referenced IDs/capabilities, and reject invalid enums or grant escalation before runtime. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Invoke an actual pinned MAF type and inspect real lifecycle/session data. Prove unsupported versions/profiles report unavailable instead of fabricating equivalent behavior.
5. **Acceptance artifact:** Exercise fluent + JSON/YAML parity, invalid combination and duplicate-registration failure. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-CONFIG-020` only after verified pass.

#### OC2-D-CONFIG-021 — Create schema parity tests across fluent API, JSON/YAML, C#/TS/Python and invalid configuration matrices.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Abstractions/, src/OpenConnect.Core/, contracts/config/, examples/` or the existing equivalent; record the package/adapter owner. Check `S01` gate and the current source API/contract version.
2. **Concrete implementation:** Create schema parity tests across fluent API, JSON/YAML, C#/TS/Python and invalid configuration matrices. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Bind one immutable typed configuration snapshot, validate all referenced IDs/capabilities, and reject invalid enums or grant escalation before runtime. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Run targeted success and deliberate negative regression in CI. Save machine-readable test artifacts and versioned audit evidence.
5. **Acceptance artifact:** Exercise fluent + JSON/YAML parity, invalid combination and duplicate-registration failure. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-CONFIG-021` only after verified pass.


### S03 — Identity, grants and delegation constraints

**Prerequisites:** `S02`. **Checklist section:** 3.4 Identity modes, contextual authorization and delegation budgets. **Scope:** 16 atomic deliverables. **Primary source paths:** `src/OpenConnect.Security/, src/OpenConnect.Hosting.AspNetCore/, tests/Security/`.

**Authority:** Use current principal/workload context and effective narrowing at every effect; do not serialize raw credentials to MAF/worker/model. **Required verification pattern:** Test cross-tenant denial, auth revocation, delegated child downgrade and audit-safe errors.

#### OC2-D-AUTH-001 — Resolve interactive actor from authenticated ASP.NET host and trusted tenant resolver; ignore tenant or role fields supplied by browser/LLM.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Security/, src/OpenConnect.Hosting.AspNetCore/, tests/Security/` or the existing equivalent; record the package/adapter owner. Check `S02` gate and the current source API/contract version.
2. **Concrete implementation:** Resolve interactive actor from authenticated ASP.NET host and trusted tenant resolver; ignore tenant or role fields supplied by browser/LLM. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Use current principal/workload context and effective narrowing at every effect; do not serialize raw credentials to MAF/worker/model. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Exercise authorized and explicitly unauthorized principals/tenants; assert no side effect on denial. Verify no credentials, plaintext or raw user content leave the intended trust boundary.
5. **Acceptance artifact:** Test cross-tenant denial, auth revocation, delegated child downgrade and audit-safe errors. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-AUTH-001` only after verified pass.

#### OC2-D-AUTH-002 — Build immutable per-task authorization/policy snapshot bound to user, tenant, session, task, catalog revision and expiry.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Security/, src/OpenConnect.Hosting.AspNetCore/, tests/Security/` or the existing equivalent; record the package/adapter owner. Check `S02` gate and the current source API/contract version.
2. **Concrete implementation:** Build immutable per-task authorization/policy snapshot bound to user, tenant, session, task, catalog revision and expiry. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Use current principal/workload context and effective narrowing at every effect; do not serialize raw credentials to MAF/worker/model. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Prove commit/rollback behavior against an actual supported relational provider. Inject lost ACK, stale fencing token and concurrent writer where the contract touches durable state.
5. **Acceptance artifact:** Test cross-tenant denial, auth revocation, delegated child downgrade and audit-safe errors. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-AUTH-002` only after verified pass.

#### OC2-D-AUTH-003 — Reauthorize every consequential tool call against current grants, not just the initial task snapshot or capability discovery.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Security/, src/OpenConnect.Hosting.AspNetCore/, tests/Security/` or the existing equivalent; record the package/adapter owner. Check `S02` gate and the current source API/contract version.
2. **Concrete implementation:** Reauthorize every consequential tool call against current grants, not just the initial task snapshot or capability discovery. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Use current principal/workload context and effective narrowing at every effect; do not serialize raw credentials to MAF/worker/model. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify successful integration with canonical contract fixtures. Inject malformed input, cancellation and unavailable dependency; assert typed error/degraded behavior.
5. **Acceptance artifact:** Test cross-tenant denial, auth revocation, delegated child downgrade and audit-safe errors. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-AUTH-003` only after verified pass.

#### OC2-D-AUTH-004 — Cancel or pause newly privileged work when user is disabled, token expires, grants revoke, tenant changes or session is invalidated.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Security/, src/OpenConnect.Hosting.AspNetCore/, tests/Security/` or the existing equivalent; record the package/adapter owner. Check `S02` gate and the current source API/contract version.
2. **Concrete implementation:** Cancel or pause newly privileged work when user is disabled, token expires, grants revoke, tenant changes or session is invalidated. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Use current principal/workload context and effective narrowing at every effect; do not serialize raw credentials to MAF/worker/model. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Exercise authorized and explicitly unauthorized principals/tenants; assert no side effect on denial. Verify no credentials, plaintext or raw user content leave the intended trust boundary.
5. **Acceptance artifact:** Test cross-tenant denial, auth revocation, delegated child downgrade and audit-safe errors. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-AUTH-004` only after verified pass.

#### OC2-D-AUTH-005 — Ensure an interactive child inherits the intersection of parent, child registration, tenant and tool policy—not the union.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Security/, src/OpenConnect.Hosting.AspNetCore/, tests/Security/` or the existing equivalent; record the package/adapter owner. Check `S02` gate and the current source API/contract version.
2. **Concrete implementation:** Ensure an interactive child inherits the intersection of parent, child registration, tenant and tool policy—not the union. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Use current principal/workload context and effective narrowing at every effect; do not serialize raw credentials to MAF/worker/model. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Exercise authorized and explicitly unauthorized principals/tenants; assert no side effect on denial. Verify no credentials, plaintext or raw user content leave the intended trust boundary.
5. **Acceptance artifact:** Test cross-tenant denial, auth revocation, delegated child downgrade and audit-safe errors. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-AUTH-005` only after verified pass.

#### OC2-D-AUTH-006 — Reject depth3 and excessive background/harness delegation before any child model or tool begins.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Security/, src/OpenConnect.Hosting.AspNetCore/, tests/Security/` or the existing equivalent; record the package/adapter owner. Check `S02` gate and the current source API/contract version.
2. **Concrete implementation:** Reject depth3 and excessive background/harness delegation before any child model or tool begins. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Use current principal/workload context and effective narrowing at every effect; do not serialize raw credentials to MAF/worker/model. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Invoke an actual pinned MAF type and inspect real lifecycle/session data. Prove unsupported versions/profiles report unavailable instead of fabricating equivalent behavior.
5. **Acceptance artifact:** Test cross-tenant denial, auth revocation, delegated child downgrade and audit-safe errors. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-AUTH-006` only after verified pass.

#### OC2-D-AUTH-007 — Enforce per-subagent budgets as a slice of parent budget and ensure delegated spend cannot be counted twice.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Security/, src/OpenConnect.Hosting.AspNetCore/, tests/Security/` or the existing equivalent; record the package/adapter owner. Check `S02` gate and the current source API/contract version.
2. **Concrete implementation:** Enforce per-subagent budgets as a slice of parent budget and ensure delegated spend cannot be counted twice. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Use current principal/workload context and effective narrowing at every effect; do not serialize raw credentials to MAF/worker/model. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Invoke an actual pinned MAF type and inspect real lifecycle/session data. Prove unsupported versions/profiles report unavailable instead of fabricating equivalent behavior.
5. **Acceptance artifact:** Test cross-tenant denial, auth revocation, delegated child downgrade and audit-safe errors. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-AUTH-007` only after verified pass.

#### OC2-D-AUTH-008 — Create explicit operational workload identity provider that does not rely on HttpContext, cookies or prior interactive tokens.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Security/, src/OpenConnect.Hosting.AspNetCore/, tests/Security/` or the existing equivalent; record the package/adapter owner. Check `S02` gate and the current source API/contract version.
2. **Concrete implementation:** Create explicit operational workload identity provider that does not rely on HttpContext, cookies or prior interactive tokens. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Use current principal/workload context and effective narrowing at every effect; do not serialize raw credentials to MAF/worker/model. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Exercise authorized and explicitly unauthorized principals/tenants; assert no side effect on denial. Verify no credentials, plaintext or raw user content leave the intended trust boundary.
5. **Acceptance artifact:** Test cross-tenant denial, auth revocation, delegated child downgrade and audit-safe errors. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-AUTH-008` only after verified pass.

#### OC2-D-AUTH-009 — Permit scheduled/event-triggered operational activation only after dedicated operator authorization and durable registration.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Security/, src/OpenConnect.Hosting.AspNetCore/, tests/Security/` or the existing equivalent; record the package/adapter owner. Check `S02` gate and the current source API/contract version.
2. **Concrete implementation:** Permit scheduled/event-triggered operational activation only after dedicated operator authorization and durable registration. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Use current principal/workload context and effective narrowing at every effect; do not serialize raw credentials to MAF/worker/model. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Exercise authorized and explicitly unauthorized principals/tenants; assert no side effect on denial. Verify no credentials, plaintext or raw user content leave the intended trust boundary.
5. **Acceptance artifact:** Test cross-tenant denial, auth revocation, delegated child downgrade and audit-safe errors. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-AUTH-009` only after verified pass.

#### OC2-D-AUTH-010 — Enforce workload scopes and tenant restrictions before each operational read/write and remote MCP call.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Security/, src/OpenConnect.Hosting.AspNetCore/, tests/Security/` or the existing equivalent; record the package/adapter owner. Check `S02` gate and the current source API/contract version.
2. **Concrete implementation:** Enforce workload scopes and tenant restrictions before each operational read/write and remote MCP call. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Use current principal/workload context and effective narrowing at every effect; do not serialize raw credentials to MAF/worker/model. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Exercise authorized and explicitly unauthorized principals/tenants; assert no side effect on denial. Verify no credentials, plaintext or raw user content leave the intended trust boundary. Verify unregistered/malicious MCP tool schemas cannot expand permissions, redirect network access or bypass approval.
5. **Acceptance artifact:** Test cross-tenant denial, auth revocation, delegated child downgrade and audit-safe errors. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-AUTH-010` only after verified pass.

#### OC2-D-AUTH-011 — Require explicit authorized durable enqueue for interactive-to-operational transitions; do not treat this as recursive delegation.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Security/, src/OpenConnect.Hosting.AspNetCore/, tests/Security/` or the existing equivalent; record the package/adapter owner. Check `S02` gate and the current source API/contract version.
2. **Concrete implementation:** Require explicit authorized durable enqueue for interactive-to-operational transitions; do not treat this as recursive delegation. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Use current principal/workload context and effective narrowing at every effect; do not serialize raw credentials to MAF/worker/model. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Run targeted success and deliberate negative regression in CI. Save machine-readable test artifacts and versioned audit evidence.
5. **Acceptance artifact:** Test cross-tenant denial, auth revocation, delegated child downgrade and audit-safe errors. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-AUTH-011` only after verified pass.

#### OC2-D-AUTH-012 — Require fresh authenticated review/notification for operational-to-interactive transitions; never activate another user's browser.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Security/, src/OpenConnect.Hosting.AspNetCore/, tests/Security/` or the existing equivalent; record the package/adapter owner. Check `S02` gate and the current source API/contract version.
2. **Concrete implementation:** Require fresh authenticated review/notification for operational-to-interactive transitions; never activate another user's browser. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Use current principal/workload context and effective narrowing at every effect; do not serialize raw credentials to MAF/worker/model. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Exercise authorized and explicitly unauthorized principals/tenants; assert no side effect on denial. Verify no credentials, plaintext or raw user content leave the intended trust boundary.
5. **Acceptance artifact:** Test cross-tenant denial, auth revocation, delegated child downgrade and audit-safe errors. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-AUTH-012` only after verified pass.

#### OC2-D-AUTH-013 — Prevent prompts, skill manifests, MCP descriptions and retrieved content from changing actor identity or grants.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Security/, src/OpenConnect.Hosting.AspNetCore/, tests/Security/` or the existing equivalent; record the package/adapter owner. Check `S02` gate and the current source API/contract version.
2. **Concrete implementation:** Prevent prompts, skill manifests, MCP descriptions and retrieved content from changing actor identity or grants. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Use current principal/workload context and effective narrowing at every effect; do not serialize raw credentials to MAF/worker/model. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Invoke an actual pinned MAF type and inspect real lifecycle/session data. Prove unsupported versions/profiles report unavailable instead of fabricating equivalent behavior. Verify unregistered/malicious MCP tool schemas cannot expand permissions, redirect network access or bypass approval.
5. **Acceptance artifact:** Test cross-tenant denial, auth revocation, delegated child downgrade and audit-safe errors. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-AUTH-013` only after verified pass.

#### OC2-D-AUTH-014 — Keep principal raw credentials out of MAF prompts, NATS metadata, checkpoint diagnostics and worker payloads.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Security/, src/OpenConnect.Hosting.AspNetCore/, tests/Security/` or the existing equivalent; record the package/adapter owner. Check `S02` gate and the current source API/contract version.
2. **Concrete implementation:** Keep principal raw credentials out of MAF prompts, NATS metadata, checkpoint diagnostics and worker payloads. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Use current principal/workload context and effective narrowing at every effect; do not serialize raw credentials to MAF/worker/model. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Exercise authorized and explicitly unauthorized principals/tenants; assert no side effect on denial. Verify no credentials, plaintext or raw user content leave the intended trust boundary.
5. **Acceptance artifact:** Test cross-tenant denial, auth revocation, delegated child downgrade and audit-safe errors. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-AUTH-014` only after verified pass.

#### OC2-D-AUTH-015 — Provide per-tenant and per-application rate limits, quotas, budgets and signed audit metadata across both modes.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Security/, src/OpenConnect.Hosting.AspNetCore/, tests/Security/` or the existing equivalent; record the package/adapter owner. Check `S02` gate and the current source API/contract version.
2. **Concrete implementation:** Provide per-tenant and per-application rate limits, quotas, budgets and signed audit metadata across both modes. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Use current principal/workload context and effective narrowing at every effect; do not serialize raw credentials to MAF/worker/model. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Exercise authorized and explicitly unauthorized principals/tenants; assert no side effect on denial. Verify no credentials, plaintext or raw user content leave the intended trust boundary.
5. **Acceptance artifact:** Test cross-tenant denial, auth revocation, delegated child downgrade and audit-safe errors. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-AUTH-015` only after verified pass.

#### OC2-D-AUTH-016 — Write adversarial tests for cross-tenant session IDs, revoked rights on reconnect, forged approval, compromised worker and malicious child contexts.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Security/, src/OpenConnect.Hosting.AspNetCore/, tests/Security/` or the existing equivalent; record the package/adapter owner. Check `S02` gate and the current source API/contract version.
2. **Concrete implementation:** Write adversarial tests for cross-tenant session IDs, revoked rights on reconnect, forged approval, compromised worker and malicious child contexts. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Use current principal/workload context and effective narrowing at every effect; do not serialize raw credentials to MAF/worker/model. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Exercise authorized and explicitly unauthorized principals/tenants; assert no side effect on denial. Verify no credentials, plaintext or raw user content leave the intended trust boundary.
5. **Acceptance artifact:** Test cross-tenant denial, auth revocation, delegated child downgrade and audit-safe errors. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-AUTH-016` only after verified pass.


### S04 — Native tools, approvals, MCP and skills

**Prerequisites:** `S03`. **Checklist section:** 3.5 Native MAF tools, tool discovery, MCP microservices, remote agents and skills. **Scope:** 25 atomic deliverables. **Primary source paths:** `src/OpenConnect.Tools/, src/OpenConnect.Mcp*/, contracts/tools/, tests/Mcp/`.

**Authority:** Allowlisted discovery is advisory; invocation requires schema, exact-grant approval, destination trust and output protection. **Required verification pattern:** Test unauthorized or new MCP tool, malformed arguments, SSRF and forged approval.

#### OC2-D-TOOLS-001 — Implement canonical OpenConnect tool descriptors (ID/version, schemas, impact, residency, scopes, timeout, cancellation, idempotency, progress capability).

1. **Repository and prerequisites:** Work in `src/OpenConnect.Tools/, src/OpenConnect.Mcp*/, contracts/tools/, tests/Mcp/` or the existing equivalent; record the package/adapter owner. Check `S03` gate and the current source API/contract version.
2. **Concrete implementation:** Implement canonical OpenConnect tool descriptors (ID/version, schemas, impact, residency, scopes, timeout, cancellation, idempotency, progress capability). Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Allowlisted discovery is advisory; invocation requires schema, exact-grant approval, destination trust and output protection. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Exercise authorized and explicitly unauthorized principals/tenants; assert no side effect on denial. Verify no credentials, plaintext or raw user content leave the intended trust boundary.
5. **Acceptance artifact:** Test unauthorized or new MCP tool, malformed arguments, SSRF and forged approval. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-TOOLS-001` only after verified pass.

#### OC2-D-TOOLS-002 — Adapt typed DI-registered C# service tools to real MAF AITool/AIFunctionFactory without building a second tool invocation loop.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Tools/, src/OpenConnect.Mcp*/, contracts/tools/, tests/Mcp/` or the existing equivalent; record the package/adapter owner. Check `S03` gate and the current source API/contract version.
2. **Concrete implementation:** Adapt typed DI-registered C# service tools to real MAF AITool/AIFunctionFactory without building a second tool invocation loop. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Allowlisted discovery is advisory; invocation requires schema, exact-grant approval, destination trust and output protection. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Invoke an actual pinned MAF type and inspect real lifecycle/session data. Prove unsupported versions/profiles report unavailable instead of fabricating equivalent behavior.
5. **Acceptance artifact:** Test unauthorized or new MCP tool, malformed arguments, SSRF and forged approval. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-TOOLS-002` only after verified pass.

#### OC2-D-TOOLS-003 — Register existing MAF tools as first-class OpenConnect-governed tools, preserving native tool behavior.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Tools/, src/OpenConnect.Mcp*/, contracts/tools/, tests/Mcp/` or the existing equivalent; record the package/adapter owner. Check `S03` gate and the current source API/contract version.
2. **Concrete implementation:** Register existing MAF tools as first-class OpenConnect-governed tools, preserving native tool behavior. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Allowlisted discovery is advisory; invocation requires schema, exact-grant approval, destination trust and output protection. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Invoke an actual pinned MAF type and inspect real lifecycle/session data. Prove unsupported versions/profiles report unavailable instead of fabricating equivalent behavior.
5. **Acceptance artifact:** Test unauthorized or new MCP tool, malformed arguments, SSRF and forged approval. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-TOOLS-003` only after verified pass.

#### OC2-D-TOOLS-004 — Build bounded tool discovery that intersects agent registration, current actor grants, tenant, task, model ability, policy and live service readiness.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Tools/, src/OpenConnect.Mcp*/, contracts/tools/, tests/Mcp/` or the existing equivalent; record the package/adapter owner. Check `S03` gate and the current source API/contract version.
2. **Concrete implementation:** Build bounded tool discovery that intersects agent registration, current actor grants, tenant, task, model ability, policy and live service readiness. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Allowlisted discovery is advisory; invocation requires schema, exact-grant approval, destination trust and output protection. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Exercise authorized and explicitly unauthorized principals/tenants; assert no side effect on denial. Verify no credentials, plaintext or raw user content leave the intended trust boundary.
5. **Acceptance artifact:** Test unauthorized or new MCP tool, malformed arguments, SSRF and forged approval. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-TOOLS-004` only after verified pass.

#### OC2-D-TOOLS-005 — Return discoverable tool descriptions only after policy filtering and redact untrusted descriptions before model context.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Tools/, src/OpenConnect.Mcp*/, contracts/tools/, tests/Mcp/` or the existing equivalent; record the package/adapter owner. Check `S03` gate and the current source API/contract version.
2. **Concrete implementation:** Return discoverable tool descriptions only after policy filtering and redact untrusted descriptions before model context. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Allowlisted discovery is advisory; invocation requires schema, exact-grant approval, destination trust and output protection. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify successful integration with canonical contract fixtures. Inject malformed input, cancellation and unavailable dependency; assert typed error/degraded behavior.
5. **Acceptance artifact:** Test unauthorized or new MCP tool, malformed arguments, SSRF and forged approval. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-TOOLS-005` only after verified pass.

#### OC2-D-TOOLS-006 — Validate input arguments with explicit JSON schema and canonicalization before approval or execution.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Tools/, src/OpenConnect.Mcp*/, contracts/tools/, tests/Mcp/` or the existing equivalent; record the package/adapter owner. Check `S03` gate and the current source API/contract version.
2. **Concrete implementation:** Validate input arguments with explicit JSON schema and canonicalization before approval or execution. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Allowlisted discovery is advisory; invocation requires schema, exact-grant approval, destination trust and output protection. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Exercise authorized and explicitly unauthorized principals/tenants; assert no side effect on denial. Verify no credentials, plaintext or raw user content leave the intended trust boundary.
5. **Acceptance artifact:** Test unauthorized or new MCP tool, malformed arguments, SSRF and forged approval. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-TOOLS-006` only after verified pass.

#### OC2-D-TOOLS-007 — Validate tool outputs with schema, size caps, MIME and privacy rules before they are returned to the MAF run.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Tools/, src/OpenConnect.Mcp*/, contracts/tools/, tests/Mcp/` or the existing equivalent; record the package/adapter owner. Check `S03` gate and the current source API/contract version.
2. **Concrete implementation:** Validate tool outputs with schema, size caps, MIME and privacy rules before they are returned to the MAF run. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Allowlisted discovery is advisory; invocation requires schema, exact-grant approval, destination trust and output protection. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Exercise authorized and explicitly unauthorized principals/tenants; assert no side effect on denial. Verify no credentials, plaintext or raw user content leave the intended trust boundary.
5. **Acceptance artifact:** Test unauthorized or new MCP tool, malformed arguments, SSRF and forged approval. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-TOOLS-007` only after verified pass.

#### OC2-D-TOOLS-008 — Define side-effect taxonomy (read, write, external effect) and approval requirements, with conservative unknown-tool default deny.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Tools/, src/OpenConnect.Mcp*/, contracts/tools/, tests/Mcp/` or the existing equivalent; record the package/adapter owner. Check `S03` gate and the current source API/contract version.
2. **Concrete implementation:** Define side-effect taxonomy (read, write, external effect) and approval requirements, with conservative unknown-tool default deny. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Allowlisted discovery is advisory; invocation requires schema, exact-grant approval, destination trust and output protection. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Exercise authorized and explicitly unauthorized principals/tenants; assert no side effect on denial. Verify no credentials, plaintext or raw user content leave the intended trust boundary.
5. **Acceptance artifact:** Test unauthorized or new MCP tool, malformed arguments, SSRF and forged approval. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-TOOLS-008` only after verified pass.

#### OC2-D-TOOLS-009 — Apply exact-argument-digest/nonce/actor/tenant/session/tool-revision approvals through the pinned native MAF HITL mechanism.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Tools/, src/OpenConnect.Mcp*/, contracts/tools/, tests/Mcp/` or the existing equivalent; record the package/adapter owner. Check `S03` gate and the current source API/contract version.
2. **Concrete implementation:** Apply exact-argument-digest/nonce/actor/tenant/session/tool-revision approvals through the pinned native MAF HITL mechanism. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Allowlisted discovery is advisory; invocation requires schema, exact-grant approval, destination trust and output protection. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Exercise authorized and explicitly unauthorized principals/tenants; assert no side effect on denial. Verify no credentials, plaintext or raw user content leave the intended trust boundary.
5. **Acceptance artifact:** Test unauthorized or new MCP tool, malformed arguments, SSRF and forged approval. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-TOOLS-009` only after verified pass.

#### OC2-D-TOOLS-010 — Prove approved tool execution cannot occur before approval receipt; expired/revoked approvals cannot be replayed after restart.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Tools/, src/OpenConnect.Mcp*/, contracts/tools/, tests/Mcp/` or the existing equivalent; record the package/adapter owner. Check `S03` gate and the current source API/contract version.
2. **Concrete implementation:** Prove approved tool execution cannot occur before approval receipt; expired/revoked approvals cannot be replayed after restart. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Allowlisted discovery is advisory; invocation requires schema, exact-grant approval, destination trust and output protection. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Exercise authorized and explicitly unauthorized principals/tenants; assert no side effect on denial. Verify no credentials, plaintext or raw user content leave the intended trust boundary.
5. **Acceptance artifact:** Test unauthorized or new MCP tool, malformed arguments, SSRF and forged approval. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-TOOLS-010` only after verified pass.

#### OC2-D-TOOLS-011 — Implement policy-controlled tool retry classification; never automatically replay non-idempotent effect with uncertain outcome.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Tools/, src/OpenConnect.Mcp*/, contracts/tools/, tests/Mcp/` or the existing equivalent; record the package/adapter owner. Check `S03` gate and the current source API/contract version.
2. **Concrete implementation:** Implement policy-controlled tool retry classification; never automatically replay non-idempotent effect with uncertain outcome. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Allowlisted discovery is advisory; invocation requires schema, exact-grant approval, destination trust and output protection. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify successful integration with canonical contract fixtures. Inject malformed input, cancellation and unavailable dependency; assert typed error/degraded behavior.
5. **Acceptance artifact:** Test unauthorized or new MCP tool, malformed arguments, SSRF and forged approval. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-TOOLS-011` only after verified pass.

#### OC2-D-TOOLS-012 — Provide tool cancellation tokens and measured IProgress equivalent with scoped execution context, without leaking credentials.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Tools/, src/OpenConnect.Mcp*/, contracts/tools/, tests/Mcp/` or the existing equivalent; record the package/adapter owner. Check `S03` gate and the current source API/contract version.
2. **Concrete implementation:** Provide tool cancellation tokens and measured IProgress equivalent with scoped execution context, without leaking credentials. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Allowlisted discovery is advisory; invocation requires schema, exact-grant approval, destination trust and output protection. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Exercise authorized and explicitly unauthorized principals/tenants; assert no side effect on denial. Verify no credentials, plaintext or raw user content leave the intended trust boundary.
5. **Acceptance artifact:** Test unauthorized or new MCP tool, malformed arguments, SSRF and forged approval. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-TOOLS-012` only after verified pass.

#### OC2-D-TOOLS-013 — Implement MCP client adapter using official MCP .NET SDK and supported Streamable HTTP remote transport.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Tools/, src/OpenConnect.Mcp*/, contracts/tools/, tests/Mcp/` or the existing equivalent; record the package/adapter owner. Check `S03` gate and the current source API/contract version.
2. **Concrete implementation:** Implement MCP client adapter using official MCP .NET SDK and supported Streamable HTTP remote transport. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Allowlisted discovery is advisory; invocation requires schema, exact-grant approval, destination trust and output protection. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Run targeted success and deliberate negative regression in CI. Save machine-readable test artifacts and versioned audit evidence. Verify unregistered/malicious MCP tool schemas cannot expand permissions, redirect network access or bypass approval.
5. **Acceptance artifact:** Test unauthorized or new MCP tool, malformed arguments, SSRF and forged approval. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-TOOLS-013` only after verified pass.

#### OC2-D-TOOLS-014 — Implement MCP server exposure of explicitly selected microservice tools/resources/prompts through approved hosting routes.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Tools/, src/OpenConnect.Mcp*/, contracts/tools/, tests/Mcp/` or the existing equivalent; record the package/adapter owner. Check `S03` gate and the current source API/contract version.
2. **Concrete implementation:** Implement MCP server exposure of explicitly selected microservice tools/resources/prompts through approved hosting routes. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Allowlisted discovery is advisory; invocation requires schema, exact-grant approval, destination trust and output protection. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Run targeted success and deliberate negative regression in CI. Save machine-readable test artifacts and versioned audit evidence. Verify unregistered/malicious MCP tool schemas cannot expand permissions, redirect network access or bypass approval.
5. **Acceptance artifact:** Test unauthorized or new MCP tool, malformed arguments, SSRF and forged approval. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-TOOLS-014` only after verified pass.

#### OC2-D-TOOLS-015 — Implement MCP gateway aggregation with normalized namespaced IDs and service/version collision rejection.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Tools/, src/OpenConnect.Mcp*/, contracts/tools/, tests/Mcp/` or the existing equivalent; record the package/adapter owner. Check `S03` gate and the current source API/contract version.
2. **Concrete implementation:** Implement MCP gateway aggregation with normalized namespaced IDs and service/version collision rejection. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Allowlisted discovery is advisory; invocation requires schema, exact-grant approval, destination trust and output protection. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify successful integration with canonical contract fixtures. Inject malformed input, cancellation and unavailable dependency; assert typed error/degraded behavior. Verify unregistered/malicious MCP tool schemas cannot expand permissions, redirect network access or bypass approval.
5. **Acceptance artifact:** Test unauthorized or new MCP tool, malformed arguments, SSRF and forged approval. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-TOOLS-015` only after verified pass.

#### OC2-D-TOOLS-016 — Implement MCP service identity (workload/delegated user), origin allowlists, transport security, DNS/redirect/SSRF defenses and token isolation.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Tools/, src/OpenConnect.Mcp*/, contracts/tools/, tests/Mcp/` or the existing equivalent; record the package/adapter owner. Check `S03` gate and the current source API/contract version.
2. **Concrete implementation:** Implement MCP service identity (workload/delegated user), origin allowlists, transport security, DNS/redirect/SSRF defenses and token isolation. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Allowlisted discovery is advisory; invocation requires schema, exact-grant approval, destination trust and output protection. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Exercise authorized and explicitly unauthorized principals/tenants; assert no side effect on denial. Verify no credentials, plaintext or raw user content leave the intended trust boundary. Verify unregistered/malicious MCP tool schemas cannot expand permissions, redirect network access or bypass approval.
5. **Acceptance artifact:** Test unauthorized or new MCP tool, malformed arguments, SSRF and forged approval. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-TOOLS-016` only after verified pass.

#### OC2-D-TOOLS-017 — Require explicit user-identity delegation strategy where remote service supports it; never forward unrelated bearer tokens.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Tools/, src/OpenConnect.Mcp*/, contracts/tools/, tests/Mcp/` or the existing equivalent; record the package/adapter owner. Check `S03` gate and the current source API/contract version.
2. **Concrete implementation:** Require explicit user-identity delegation strategy where remote service supports it; never forward unrelated bearer tokens. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Allowlisted discovery is advisory; invocation requires schema, exact-grant approval, destination trust and output protection. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Exercise authorized and explicitly unauthorized principals/tenants; assert no side effect on denial. Verify no credentials, plaintext or raw user content leave the intended trust boundary.
5. **Acceptance artifact:** Test unauthorized or new MCP tool, malformed arguments, SSRF and forged approval. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-TOOLS-017` only after verified pass.

#### OC2-D-TOOLS-018 — Require tool metadata refresh and schema-diff review before newly discovered MCP capabilities become callable.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Tools/, src/OpenConnect.Mcp*/, contracts/tools/, tests/Mcp/` or the existing equivalent; record the package/adapter owner. Check `S03` gate and the current source API/contract version.
2. **Concrete implementation:** Require tool metadata refresh and schema-diff review before newly discovered MCP capabilities become callable. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Allowlisted discovery is advisory; invocation requires schema, exact-grant approval, destination trust and output protection. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify successful integration with canonical contract fixtures. Inject malformed input, cancellation and unavailable dependency; assert typed error/degraded behavior. Verify unregistered/malicious MCP tool schemas cannot expand permissions, redirect network access or bypass approval.
5. **Acceptance artifact:** Test unauthorized or new MCP tool, malformed arguments, SSRF and forged approval. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-TOOLS-018` only after verified pass.

#### OC2-D-TOOLS-019 — Handle MCP progress notifications only as validated non-authoritative progress; map durable job polling/receipts explicitly.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Tools/, src/OpenConnect.Mcp*/, contracts/tools/, tests/Mcp/` or the existing equivalent; record the package/adapter owner. Check `S03` gate and the current source API/contract version.
2. **Concrete implementation:** Handle MCP progress notifications only as validated non-authoritative progress; map durable job polling/receipts explicitly. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Allowlisted discovery is advisory; invocation requires schema, exact-grant approval, destination trust and output protection. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Run targeted success and deliberate negative regression in CI. Save machine-readable test artifacts and versioned audit evidence. Verify unregistered/malicious MCP tool schemas cannot expand permissions, redirect network access or bypass approval.
5. **Acceptance artifact:** Test unauthorized or new MCP tool, malformed arguments, SSRF and forged approval. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-TOOLS-019` only after verified pass.

#### OC2-D-TOOLS-020 — Implement remote A2A integration only when registered, with trust/identity boundaries and no implicit transitive tool permissions.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Tools/, src/OpenConnect.Mcp*/, contracts/tools/, tests/Mcp/` or the existing equivalent; record the package/adapter owner. Check `S03` gate and the current source API/contract version.
2. **Concrete implementation:** Implement remote A2A integration only when registered, with trust/identity boundaries and no implicit transitive tool permissions. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Allowlisted discovery is advisory; invocation requires schema, exact-grant approval, destination trust and output protection. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Run targeted success and deliberate negative regression in CI. Save machine-readable test artifacts and versioned audit evidence.
5. **Acceptance artifact:** Test unauthorized or new MCP tool, malformed arguments, SSRF and forged approval. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-TOOLS-020` only after verified pass.

#### OC2-D-TOOLS-021 — Integrate native MAF skill catalogs and verified progressive loading; avoid OpenConnect's own generic skills engine.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Tools/, src/OpenConnect.Mcp*/, contracts/tools/, tests/Mcp/` or the existing equivalent; record the package/adapter owner. Check `S03` gate and the current source API/contract version.
2. **Concrete implementation:** Integrate native MAF skill catalogs and verified progressive loading; avoid OpenConnect's own generic skills engine. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Allowlisted discovery is advisory; invocation requires schema, exact-grant approval, destination trust and output protection. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Validate generated/readme/code snippet claims against built assemblies or referenced source. Detect references to superseded APIs, stale paths and unsupported capabilities.
5. **Acceptance artifact:** Test unauthorized or new MCP tool, malformed arguments, SSRF and forged approval. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-TOOLS-021` only after verified pass.

#### OC2-D-TOOLS-022 — Enforce skill package provenance, path containment, size quotas, policy metadata and tool grants.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Tools/, src/OpenConnect.Mcp*/, contracts/tools/, tests/Mcp/` or the existing equivalent; record the package/adapter owner. Check `S03` gate and the current source API/contract version.
2. **Concrete implementation:** Enforce skill package provenance, path containment, size quotas, policy metadata and tool grants. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Allowlisted discovery is advisory; invocation requires schema, exact-grant approval, destination trust and output protection. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Invoke an actual pinned MAF type and inspect real lifecycle/session data. Prove unsupported versions/profiles report unavailable instead of fabricating equivalent behavior.
5. **Acceptance artifact:** Test unauthorized or new MCP tool, malformed arguments, SSRF and forged approval. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-TOOLS-022` only after verified pass.

#### OC2-D-TOOLS-023 — Sandbox any privileged shell/file/network skill execution separately and default it to disabled.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Tools/, src/OpenConnect.Mcp*/, contracts/tools/, tests/Mcp/` or the existing equivalent; record the package/adapter owner. Check `S03` gate and the current source API/contract version.
2. **Concrete implementation:** Sandbox any privileged shell/file/network skill execution separately and default it to disabled. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Allowlisted discovery is advisory; invocation requires schema, exact-grant approval, destination trust and output protection. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Invoke an actual pinned MAF type and inspect real lifecycle/session data. Prove unsupported versions/profiles report unavailable instead of fabricating equivalent behavior.
5. **Acceptance artifact:** Test unauthorized or new MCP tool, malformed arguments, SSRF and forged approval. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-TOOLS-023` only after verified pass.

#### OC2-D-TOOLS-024 — Test hostile MCP tool names, misleading capability descriptions, URL rebinds, shadowed tools, script injection and output exfiltration.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Tools/, src/OpenConnect.Mcp*/, contracts/tools/, tests/Mcp/` or the existing equivalent; record the package/adapter owner. Check `S03` gate and the current source API/contract version.
2. **Concrete implementation:** Test hostile MCP tool names, misleading capability descriptions, URL rebinds, shadowed tools, script injection and output exfiltration. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Allowlisted discovery is advisory; invocation requires schema, exact-grant approval, destination trust and output protection. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Run targeted success and deliberate negative regression in CI. Save machine-readable test artifacts and versioned audit evidence. Verify unregistered/malicious MCP tool schemas cannot expand permissions, redirect network access or bypass approval.
5. **Acceptance artifact:** Test unauthorized or new MCP tool, malformed arguments, SSRF and forged approval. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-TOOLS-024` only after verified pass.

#### OC2-D-TOOLS-025 — Provide one real sample exposing orders/inventory microservices with read-only and approval-gated write tools.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Tools/, src/OpenConnect.Mcp*/, contracts/tools/, tests/Mcp/` or the existing equivalent; record the package/adapter owner. Check `S03` gate and the current source API/contract version.
2. **Concrete implementation:** Provide one real sample exposing orders/inventory microservices with read-only and approval-gated write tools. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Allowlisted discovery is advisory; invocation requires schema, exact-grant approval, destination trust and output protection. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Exercise authorized and explicitly unauthorized principals/tenants; assert no side effect on denial. Verify no credentials, plaintext or raw user content leave the intended trust boundary.
5. **Acceptance artifact:** Test unauthorized or new MCP tool, malformed arguments, SSRF and forged approval. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-TOOLS-025` only after verified pass.


### S05 — MAF-owned session, context, compaction and memory

**Prerequisites:** `S01,S03,S04`. **Checklist section:** 3.6 Native MAF session/history, scoped context, compaction and memory. **Scope:** 18 atomic deliverables. **Primary source paths:** `src/OpenConnect.Sessions/, src/OpenConnect.Agents.Microsoft/, src/OpenConnect.QdrantVactor/`.

**Authority:** Persist native MAF session/checkpoint state according to provider-specific ownership; keep app events separate. Never duplicate planner/compactor. **Required verification pattern:** Test 10-turn restore, concurrent branches, cross-tenant context and memory deletion.

#### OC2-D-SESSION-001 — Document authoritative owner of model history per provider: native MAF session/local history provider or service-managed history, never both.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Sessions/, src/OpenConnect.Agents.Microsoft/, src/OpenConnect.QdrantVactor/` or the existing equivalent; record the package/adapter owner. Check `S01,S03,S04` gate and the current source API/contract version.
2. **Concrete implementation:** Document authoritative owner of model history per provider: native MAF session/local history provider or service-managed history, never both. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Persist native MAF session/checkpoint state according to provider-specific ownership; keep app events separate. Never duplicate planner/compactor. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Validate generated/readme/code snippet claims against built assemblies or referenced source. Detect references to superseded APIs, stale paths and unsupported capabilities.
5. **Acceptance artifact:** Test 10-turn restore, concurrent branches, cross-tenant context and memory deletion. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-SESSION-001` only after verified pass.

#### OC2-D-SESSION-002 — Use native AgentSession serialization and restore where supported, preserving provider-specific compatibility metadata.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Sessions/, src/OpenConnect.Agents.Microsoft/, src/OpenConnect.QdrantVactor/` or the existing equivalent; record the package/adapter owner. Check `S01,S03,S04` gate and the current source API/contract version.
2. **Concrete implementation:** Use native AgentSession serialization and restore where supported, preserving provider-specific compatibility metadata. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Persist native MAF session/checkpoint state according to provider-specific ownership; keep app events separate. Never duplicate planner/compactor. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Invoke an actual pinned MAF type and inspect real lifecycle/session data. Prove unsupported versions/profiles report unavailable instead of fabricating equivalent behavior.
5. **Acceptance artifact:** Test 10-turn restore, concurrent branches, cross-tenant context and memory deletion. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-SESSION-002` only after verified pass.

#### OC2-D-SESSION-003 — Persist and restore native Harness planning, todo lists, approval and background-child state through native MAF codecs.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Sessions/, src/OpenConnect.Agents.Microsoft/, src/OpenConnect.QdrantVactor/` or the existing equivalent; record the package/adapter owner. Check `S01,S03,S04` gate and the current source API/contract version.
2. **Concrete implementation:** Persist and restore native Harness planning, todo lists, approval and background-child state through native MAF codecs. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Persist native MAF session/checkpoint state according to provider-specific ownership; keep app events separate. Never duplicate planner/compactor. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Exercise authorized and explicitly unauthorized principals/tenants; assert no side effect on denial. Verify no credentials, plaintext or raw user content leave the intended trust boundary.
5. **Acceptance artifact:** Test 10-turn restore, concurrent branches, cross-tenant context and memory deletion. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-SESSION-003` only after verified pass.

#### OC2-D-SESSION-004 — Persist workflow checkpoint references with native graph identity/revision and checkpoint-version compatibility.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Sessions/, src/OpenConnect.Agents.Microsoft/, src/OpenConnect.QdrantVactor/` or the existing equivalent; record the package/adapter owner. Check `S01,S03,S04` gate and the current source API/contract version.
2. **Concrete implementation:** Persist workflow checkpoint references with native graph identity/revision and checkpoint-version compatibility. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Persist native MAF session/checkpoint state according to provider-specific ownership; keep app events separate. Never duplicate planner/compactor. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify disabled/uninstalled, ready, startup failure and shutdown behavior independently. Validate model/asset manifest hash, license, actual capabilities and bounded resources where applicable.
5. **Acceptance artifact:** Test 10-turn restore, concurrent branches, cross-tenant context and memory deletion. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-SESSION-004` only after verified pass.

#### OC2-D-SESSION-005 — Define OpenConnect app conversation/turn event history separately from raw MAF transcript/state and provenance.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Sessions/, src/OpenConnect.Agents.Microsoft/, src/OpenConnect.QdrantVactor/` or the existing equivalent; record the package/adapter owner. Check `S01,S03,S04` gate and the current source API/contract version.
2. **Concrete implementation:** Define OpenConnect app conversation/turn event history separately from raw MAF transcript/state and provenance. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Persist native MAF session/checkpoint state according to provider-specific ownership; keep app events separate. Never duplicate planner/compactor. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify disabled/uninstalled, ready, startup failure and shutdown behavior independently. Validate model/asset manifest hash, license, actual capabilities and bounded resources where applicable.
5. **Acceptance artifact:** Test 10-turn restore, concurrent branches, cross-tenant context and memory deletion. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-SESSION-005` only after verified pass.

#### OC2-D-SESSION-006 — Ensure ten sequential turns across host restart restore the same authorized native session and produce coherent answers.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Sessions/, src/OpenConnect.Agents.Microsoft/, src/OpenConnect.QdrantVactor/` or the existing equivalent; record the package/adapter owner. Check `S01,S03,S04` gate and the current source API/contract version.
2. **Concrete implementation:** Ensure ten sequential turns across host restart restore the same authorized native session and produce coherent answers. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Persist native MAF session/checkpoint state according to provider-specific ownership; keep app events separate. Never duplicate planner/compactor. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Invoke an actual pinned MAF type and inspect real lifecycle/session data. Prove unsupported versions/profiles report unavailable instead of fabricating equivalent behavior.
5. **Acceptance artifact:** Test 10-turn restore, concurrent branches, cross-tenant context and memory deletion. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-SESSION-006` only after verified pass.

#### OC2-D-SESSION-007 — Enforce per-session turn serialization/queue-or-conflict policy with optimistic revision and no silent concurrent history mutation.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Sessions/, src/OpenConnect.Agents.Microsoft/, src/OpenConnect.QdrantVactor/` or the existing equivalent; record the package/adapter owner. Check `S01,S03,S04` gate and the current source API/contract version.
2. **Concrete implementation:** Enforce per-session turn serialization/queue-or-conflict policy with optimistic revision and no silent concurrent history mutation. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Persist native MAF session/checkpoint state according to provider-specific ownership; keep app events separate. Never duplicate planner/compactor. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Invoke an actual pinned MAF type and inspect real lifecycle/session data. Prove unsupported versions/profiles report unavailable instead of fabricating equivalent behavior.
5. **Acceptance artifact:** Test 10-turn restore, concurrent branches, cross-tenant context and memory deletion. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-SESSION-007` only after verified pass.

#### OC2-D-SESSION-008 — Define approved context projection types (task summary, relevant history, approved artifacts, memory) with bounded sizes and TTL.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Sessions/, src/OpenConnect.Agents.Microsoft/, src/OpenConnect.QdrantVactor/` or the existing equivalent; record the package/adapter owner. Check `S01,S03,S04` gate and the current source API/contract version.
2. **Concrete implementation:** Define approved context projection types (task summary, relevant history, approved artifacts, memory) with bounded sizes and TTL. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Persist native MAF session/checkpoint state according to provider-specific ownership; keep app events separate. Never duplicate planner/compactor. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify successful integration with canonical contract fixtures. Inject malformed input, cancellation and unavailable dependency; assert typed error/degraded behavior.
5. **Acceptance artifact:** Test 10-turn restore, concurrent branches, cross-tenant context and memory deletion. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-SESSION-008` only after verified pass.

#### OC2-D-SESSION-009 — Restrict child context by identity, tenant, classification, purpose, data zone, model route and delegated token budget.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Sessions/, src/OpenConnect.Agents.Microsoft/, src/OpenConnect.QdrantVactor/` or the existing equivalent; record the package/adapter owner. Check `S01,S03,S04` gate and the current source API/contract version.
2. **Concrete implementation:** Restrict child context by identity, tenant, classification, purpose, data zone, model route and delegated token budget. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Persist native MAF session/checkpoint state according to provider-specific ownership; keep app events separate. Never duplicate planner/compactor. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Exercise authorized and explicitly unauthorized principals/tenants; assert no side effect on denial. Verify no credentials, plaintext or raw user content leave the intended trust boundary.
5. **Acceptance artifact:** Test 10-turn restore, concurrent branches, cross-tenant context and memory deletion. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-SESSION-009` only after verified pass.

#### OC2-D-SESSION-010 — Keep parallel branch scratchpads and provider sessions isolated; merge only validated, versioned child result summaries.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Sessions/, src/OpenConnect.Agents.Microsoft/, src/OpenConnect.QdrantVactor/` or the existing equivalent; record the package/adapter owner. Check `S01,S03,S04` gate and the current source API/contract version.
2. **Concrete implementation:** Keep parallel branch scratchpads and provider sessions isolated; merge only validated, versioned child result summaries. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Persist native MAF session/checkpoint state according to provider-specific ownership; keep app events separate. Never duplicate planner/compactor. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Invoke an actual pinned MAF type and inspect real lifecycle/session data. Prove unsupported versions/profiles report unavailable instead of fabricating equivalent behavior.
5. **Acceptance artifact:** Test 10-turn restore, concurrent branches, cross-tenant context and memory deletion. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-SESSION-010` only after verified pass.

#### OC2-D-SESSION-011 — Integrate MAF AIContextProvider / ChatHistoryProvider rather than creating a competing prompt history injector.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Sessions/, src/OpenConnect.Agents.Microsoft/, src/OpenConnect.QdrantVactor/` or the existing equivalent; record the package/adapter owner. Check `S01,S03,S04` gate and the current source API/contract version.
2. **Concrete implementation:** Integrate MAF AIContextProvider / ChatHistoryProvider rather than creating a competing prompt history injector. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Persist native MAF session/checkpoint state according to provider-specific ownership; keep app events separate. Never duplicate planner/compactor. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Invoke an actual pinned MAF type and inspect real lifecycle/session data. Prove unsupported versions/profiles report unavailable instead of fabricating equivalent behavior.
5. **Acceptance artifact:** Test 10-turn restore, concurrent branches, cross-tenant context and memory deletion. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-SESSION-011` only after verified pass.

#### OC2-D-SESSION-012 — Use native Harness compaction where supported and preserve sources, user commitments, approvals and privacy boundaries.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Sessions/, src/OpenConnect.Agents.Microsoft/, src/OpenConnect.QdrantVactor/` or the existing equivalent; record the package/adapter owner. Check `S01,S03,S04` gate and the current source API/contract version.
2. **Concrete implementation:** Use native Harness compaction where supported and preserve sources, user commitments, approvals and privacy boundaries. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Persist native MAF session/checkpoint state according to provider-specific ownership; keep app events separate. Never duplicate planner/compactor. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Exercise authorized and explicitly unauthorized principals/tenants; assert no side effect on denial. Verify no credentials, plaintext or raw user content leave the intended trust boundary.
5. **Acceptance artifact:** Test 10-turn restore, concurrent branches, cross-tenant context and memory deletion. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-SESSION-012` only after verified pass.

#### OC2-D-SESSION-013 — Treat retrieved memory, summaries, tools and prior conversations as untrusted data, not new system authority.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Sessions/, src/OpenConnect.Agents.Microsoft/, src/OpenConnect.QdrantVactor/` or the existing equivalent; record the package/adapter owner. Check `S01,S03,S04` gate and the current source API/contract version.
2. **Concrete implementation:** Treat retrieved memory, summaries, tools and prior conversations as untrusted data, not new system authority. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Persist native MAF session/checkpoint state according to provider-specific ownership; keep app events separate. Never duplicate planner/compactor. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify successful integration with canonical contract fixtures. Inject malformed input, cancellation and unavailable dependency; assert typed error/degraded behavior.
5. **Acceptance artifact:** Test 10-turn restore, concurrent branches, cross-tenant context and memory deletion. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-SESSION-013` only after verified pass.

#### OC2-D-SESSION-014 — Implement optional scoped durable memory key/value/summary records with consent, provenance, retention and deletion tombstones.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Sessions/, src/OpenConnect.Agents.Microsoft/, src/OpenConnect.QdrantVactor/` or the existing equivalent; record the package/adapter owner. Check `S01,S03,S04` gate and the current source API/contract version.
2. **Concrete implementation:** Implement optional scoped durable memory key/value/summary records with consent, provenance, retention and deletion tombstones. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Persist native MAF session/checkpoint state according to provider-specific ownership; keep app events separate. Never duplicate planner/compactor. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Exercise authorized and explicitly unauthorized principals/tenants; assert no side effect on denial. Verify no credentials, plaintext or raw user content leave the intended trust boundary.
5. **Acceptance artifact:** Test 10-turn restore, concurrent branches, cross-tenant context and memory deletion. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-SESSION-014` only after verified pass.

#### OC2-D-SESSION-015 — Implement optional semantic-memory retrieval adapter with query-time authorization and no core Qdrant dependency.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Sessions/, src/OpenConnect.Agents.Microsoft/, src/OpenConnect.QdrantVactor/` or the existing equivalent; record the package/adapter owner. Check `S01,S03,S04` gate and the current source API/contract version.
2. **Concrete implementation:** Implement optional semantic-memory retrieval adapter with query-time authorization and no core Qdrant dependency. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Persist native MAF session/checkpoint state according to provider-specific ownership; keep app events separate. Never duplicate planner/compactor. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Exercise authorized and explicitly unauthorized principals/tenants; assert no side effect on denial. Verify no credentials, plaintext or raw user content leave the intended trust boundary.
5. **Acceptance artifact:** Test 10-turn restore, concurrent branches, cross-tenant context and memory deletion. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-SESSION-015` only after verified pass.

#### OC2-D-SESSION-016 — Implement memory erase/revoke propagation to derived vector projections and caches, with proof of unauthorized retrieval denial.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Sessions/, src/OpenConnect.Agents.Microsoft/, src/OpenConnect.QdrantVactor/` or the existing equivalent; record the package/adapter owner. Check `S01,S03,S04` gate and the current source API/contract version.
2. **Concrete implementation:** Implement memory erase/revoke propagation to derived vector projections and caches, with proof of unauthorized retrieval denial. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Persist native MAF session/checkpoint state according to provider-specific ownership; keep app events separate. Never duplicate planner/compactor. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify successful integration with canonical contract fixtures. Inject malformed input, cancellation and unavailable dependency; assert typed error/degraded behavior.
5. **Acceptance artifact:** Test 10-turn restore, concurrent branches, cross-tenant context and memory deletion. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-SESSION-016` only after verified pass.

#### OC2-D-SESSION-017 — Store large/private attachments as encrypted, tenant-scoped object refs instead of unrestricted prompt/NATS payloads.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Sessions/, src/OpenConnect.Agents.Microsoft/, src/OpenConnect.QdrantVactor/` or the existing equivalent; record the package/adapter owner. Check `S01,S03,S04` gate and the current source API/contract version.
2. **Concrete implementation:** Store large/private attachments as encrypted, tenant-scoped object refs instead of unrestricted prompt/NATS payloads. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Persist native MAF session/checkpoint state according to provider-specific ownership; keep app events separate. Never duplicate planner/compactor. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Exercise authorized and explicitly unauthorized principals/tenants; assert no side effect on denial. Verify no credentials, plaintext or raw user content leave the intended trust boundary.
5. **Acceptance artifact:** Test 10-turn restore, concurrent branches, cross-tenant context and memory deletion. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-SESSION-017` only after verified pass.

#### OC2-D-SESSION-018 — Test provider history incompatibility, checkpoint version mismatch, branch merge race, retained secrets and cross-session memory leakage.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Sessions/, src/OpenConnect.Agents.Microsoft/, src/OpenConnect.QdrantVactor/` or the existing equivalent; record the package/adapter owner. Check `S01,S03,S04` gate and the current source API/contract version.
2. **Concrete implementation:** Test provider history incompatibility, checkpoint version mismatch, branch merge race, retained secrets and cross-session memory leakage. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Persist native MAF session/checkpoint state according to provider-specific ownership; keep app events separate. Never duplicate planner/compactor. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify disabled/uninstalled, ready, startup failure and shutdown behavior independently. Validate model/asset manifest hash, license, actual capabilities and bounded resources where applicable.
5. **Acceptance artifact:** Test 10-turn restore, concurrent branches, cross-tenant context and memory deletion. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-SESSION-018` only after verified pass.


### S06 — EF-neutral storage and application outbox

**Prerequisites:** `S03`. **Checklist section:** 3.7 EF Core provider neutrality, mappings, migration and transaction boundaries. **Scope:** 28 atomic deliverables. **Primary source paths:** `src/OpenConnect.Persistence.EFCore/, tests/Persistence/`.

**Authority:** Persist native MAF session/checkpoint state according to provider-specific ownership; keep app events separate. Never duplicate planner/compactor. **Required verification pattern:** Test 10-turn restore, concurrent branches, cross-tenant context and memory deletion.

#### OC2-D-DATA-001 — Implement OpenConnect.Persistence.EFCore with no concrete SQL Server, SQLite, PostgreSQL or EF InMemory provider package references.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Persistence.EFCore/, tests/Persistence/` or the existing equivalent; record the package/adapter owner. Check `S03` gate and the current source API/contract version.
2. **Concrete implementation:** Implement OpenConnect.Persistence.EFCore with no concrete SQL Server, SQLite, PostgreSQL or EF InMemory provider package references. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Persist native MAF session/checkpoint state according to provider-specific ownership; keep app events separate. Never duplicate planner/compactor. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Prove commit/rollback behavior against an actual supported relational provider. Inject lost ACK, stale fencing token and concurrent writer where the contract touches durable state.
5. **Acceptance artifact:** Test 10-turn restore, concurrent branches, cross-tenant context and memory deletion. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-DATA-001` only after verified pass.

#### OC2-D-DATA-002 — Implement existing ApplicationDbContext adapter registration using application's registered DbContext and provider configuration.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Persistence.EFCore/, tests/Persistence/` or the existing equivalent; record the package/adapter owner. Check `S03` gate and the current source API/contract version.
2. **Concrete implementation:** Implement existing ApplicationDbContext adapter registration using application's registered DbContext and provider configuration. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Persist native MAF session/checkpoint state according to provider-specific ownership; keep app events separate. Never duplicate planner/compactor. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Prove commit/rollback behavior against an actual supported relational provider. Inject lost ACK, stale fencing token and concurrent writer where the contract touches durable state.
5. **Acceptance artifact:** Test 10-turn restore, concurrent branches, cross-tenant context and memory deletion. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-DATA-002` only after verified pass.

#### OC2-D-DATA-003 — Implement ApplyOpenConnectModel() explicit entity mappings with internal entities, no user DbSet properties and no unrelated model scans.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Persistence.EFCore/, tests/Persistence/` or the existing equivalent; record the package/adapter owner. Check `S03` gate and the current source API/contract version.
2. **Concrete implementation:** Implement ApplyOpenConnectModel() explicit entity mappings with internal entities, no user DbSet properties and no unrelated model scans. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Persist native MAF session/checkpoint state according to provider-specific ownership; keep app events separate. Never duplicate planner/compactor. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Run targeted success and deliberate negative regression in CI. Save machine-readable test artifacts and versioned audit evidence.
5. **Acceptance artifact:** Test 10-turn restore, concurrent branches, cross-tenant context and memory deletion. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-DATA-003` only after verified pass.

#### OC2-D-DATA-004 — Provide optional OpenConnectDbContext with explicit provider setup, not inferred from connection string.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Persistence.EFCore/, tests/Persistence/` or the existing equivalent; record the package/adapter owner. Check `S03` gate and the current source API/contract version.
2. **Concrete implementation:** Provide optional OpenConnectDbContext with explicit provider setup, not inferred from connection string. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Persist native MAF session/checkpoint state according to provider-specific ownership; keep app events separate. Never duplicate planner/compactor. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Prove commit/rollback behavior against an actual supported relational provider. Inject lost ACK, stale fencing token and concurrent writer where the contract touches durable state.
5. **Acceptance artifact:** Test 10-turn restore, concurrent branches, cross-tenant context and memory deletion. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-DATA-004` only after verified pass.

#### OC2-D-DATA-005 — Define schema openconnect for PostgreSQL/SQL Server and portable oc_ table prefix for SQLite without pretending named schema exists.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Persistence.EFCore/, tests/Persistence/` or the existing equivalent; record the package/adapter owner. Check `S03` gate and the current source API/contract version.
2. **Concrete implementation:** Define schema openconnect for PostgreSQL/SQL Server and portable oc_ table prefix for SQLite without pretending named schema exists. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Persist native MAF session/checkpoint state according to provider-specific ownership; keep app events separate. Never duplicate planner/compactor. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Prove commit/rollback behavior against an actual supported relational provider. Inject lost ACK, stale fencing token and concurrent writer where the contract touches durable state.
5. **Acceptance artifact:** Test 10-turn restore, concurrent branches, cross-tenant context and memory deletion. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-DATA-005` only after verified pass.

#### OC2-D-DATA-006 — Define and verify schema/model cache keys when mapping-shaping settings vary by provider/schema/prefix.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Persistence.EFCore/, tests/Persistence/` or the existing equivalent; record the package/adapter owner. Check `S03` gate and the current source API/contract version.
2. **Concrete implementation:** Define and verify schema/model cache keys when mapping-shaping settings vary by provider/schema/prefix. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Persist native MAF session/checkpoint state according to provider-specific ownership; keep app events separate. Never duplicate planner/compactor. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Run targeted success and deliberate negative regression in CI. Save machine-readable test artifacts and versioned audit evidence.
5. **Acceptance artifact:** Test 10-turn restore, concurrent branches, cross-tenant context and memory deletion. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-DATA-006` only after verified pass.

#### OC2-D-DATA-007 — Keep migrations Manual by default and prohibit migration/EnsureCreated/EnsureDeleted during DI registration.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Persistence.EFCore/, tests/Persistence/` or the existing equivalent; record the package/adapter owner. Check `S03` gate and the current source API/contract version.
2. **Concrete implementation:** Keep migrations Manual by default and prohibit migration/EnsureCreated/EnsureDeleted during DI registration. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Persist native MAF session/checkpoint state according to provider-specific ownership; keep app events separate. Never duplicate planner/compactor. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Prove commit/rollback behavior against an actual supported relational provider. Inject lost ACK, stale fencing token and concurrent writer where the contract touches durable state.
5. **Acceptance artifact:** Test 10-turn restore, concurrent branches, cross-tenant context and memory deletion. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-DATA-007` only after verified pass.

#### OC2-D-DATA-008 — Generate reviewed shared-context app-owned migration scripts and confirm business tables/rows remain untouched.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Persistence.EFCore/, tests/Persistence/` or the existing equivalent; record the package/adapter owner. Check `S03` gate and the current source API/contract version.
2. **Concrete implementation:** Generate reviewed shared-context app-owned migration scripts and confirm business tables/rows remain untouched. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Persist native MAF session/checkpoint state according to provider-specific ownership; keep app events separate. Never duplicate planner/compactor. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Prove commit/rollback behavior against an actual supported relational provider. Inject lost ACK, stale fencing token and concurrent writer where the contract touches durable state.
5. **Acceptance artifact:** Test 10-turn restore, concurrent branches, cross-tenant context and memory deletion. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-DATA-008` only after verified pass.

#### OC2-D-DATA-009 — Provide provider-compatible dedicated-context migration assembly/history and safe manual migration workflow.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Persistence.EFCore/, tests/Persistence/` or the existing equivalent; record the package/adapter owner. Check `S03` gate and the current source API/contract version.
2. **Concrete implementation:** Provide provider-compatible dedicated-context migration assembly/history and safe manual migration workflow. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Persist native MAF session/checkpoint state according to provider-specific ownership; keep app events separate. Never duplicate planner/compactor. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Prove commit/rollback behavior against an actual supported relational provider. Inject lost ACK, stale fencing token and concurrent writer where the contract touches durable state.
5. **Acceptance artifact:** Test 10-turn restore, concurrent branches, cross-tenant context and memory deletion. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-DATA-009` only after verified pass.

#### OC2-D-DATA-010 — Reject unsafe ApplyOnStartup for shared context; dedicated startup migrations require tested cross-replica coordination.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Persistence.EFCore/, tests/Persistence/` or the existing equivalent; record the package/adapter owner. Check `S03` gate and the current source API/contract version.
2. **Concrete implementation:** Reject unsafe ApplyOnStartup for shared context; dedicated startup migrations require tested cross-replica coordination. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Persist native MAF session/checkpoint state according to provider-specific ownership; keep app events separate. Never duplicate planner/compactor. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Prove commit/rollback behavior against an actual supported relational provider. Inject lost ACK, stale fencing token and concurrent writer where the contract touches durable state.
5. **Acceptance artifact:** Test 10-turn restore, concurrent branches, cross-tenant context and memory deletion. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-DATA-010` only after verified pass.

#### OC2-D-DATA-011 — Map sessions, tasks, turns, sanitized events, outbox, leases, approvals and encrypted token mappings with scoped indexes.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Persistence.EFCore/, tests/Persistence/` or the existing equivalent; record the package/adapter owner. Check `S03` gate and the current source API/contract version.
2. **Concrete implementation:** Map sessions, tasks, turns, sanitized events, outbox, leases, approvals and encrypted token mappings with scoped indexes. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Persist native MAF session/checkpoint state according to provider-specific ownership; keep app events separate. Never duplicate planner/compactor. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Prove commit/rollback behavior against an actual supported relational provider. Inject lost ACK, stale fencing token and concurrent writer where the contract touches durable state.
5. **Acceptance artifact:** Test 10-turn restore, concurrent branches, cross-tenant context and memory deletion. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-DATA-011` only after verified pass.

#### OC2-D-DATA-012 — Map operational schedules/runs/leases and selected optional feature tables only when their modules are installed.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Persistence.EFCore/, tests/Persistence/` or the existing equivalent; record the package/adapter owner. Check `S03` gate and the current source API/contract version.
2. **Concrete implementation:** Map operational schedules/runs/leases and selected optional feature tables only when their modules are installed. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Persist native MAF session/checkpoint state according to provider-specific ownership; keep app events separate. Never duplicate planner/compactor. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Prove commit/rollback behavior against an actual supported relational provider. Inject lost ACK, stale fencing token and concurrent writer where the contract touches durable state.
5. **Acceptance artifact:** Test 10-turn restore, concurrent branches, cross-tenant context and memory deletion. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-DATA-012` only after verified pass.

#### OC2-D-DATA-013 — Define native MAF checkpoint blob/reference store with encryption, compatibility metadata and strict scope.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Persistence.EFCore/, tests/Persistence/` or the existing equivalent; record the package/adapter owner. Check `S03` gate and the current source API/contract version.
2. **Concrete implementation:** Define native MAF checkpoint blob/reference store with encryption, compatibility metadata and strict scope. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Persist native MAF session/checkpoint state according to provider-specific ownership; keep app events separate. Never duplicate planner/compactor. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Exercise authorized and explicitly unauthorized principals/tenants; assert no side effect on denial. Verify no credentials, plaintext or raw user content leave the intended trust boundary.
5. **Acceptance artifact:** Test 10-turn restore, concurrent branches, cross-tenant context and memory deletion. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-DATA-013` only after verified pass.

#### OC2-D-DATA-014 — Implement IOpenConnectPersistenceUnitOfWork for owned/shared/external transaction modes with explicit commit owner.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Persistence.EFCore/, tests/Persistence/` or the existing equivalent; record the package/adapter owner. Check `S03` gate and the current source API/contract version.
2. **Concrete implementation:** Implement IOpenConnectPersistenceUnitOfWork for owned/shared/external transaction modes with explicit commit owner. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Persist native MAF session/checkpoint state according to provider-specific ownership; keep app events separate. Never duplicate planner/compactor. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Prove commit/rollback behavior against an actual supported relational provider. Inject lost ACK, stale fencing token and concurrent writer where the contract touches durable state.
5. **Acceptance artifact:** Test 10-turn restore, concurrent branches, cross-tenant context and memory deletion. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-DATA-014` only after verified pass.

#### OC2-D-DATA-015 — Prove application business changes and OpenConnect outbox are atomic only when they share an actual transaction, not merely a connection string.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Persistence.EFCore/, tests/Persistence/` or the existing equivalent; record the package/adapter owner. Check `S03` gate and the current source API/contract version.
2. **Concrete implementation:** Prove application business changes and OpenConnect outbox are atomic only when they share an actual transaction, not merely a connection string. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Persist native MAF session/checkpoint state according to provider-specific ownership; keep app events separate. Never duplicate planner/compactor. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Prove commit/rollback behavior against an actual supported relational provider. Inject lost ACK, stale fencing token and concurrent writer where the contract touches durable state.
5. **Acceptance artifact:** Test 10-turn restore, concurrent branches, cross-tenant context and memory deletion. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-DATA-015` only after verified pass.

#### OC2-D-DATA-016 — Persist accepted task/session mutation/committed event/outbox in a single relational transaction.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Persistence.EFCore/, tests/Persistence/` or the existing equivalent; record the package/adapter owner. Check `S03` gate and the current source API/contract version.
2. **Concrete implementation:** Persist accepted task/session mutation/committed event/outbox in a single relational transaction. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Persist native MAF session/checkpoint state according to provider-specific ownership; keep app events separate. Never duplicate planner/compactor. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Prove commit/rollback behavior against an actual supported relational provider. Inject lost ACK, stale fencing token and concurrent writer where the contract touches durable state.
5. **Acceptance artifact:** Test 10-turn restore, concurrent branches, cross-tenant context and memory deletion. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-DATA-016` only after verified pass.

#### OC2-D-DATA-017 — Allocate monotonic per-session sequence with atomic concurrency control and test simultaneous admitted turns.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Persistence.EFCore/, tests/Persistence/` or the existing equivalent; record the package/adapter owner. Check `S03` gate and the current source API/contract version.
2. **Concrete implementation:** Allocate monotonic per-session sequence with atomic concurrency control and test simultaneous admitted turns. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Persist native MAF session/checkpoint state according to provider-specific ownership; keep app events separate. Never duplicate planner/compactor. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Invoke an actual pinned MAF type and inspect real lifecycle/session data. Prove unsupported versions/profiles report unavailable instead of fabricating equivalent behavior.
5. **Acceptance artifact:** Test 10-turn restore, concurrent branches, cross-tenant context and memory deletion. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-DATA-017` only after verified pass.

#### OC2-D-DATA-018 — Implement idempotency key + scoped content-hash uniqueness; identical retries return same accepted task, divergent payload conflicts.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Persistence.EFCore/, tests/Persistence/` or the existing equivalent; record the package/adapter owner. Check `S03` gate and the current source API/contract version.
2. **Concrete implementation:** Implement idempotency key + scoped content-hash uniqueness; identical retries return same accepted task, divergent payload conflicts. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Persist native MAF session/checkpoint state according to provider-specific ownership; keep app events separate. Never duplicate planner/compactor. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Exercise authorized and explicitly unauthorized principals/tenants; assert no side effect on denial. Verify no credentials, plaintext or raw user content leave the intended trust boundary.
5. **Acceptance artifact:** Test 10-turn restore, concurrent branches, cross-tenant context and memory deletion. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-DATA-018` only after verified pass.

#### OC2-D-DATA-019 — Implement short fenced outbox claim, broker-ACK publish, delivery marking and expiry recovery without DB transaction across network.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Persistence.EFCore/, tests/Persistence/` or the existing equivalent; record the package/adapter owner. Check `S03` gate and the current source API/contract version.
2. **Concrete implementation:** Implement short fenced outbox claim, broker-ACK publish, delivery marking and expiry recovery without DB transaction across network. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Persist native MAF session/checkpoint state according to provider-specific ownership; keep app events separate. Never duplicate planner/compactor. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Prove commit/rollback behavior against an actual supported relational provider. Inject lost ACK, stale fencing token and concurrent writer where the contract touches durable state.
5. **Acceptance artifact:** Test 10-turn restore, concurrent branches, cross-tenant context and memory deletion. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-DATA-019` only after verified pass.

#### OC2-D-DATA-020 — Implement provider-specific validated claim/CAS strategy; prohibit universal use of Postgres-only SKIP LOCKED or SQLServer rowversion assumptions.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Persistence.EFCore/, tests/Persistence/` or the existing equivalent; record the package/adapter owner. Check `S03` gate and the current source API/contract version.
2. **Concrete implementation:** Implement provider-specific validated claim/CAS strategy; prohibit universal use of Postgres-only SKIP LOCKED or SQLServer rowversion assumptions. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Persist native MAF session/checkpoint state according to provider-specific ownership; keep app events separate. Never duplicate planner/compactor. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Prove commit/rollback behavior against an actual supported relational provider. Inject lost ACK, stale fencing token and concurrent writer where the contract touches durable state.
5. **Acceptance artifact:** Test 10-turn restore, concurrent branches, cross-tenant context and memory deletion. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-DATA-020` only after verified pass.

#### OC2-D-DATA-021 — Test SQL Server multi-host concurrent claims, split-brain fencing and broker failure with real disposable database.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Persistence.EFCore/, tests/Persistence/` or the existing equivalent; record the package/adapter owner. Check `S03` gate and the current source API/contract version.
2. **Concrete implementation:** Test SQL Server multi-host concurrent claims, split-brain fencing and broker failure with real disposable database. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Persist native MAF session/checkpoint state according to provider-specific ownership; keep app events separate. Never duplicate planner/compactor. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Prove commit/rollback behavior against an actual supported relational provider. Inject lost ACK, stale fencing token and concurrent writer where the contract touches durable state.
5. **Acceptance artifact:** Test 10-turn restore, concurrent branches, cross-tenant context and memory deletion. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-DATA-021` only after verified pass.

#### OC2-D-DATA-022 — Test PostgreSQL multi-host concurrent claims, split-brain fencing and broker failure with real disposable database.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Persistence.EFCore/, tests/Persistence/` or the existing equivalent; record the package/adapter owner. Check `S03` gate and the current source API/contract version.
2. **Concrete implementation:** Test PostgreSQL multi-host concurrent claims, split-brain fencing and broker failure with real disposable database. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Persist native MAF session/checkpoint state according to provider-specific ownership; keep app events separate. Never duplicate planner/compactor. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Prove commit/rollback behavior against an actual supported relational provider. Inject lost ACK, stale fencing token and concurrent writer where the contract touches durable state.
5. **Acceptance artifact:** Test 10-turn restore, concurrent branches, cross-tenant context and memory deletion. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-DATA-022` only after verified pass.

#### OC2-D-DATA-023 — Test SQLite file-based restart/lock contention and bounded retry policy; document supported small-instance scale.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Persistence.EFCore/, tests/Persistence/` or the existing equivalent; record the package/adapter owner. Check `S03` gate and the current source API/contract version.
2. **Concrete implementation:** Test SQLite file-based restart/lock contention and bounded retry policy; document supported small-instance scale. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Persist native MAF session/checkpoint state according to provider-specific ownership; keep app events separate. Never duplicate planner/compactor. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Prove commit/rollback behavior against an actual supported relational provider. Inject lost ACK, stale fencing token and concurrent writer where the contract touches durable state.
5. **Acceptance artifact:** Test 10-turn restore, concurrent branches, cross-tenant context and memory deletion. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-DATA-023` only after verified pass.

#### OC2-D-DATA-024 — Test EF InMemory dev-only capability with explicit rejection of production distributed/outbox durability claims.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Persistence.EFCore/, tests/Persistence/` or the existing equivalent; record the package/adapter owner. Check `S03` gate and the current source API/contract version.
2. **Concrete implementation:** Test EF InMemory dev-only capability with explicit rejection of production distributed/outbox durability claims. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Persist native MAF session/checkpoint state according to provider-specific ownership; keep app events separate. Never duplicate planner/compactor. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Prove commit/rollback behavior against an actual supported relational provider. Inject lost ACK, stale fencing token and concurrent writer where the contract touches durable state.
5. **Acceptance artifact:** Test 10-turn restore, concurrent branches, cross-tenant context and memory deletion. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-DATA-024` only after verified pass.

#### OC2-D-DATA-025 — Test rollback, outbox crash-after-publish, duplicate consumer receipt, replay recovery and lease takeover after process kill.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Persistence.EFCore/, tests/Persistence/` or the existing equivalent; record the package/adapter owner. Check `S03` gate and the current source API/contract version.
2. **Concrete implementation:** Test rollback, outbox crash-after-publish, duplicate consumer receipt, replay recovery and lease takeover after process kill. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Persist native MAF session/checkpoint state according to provider-specific ownership; keep app events separate. Never duplicate planner/compactor. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Prove commit/rollback behavior against an actual supported relational provider. Inject lost ACK, stale fencing token and concurrent writer where the contract touches durable state.
5. **Acceptance artifact:** Test 10-turn restore, concurrent branches, cross-tenant context and memory deletion. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-DATA-025` only after verified pass.

#### OC2-D-DATA-026 — Test tenant/actor isolation for all session, approval, checkpoint, vault and background run records.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Persistence.EFCore/, tests/Persistence/` or the existing equivalent; record the package/adapter owner. Check `S03` gate and the current source API/contract version.
2. **Concrete implementation:** Test tenant/actor isolation for all session, approval, checkpoint, vault and background run records. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Persist native MAF session/checkpoint state according to provider-specific ownership; keep app events separate. Never duplicate planner/compactor. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Exercise authorized and explicitly unauthorized principals/tenants; assert no side effect on denial. Verify no credentials, plaintext or raw user content leave the intended trust boundary.
5. **Acceptance artifact:** Test 10-turn restore, concurrent branches, cross-tenant context and memory deletion. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-DATA-026` only after verified pass.

#### OC2-D-DATA-027 — Validate schema readiness without creating tables, and expose exact actionable diagnostics for missing/mismatched mappings.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Persistence.EFCore/, tests/Persistence/` or the existing equivalent; record the package/adapter owner. Check `S03` gate and the current source API/contract version.
2. **Concrete implementation:** Validate schema readiness without creating tables, and expose exact actionable diagnostics for missing/mismatched mappings. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Persist native MAF session/checkpoint state according to provider-specific ownership; keep app events separate. Never duplicate planner/compactor. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Prove commit/rollback behavior against an actual supported relational provider. Inject lost ACK, stale fencing token and concurrent writer where the contract touches durable state.
5. **Acceptance artifact:** Test 10-turn restore, concurrent branches, cross-tenant context and memory deletion. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-DATA-027` only after verified pass.

#### OC2-D-DATA-028 — Document backup/restore, forward-compatible migration, destructive retention/purge, legal hold and rolling upgrade restrictions.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Persistence.EFCore/, tests/Persistence/` or the existing equivalent; record the package/adapter owner. Check `S03` gate and the current source API/contract version.
2. **Concrete implementation:** Document backup/restore, forward-compatible migration, destructive retention/purge, legal hold and rolling upgrade restrictions. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Persist native MAF session/checkpoint state according to provider-specific ownership; keep app events separate. Never duplicate planner/compactor. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Prove commit/rollback behavior against an actual supported relational provider. Inject lost ACK, stale fencing token and concurrent writer where the contract touches durable state.
5. **Acceptance artifact:** Test 10-turn restore, concurrent branches, cross-tenant context and memory deletion. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-DATA-028` only after verified pass.


### S07 — MAF durable workflows and independent operational triggers

**Prerequisites:** `S05,S06`. **Checklist section:** 3.8 MAF Durable workflows, operational scheduling and side-effect ownership. **Scope:** 17 atomic deliverables. **Primary source paths:** `src/OpenConnect.Agents.Microsoft/, src/OpenConnect.Operations/, tests/Durability/`.

**Authority:** Native workflow owns replay/checkpoints; OpenConnect owns admission, actor/lease policy, domain outbox and idempotent effect receipts. **Required verification pattern:** Crash before/after effect, checkpoint/version mismatch, stale fence and restart without double action.

#### OC2-D-DURABLE-001 — Define one selected runtime execution backend per run (native MAF Durable Extension or qualified alternative); do not run competing workflow schedulers.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Agents.Microsoft/, src/OpenConnect.Operations/, tests/Durability/` or the existing equivalent; record the package/adapter owner. Check `S05,S06` gate and the current source API/contract version.
2. **Concrete implementation:** Define one selected runtime execution backend per run (native MAF Durable Extension or qualified alternative); do not run competing workflow schedulers. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Native workflow owns replay/checkpoints; OpenConnect owns admission, actor/lease policy, domain outbox and idempotent effect receipts. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Invoke an actual pinned MAF type and inspect real lifecycle/session data. Prove unsupported versions/profiles report unavailable instead of fabricating equivalent behavior.
5. **Acceptance artifact:** Crash before/after effect, checkpoint/version mismatch, stale fence and restart without double action. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-DURABLE-001` only after verified pass.

#### OC2-D-DURABLE-002 — Build actual MAF Durable Extension lab with relevant storage/hosting and document supported TFMs and checkpoint behavior.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Agents.Microsoft/, src/OpenConnect.Operations/, tests/Durability/` or the existing equivalent; record the package/adapter owner. Check `S05,S06` gate and the current source API/contract version.
2. **Concrete implementation:** Build actual MAF Durable Extension lab with relevant storage/hosting and document supported TFMs and checkpoint behavior. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Native workflow owns replay/checkpoints; OpenConnect owns admission, actor/lease policy, domain outbox and idempotent effect receipts. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify disabled/uninstalled, ready, startup failure and shutdown behavior independently. Validate model/asset manifest hash, license, actual capabilities and bounded resources where applicable.
5. **Acceptance artifact:** Crash before/after effect, checkpoint/version mismatch, stale fence and restart without double action. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-DURABLE-002` only after verified pass.

#### OC2-D-DURABLE-003 — Implement explicit backend option validation and fail if requested durable backend is unavailable or incompatible.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Agents.Microsoft/, src/OpenConnect.Operations/, tests/Durability/` or the existing equivalent; record the package/adapter owner. Check `S05,S06` gate and the current source API/contract version.
2. **Concrete implementation:** Implement explicit backend option validation and fail if requested durable backend is unavailable or incompatible. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Native workflow owns replay/checkpoints; OpenConnect owns admission, actor/lease policy, domain outbox and idempotent effect receipts. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Run targeted success and deliberate negative regression in CI. Save machine-readable test artifacts and versioned audit evidence.
5. **Acceptance artifact:** Crash before/after effect, checkpoint/version mismatch, stale fence and restart without double action. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-DURABLE-003` only after verified pass.

#### OC2-D-DURABLE-004 — Map OpenConnect TaskId/RunId to native workflow instance and checkpoint identity with stable versioned association.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Agents.Microsoft/, src/OpenConnect.Operations/, tests/Durability/` or the existing equivalent; record the package/adapter owner. Check `S05,S06` gate and the current source API/contract version.
2. **Concrete implementation:** Map OpenConnect TaskId/RunId to native workflow instance and checkpoint identity with stable versioned association. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Native workflow owns replay/checkpoints; OpenConnect owns admission, actor/lease policy, domain outbox and idempotent effect receipts. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Prove commit/rollback behavior against an actual supported relational provider. Inject lost ACK, stale fencing token and concurrent writer where the contract touches durable state.
5. **Acceptance artifact:** Crash before/after effect, checkpoint/version mismatch, stale fence and restart without double action. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-DURABLE-004` only after verified pass.

#### OC2-D-DURABLE-005 — Keep OpenConnect domain transaction/outbox separate from native workflow state transaction; document cross-store consistency limits.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Agents.Microsoft/, src/OpenConnect.Operations/, tests/Durability/` or the existing equivalent; record the package/adapter owner. Check `S05,S06` gate and the current source API/contract version.
2. **Concrete implementation:** Keep OpenConnect domain transaction/outbox separate from native workflow state transaction; document cross-store consistency limits. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Native workflow owns replay/checkpoints; OpenConnect owns admission, actor/lease policy, domain outbox and idempotent effect receipts. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Prove commit/rollback behavior against an actual supported relational provider. Inject lost ACK, stale fencing token and concurrent writer where the contract touches durable state.
5. **Acceptance artifact:** Crash before/after effect, checkpoint/version mismatch, stale fence and restart without double action. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-DURABLE-005` only after verified pass.

#### OC2-D-DURABLE-006 — Implement operational workload triggers via bounded scheduler integration without reimplementing native workflow graph traversal.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Agents.Microsoft/, src/OpenConnect.Operations/, tests/Durability/` or the existing equivalent; record the package/adapter owner. Check `S05,S06` gate and the current source API/contract version.
2. **Concrete implementation:** Implement operational workload triggers via bounded scheduler integration without reimplementing native workflow graph traversal. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Native workflow owns replay/checkpoints; OpenConnect owns admission, actor/lease policy, domain outbox and idempotent effect receipts. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Invoke an actual pinned MAF type and inspect real lifecycle/session data. Prove unsupported versions/profiles report unavailable instead of fabricating equivalent behavior.
5. **Acceptance artifact:** Crash before/after effect, checkpoint/version mismatch, stale fence and restart without double action. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-DURABLE-006` only after verified pass.

#### OC2-D-DURABLE-007 — Define cron/timezone and misfire validation, overlap policy, concurrency caps, budgets and max runtime for operational activation.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Agents.Microsoft/, src/OpenConnect.Operations/, tests/Durability/` or the existing equivalent; record the package/adapter owner. Check `S05,S06` gate and the current source API/contract version.
2. **Concrete implementation:** Define cron/timezone and misfire validation, overlap policy, concurrency caps, budgets and max runtime for operational activation. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Native workflow owns replay/checkpoints; OpenConnect owns admission, actor/lease policy, domain outbox and idempotent effect receipts. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify successful integration with canonical contract fixtures. Inject malformed input, cancellation and unavailable dependency; assert typed error/degraded behavior.
5. **Acceptance artifact:** Crash before/after effect, checkpoint/version mismatch, stale fence and restart without double action. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-DURABLE-007` only after verified pass.

#### OC2-D-DURABLE-008 — Implement durable run acceptance, lease/fencing and stable schedule-occurrence idempotency when the chosen backend requires it.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Agents.Microsoft/, src/OpenConnect.Operations/, tests/Durability/` or the existing equivalent; record the package/adapter owner. Check `S05,S06` gate and the current source API/contract version.
2. **Concrete implementation:** Implement durable run acceptance, lease/fencing and stable schedule-occurrence idempotency when the chosen backend requires it. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Native workflow owns replay/checkpoints; OpenConnect owns admission, actor/lease policy, domain outbox and idempotent effect receipts. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Prove commit/rollback behavior against an actual supported relational provider. Inject lost ACK, stale fencing token and concurrent writer where the contract touches durable state.
5. **Acceptance artifact:** Crash before/after effect, checkpoint/version mismatch, stale fence and restart without double action. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-DURABLE-008` only after verified pass.

#### OC2-D-DURABLE-009 — Prevent double lease ownership when MAF durable backend provides distributed leases; only one component owns execution lease.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Agents.Microsoft/, src/OpenConnect.Operations/, tests/Durability/` or the existing equivalent; record the package/adapter owner. Check `S05,S06` gate and the current source API/contract version.
2. **Concrete implementation:** Prevent double lease ownership when MAF durable backend provides distributed leases; only one component owns execution lease. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Native workflow owns replay/checkpoints; OpenConnect owns admission, actor/lease policy, domain outbox and idempotent effect receipts. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Prove commit/rollback behavior against an actual supported relational provider. Inject lost ACK, stale fencing token and concurrent writer where the contract touches durable state.
5. **Acceptance artifact:** Crash before/after effect, checkpoint/version mismatch, stale fence and restart without double action. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-DURABLE-009` only after verified pass.

#### OC2-D-DURABLE-010 — Persist exact planned external side-effect intent and result receipt with idempotency/reconciliation in OpenConnect domain store.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Agents.Microsoft/, src/OpenConnect.Operations/, tests/Durability/` or the existing equivalent; record the package/adapter owner. Check `S05,S06` gate and the current source API/contract version.
2. **Concrete implementation:** Persist exact planned external side-effect intent and result receipt with idempotency/reconciliation in OpenConnect domain store. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Native workflow owns replay/checkpoints; OpenConnect owns admission, actor/lease policy, domain outbox and idempotent effect receipts. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Run targeted success and deliberate negative regression in CI. Save machine-readable test artifacts and versioned audit evidence.
5. **Acceptance artifact:** Crash before/after effect, checkpoint/version mismatch, stale fence and restart without double action. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-DURABLE-010` only after verified pass.

#### OC2-D-DURABLE-011 — Implement prompt/approval pause using native MAF continuation, not a custom execution loop replaying every step.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Agents.Microsoft/, src/OpenConnect.Operations/, tests/Durability/` or the existing equivalent; record the package/adapter owner. Check `S05,S06` gate and the current source API/contract version.
2. **Concrete implementation:** Implement prompt/approval pause using native MAF continuation, not a custom execution loop replaying every step. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Native workflow owns replay/checkpoints; OpenConnect owns admission, actor/lease policy, domain outbox and idempotent effect receipts. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Exercise authorized and explicitly unauthorized principals/tenants; assert no side effect on denial. Verify no credentials, plaintext or raw user content leave the intended trust boundary.
5. **Acceptance artifact:** Crash before/after effect, checkpoint/version mismatch, stale fence and restart without double action. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-DURABLE-011` only after verified pass.

#### OC2-D-DURABLE-012 — Revalidate principal/workload grants and privacy policy before any resumed external effect.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Agents.Microsoft/, src/OpenConnect.Operations/, tests/Durability/` or the existing equivalent; record the package/adapter owner. Check `S05,S06` gate and the current source API/contract version.
2. **Concrete implementation:** Revalidate principal/workload grants and privacy policy before any resumed external effect. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Native workflow owns replay/checkpoints; OpenConnect owns admission, actor/lease policy, domain outbox and idempotent effect receipts. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Exercise authorized and explicitly unauthorized principals/tenants; assert no side effect on denial. Verify no credentials, plaintext or raw user content leave the intended trust boundary.
5. **Acceptance artifact:** Crash before/after effect, checkpoint/version mismatch, stale fence and restart without double action. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-DURABLE-012` only after verified pass.

#### OC2-D-DURABLE-013 — Implement cancelled/failed/stale run status mapping from real native runtime and committed OpenConnect events.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Agents.Microsoft/, src/OpenConnect.Operations/, tests/Durability/` or the existing equivalent; record the package/adapter owner. Check `S05,S06` gate and the current source API/contract version.
2. **Concrete implementation:** Implement cancelled/failed/stale run status mapping from real native runtime and committed OpenConnect events. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Native workflow owns replay/checkpoints; OpenConnect owns admission, actor/lease policy, domain outbox and idempotent effect receipts. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify successful integration with canonical contract fixtures. Inject malformed input, cancellation and unavailable dependency; assert typed error/degraded behavior.
5. **Acceptance artifact:** Crash before/after effect, checkpoint/version mismatch, stale fence and restart without double action. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-DURABLE-013` only after verified pass.

#### OC2-D-DURABLE-014 — Document unknown external-effect reconciliation after crash and prohibit automatic replay of uncertain writes.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Agents.Microsoft/, src/OpenConnect.Operations/, tests/Durability/` or the existing equivalent; record the package/adapter owner. Check `S05,S06` gate and the current source API/contract version.
2. **Concrete implementation:** Document unknown external-effect reconciliation after crash and prohibit automatic replay of uncertain writes. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Native workflow owns replay/checkpoints; OpenConnect owns admission, actor/lease policy, domain outbox and idempotent effect receipts. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Validate generated/readme/code snippet claims against built assemblies or referenced source. Detect references to superseded APIs, stale paths and unsupported capabilities.
5. **Acceptance artifact:** Crash before/after effect, checkpoint/version mismatch, stale fence and restart without double action. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-DURABLE-014` only after verified pass.

#### OC2-D-DURABLE-015 — Test host restart between workflow checkpoint and external tool acceptance without duplicate native executor or business effects.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Agents.Microsoft/, src/OpenConnect.Operations/, tests/Durability/` or the existing equivalent; record the package/adapter owner. Check `S05,S06` gate and the current source API/contract version.
2. **Concrete implementation:** Test host restart between workflow checkpoint and external tool acceptance without duplicate native executor or business effects. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Native workflow owns replay/checkpoints; OpenConnect owns admission, actor/lease policy, domain outbox and idempotent effect receipts. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify disabled/uninstalled, ready, startup failure and shutdown behavior independently. Validate model/asset manifest hash, license, actual capabilities and bounded resources where applicable.
5. **Acceptance artifact:** Crash before/after effect, checkpoint/version mismatch, stale fence and restart without double action. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-DURABLE-015` only after verified pass.

#### OC2-D-DURABLE-016 — Test work distributed over two host instances with a real lease backend and deterministic run outcome.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Agents.Microsoft/, src/OpenConnect.Operations/, tests/Durability/` or the existing equivalent; record the package/adapter owner. Check `S05,S06` gate and the current source API/contract version.
2. **Concrete implementation:** Test work distributed over two host instances with a real lease backend and deterministic run outcome. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Native workflow owns replay/checkpoints; OpenConnect owns admission, actor/lease policy, domain outbox and idempotent effect receipts. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Prove commit/rollback behavior against an actual supported relational provider. Inject lost ACK, stale fencing token and concurrent writer where the contract touches durable state.
5. **Acceptance artifact:** Crash before/after effect, checkpoint/version mismatch, stale fence and restart without double action. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-DURABLE-016` only after verified pass.

#### OC2-D-DURABLE-017 — Test MAF package upgrade/checkpoint incompatible restore fails safe with migration/recovery playbook.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Agents.Microsoft/, src/OpenConnect.Operations/, tests/Durability/` or the existing equivalent; record the package/adapter owner. Check `S05,S06` gate and the current source API/contract version.
2. **Concrete implementation:** Test MAF package upgrade/checkpoint incompatible restore fails safe with migration/recovery playbook. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Native workflow owns replay/checkpoints; OpenConnect owns admission, actor/lease policy, domain outbox and idempotent effect receipts. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Prove commit/rollback behavior against an actual supported relational provider. Inject lost ACK, stale fencing token and concurrent writer where the contract touches durable state.
5. **Acceptance artifact:** Crash before/after effect, checkpoint/version mismatch, stale fence and restart without double action. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-DURABLE-017` only after verified pass.


### S08 — Truthful durable tools, progress, cancel and resume

**Prerequisites:** `S04,S06,S07`. **Checklist section:** 3.9 Long-running tool acceptance, truthful progress, cancellation and resume. **Scope:** 19 atomic deliverables. **Primary source paths:** `src/OpenConnect.Tools/, src/OpenConnect.Sessions/, contracts/events/`.

**Authority:** Atomic tool.accepted before external start for declared long tools; measured progress only, heartbeat means liveness, one native MAF result continuation. **Required verification pattern:** Fault tests for disconnected client, 100% before commit, cancel race and uncertain external effect.

#### OC2-D-ACK-001 — Define ToolExecutionMode Inline versus BackgroundDurable and typed expected-duration policy; require explicit long-running declaration for guaranteed pre-start ACK.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Tools/, src/OpenConnect.Sessions/, contracts/events/` or the existing equivalent; record the package/adapter owner. Check `S04,S06,S07` gate and the current source API/contract version.
2. **Concrete implementation:** Define ToolExecutionMode Inline versus BackgroundDurable and typed expected-duration policy; require explicit long-running declaration for guaranteed pre-start ACK. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Atomic tool.accepted before external start for declared long tools; measured progress only, heartbeat means liveness, one native MAF result continuation. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Run targeted success and deliberate negative regression in CI. Save machine-readable test artifacts and versioned audit evidence.
5. **Acceptance artifact:** Fault tests for disconnected client, 100% before commit, cancel race and uncertain external effect. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-ACK-001` only after verified pass.

#### OC2-D-ACK-002 — Create durable invocation row with stable invocation ID, attempt, owner, canonical argument hash, tool revision and auth/policy snapshot.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Tools/, src/OpenConnect.Sessions/, contracts/events/` or the existing equivalent; record the package/adapter owner. Check `S04,S06,S07` gate and the current source API/contract version.
2. **Concrete implementation:** Create durable invocation row with stable invocation ID, attempt, owner, canonical argument hash, tool revision and auth/policy snapshot. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Atomic tool.accepted before external start for declared long tools; measured progress only, heartbeat means liveness, one native MAF result continuation. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Prove commit/rollback behavior against an actual supported relational provider. Inject lost ACK, stale fencing token and concurrent writer where the contract touches durable state.
5. **Acceptance artifact:** Fault tests for disconnected client, 100% before commit, cancel race and uncertain external effect. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-ACK-002` only after verified pass.

#### OC2-D-ACK-003 — Persist approval request before queued state for approval-gated tools; never indicate queued/executing while approval pending.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Tools/, src/OpenConnect.Sessions/, contracts/events/` or the existing equivalent; record the package/adapter owner. Check `S04,S06,S07` gate and the current source API/contract version.
2. **Concrete implementation:** Persist approval request before queued state for approval-gated tools; never indicate queued/executing while approval pending. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Atomic tool.accepted before external start for declared long tools; measured progress only, heartbeat means liveness, one native MAF result continuation. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Exercise authorized and explicitly unauthorized principals/tenants; assert no side effect on denial. Verify no credentials, plaintext or raw user content leave the intended trust boundary.
5. **Acceptance artifact:** Fault tests for disconnected client, 100% before commit, cancel race and uncertain external effect. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-ACK-003` only after verified pass.

#### OC2-D-ACK-004 — Commit tool.accepted event, invocation state and outbox atomically before dispatching a declared BackgroundDurable tool.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Tools/, src/OpenConnect.Sessions/, contracts/events/` or the existing equivalent; record the package/adapter owner. Check `S04,S06,S07` gate and the current source API/contract version.
2. **Concrete implementation:** Commit tool.accepted event, invocation state and outbox atomically before dispatching a declared BackgroundDurable tool. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Atomic tool.accepted before external start for declared long tools; measured progress only, heartbeat means liveness, one native MAF result continuation. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Prove commit/rollback behavior against an actual supported relational provider. Inject lost ACK, stale fencing token and concurrent writer where the contract touches durable state.
5. **Acceptance artifact:** Fault tests for disconnected client, 100% before commit, cancel race and uncertain external effect. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-ACK-004` only after verified pass.

#### OC2-D-ACK-005 — Distinguish durable acceptance from actual client display delivery; do not wait for browser render ACK before executing.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Tools/, src/OpenConnect.Sessions/, contracts/events/` or the existing equivalent; record the package/adapter owner. Check `S04,S06,S07` gate and the current source API/contract version.
2. **Concrete implementation:** Distinguish durable acceptance from actual client display delivery; do not wait for browser render ACK before executing. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Atomic tool.accepted before external start for declared long tools; measured progress only, heartbeat means liveness, one native MAF result continuation. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify successful integration with canonical contract fixtures. Inject malformed input, cancellation and unavailable dependency; assert typed error/degraded behavior.
5. **Acceptance artifact:** Fault tests for disconnected client, 100% before commit, cancel race and uncertain external effect. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-ACK-005` only after verified pass.

#### OC2-D-ACK-006 — Persist real tool.started only after handler/worker claims task and execution actually begins.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Tools/, src/OpenConnect.Sessions/, contracts/events/` or the existing equivalent; record the package/adapter owner. Check `S04,S06,S07` gate and the current source API/contract version.
2. **Concrete implementation:** Persist real tool.started only after handler/worker claims task and execution actually begins. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Atomic tool.accepted before external start for declared long tools; measured progress only, heartbeat means liveness, one native MAF result continuation. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify disabled/uninstalled, ready, startup failure and shutdown behavior independently. Validate model/asset manifest hash, license, actual capabilities and bounded resources where applicable.
5. **Acceptance artifact:** Fault tests for disconnected client, 100% before commit, cancel race and uncertain external effect. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-ACK-006` only after verified pass.

#### OC2-D-ACK-007 — Expose measured phase, completed units and verified total with monotonic bounds; never synthesize percentages.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Tools/, src/OpenConnect.Sessions/, contracts/events/` or the existing equivalent; record the package/adapter owner. Check `S04,S06,S07` gate and the current source API/contract version.
2. **Concrete implementation:** Expose measured phase, completed units and verified total with monotonic bounds; never synthesize percentages. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Atomic tool.accepted before external start for declared long tools; measured progress only, heartbeat means liveness, one native MAF result continuation. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify successful integration with canonical contract fixtures. Inject malformed input, cancellation and unavailable dependency; assert typed error/degraded behavior.
5. **Acceptance artifact:** Fault tests for disconnected client, 100% before commit, cancel race and uncertain external effect. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-ACK-007` only after verified pass.

#### OC2-D-ACK-008 — Keep progress events bounded/coalesced and allow independently configured latest snapshot vs transient updates.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Tools/, src/OpenConnect.Sessions/, contracts/events/` or the existing equivalent; record the package/adapter owner. Check `S04,S06,S07` gate and the current source API/contract version.
2. **Concrete implementation:** Keep progress events bounded/coalesced and allow independently configured latest snapshot vs transient updates. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Atomic tool.accepted before external start for declared long tools; measured progress only, heartbeat means liveness, one native MAF result continuation. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Run targeted success and deliberate negative regression in CI. Save machine-readable test artifacts and versioned audit evidence.
5. **Acceptance artifact:** Fault tests for disconnected client, 100% before commit, cancel race and uncertain external effect. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-ACK-008` only after verified pass.

#### OC2-D-ACK-009 — Emit heartbeat only from verified lease/process liveness; prevent heartbeat from implying actual work advanced.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Tools/, src/OpenConnect.Sessions/, contracts/events/` or the existing equivalent; record the package/adapter owner. Check `S04,S06,S07` gate and the current source API/contract version.
2. **Concrete implementation:** Emit heartbeat only from verified lease/process liveness; prevent heartbeat from implying actual work advanced. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Atomic tool.accepted before external start for declared long tools; measured progress only, heartbeat means liveness, one native MAF result continuation. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Prove commit/rollback behavior against an actual supported relational provider. Inject lost ACK, stale fencing token and concurrent writer where the contract touches durable state.
5. **Acceptance artifact:** Fault tests for disconnected client, 100% before commit, cancel race and uncertain external effect. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-ACK-009` only after verified pass.

#### OC2-D-ACK-010 — Emit explicit Finalizing when work reports 100 percent but output validation or durable commit remains pending.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Tools/, src/OpenConnect.Sessions/, contracts/events/` or the existing equivalent; record the package/adapter owner. Check `S04,S06,S07` gate and the current source API/contract version.
2. **Concrete implementation:** Emit explicit Finalizing when work reports 100 percent but output validation or durable commit remains pending. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Atomic tool.accepted before external start for declared long tools; measured progress only, heartbeat means liveness, one native MAF result continuation. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Run targeted success and deliberate negative regression in CI. Save machine-readable test artifacts and versioned audit evidence.
5. **Acceptance artifact:** Fault tests for disconnected client, 100% before commit, cancel race and uncertain external effect. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-ACK-010` only after verified pass.

#### OC2-D-ACK-011 — Validate tool result schema/privacy and commit terminal event before telling native MAF continuation the operation succeeded.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Tools/, src/OpenConnect.Sessions/, contracts/events/` or the existing equivalent; record the package/adapter owner. Check `S04,S06,S07` gate and the current source API/contract version.
2. **Concrete implementation:** Validate tool result schema/privacy and commit terminal event before telling native MAF continuation the operation succeeded. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Atomic tool.accepted before external start for declared long tools; measured progress only, heartbeat means liveness, one native MAF result continuation. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Exercise authorized and explicitly unauthorized principals/tenants; assert no side effect on denial. Verify no credentials, plaintext or raw user content leave the intended trust boundary.
5. **Acceptance artifact:** Fault tests for disconnected client, 100% before commit, cancel race and uncertain external effect. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-ACK-011` only after verified pass.

#### OC2-D-ACK-012 — Resume MAF pending tool request with one logical result across duplicate notifications/restarts; deduplicate by invocation receipt.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Tools/, src/OpenConnect.Sessions/, contracts/events/` or the existing equivalent; record the package/adapter owner. Check `S04,S06,S07` gate and the current source API/contract version.
2. **Concrete implementation:** Resume MAF pending tool request with one logical result across duplicate notifications/restarts; deduplicate by invocation receipt. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Atomic tool.accepted before external start for declared long tools; measured progress only, heartbeat means liveness, one native MAF result continuation. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Invoke an actual pinned MAF type and inspect real lifecycle/session data. Prove unsupported versions/profiles report unavailable instead of fabricating equivalent behavior.
5. **Acceptance artifact:** Fault tests for disconnected client, 100% before commit, cancel race and uncertain external effect. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-ACK-012` only after verified pass.

#### OC2-D-ACK-013 — Map cancellation.requested vs confirmed cancelled/completed/outcome_unknown as separate transitions with racing outcome test.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Tools/, src/OpenConnect.Sessions/, contracts/events/` or the existing equivalent; record the package/adapter owner. Check `S04,S06,S07` gate and the current source API/contract version.
2. **Concrete implementation:** Map cancellation.requested vs confirmed cancelled/completed/outcome_unknown as separate transitions with racing outcome test. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Atomic tool.accepted before external start for declared long tools; measured progress only, heartbeat means liveness, one native MAF result continuation. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Run targeted success and deliberate negative regression in CI. Save machine-readable test artifacts and versioned audit evidence.
5. **Acceptance artifact:** Fault tests for disconnected client, 100% before commit, cancel race and uncertain external effect. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-ACK-013` only after verified pass.

#### OC2-D-ACK-014 — Do not retry uncertain non-idempotent remote effects without reconciliation; provide operator review/recovery endpoint.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Tools/, src/OpenConnect.Sessions/, contracts/events/` or the existing equivalent; record the package/adapter owner. Check `S04,S06,S07` gate and the current source API/contract version.
2. **Concrete implementation:** Do not retry uncertain non-idempotent remote effects without reconciliation; provide operator review/recovery endpoint. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Atomic tool.accepted before external start for declared long tools; measured progress only, heartbeat means liveness, one native MAF result continuation. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Run targeted success and deliberate negative regression in CI. Save machine-readable test artifacts and versioned audit evidence.
5. **Acceptance artifact:** Fault tests for disconnected client, 100% before commit, cancel race and uncertain external effect. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-ACK-014` only after verified pass.

#### OC2-D-ACK-015 — Implement timeout/watchdog for missing heartbeat, stale lease, cancellation and dead-letter quarantine with safe diagnostics.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Tools/, src/OpenConnect.Sessions/, contracts/events/` or the existing equivalent; record the package/adapter owner. Check `S04,S06,S07` gate and the current source API/contract version.
2. **Concrete implementation:** Implement timeout/watchdog for missing heartbeat, stale lease, cancellation and dead-letter quarantine with safe diagnostics. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Atomic tool.accepted before external start for declared long tools; measured progress only, heartbeat means liveness, one native MAF result continuation. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Prove commit/rollback behavior against an actual supported relational provider. Inject lost ACK, stale fencing token and concurrent writer where the contract touches durable state.
5. **Acceptance artifact:** Fault tests for disconnected client, 100% before commit, cancel race and uncertain external effect. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-ACK-015` only after verified pass.

#### OC2-D-ACK-016 — Expose task/get tool status endpoint and replay snapshot, with tenant/actor scope and no raw arguments in browser event.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Tools/, src/OpenConnect.Sessions/, contracts/events/` or the existing equivalent; record the package/adapter owner. Check `S04,S06,S07` gate and the current source API/contract version.
2. **Concrete implementation:** Expose task/get tool status endpoint and replay snapshot, with tenant/actor scope and no raw arguments in browser event. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Atomic tool.accepted before external start for declared long tools; measured progress only, heartbeat means liveness, one native MAF result continuation. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Exercise authorized and explicitly unauthorized principals/tenants; assert no side effect on denial. Verify no credentials, plaintext or raw user content leave the intended trust boundary.
5. **Acceptance artifact:** Fault tests for disconnected client, 100% before commit, cancel race and uncertain external effect. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-ACK-016` only after verified pass.

#### OC2-D-ACK-017 — Use deterministic localized status keys for MiniComposer, not additional LLM-generated chat turns or unmetered commentary.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Tools/, src/OpenConnect.Sessions/, contracts/events/` or the existing equivalent; record the package/adapter owner. Check `S04,S06,S07` gate and the current source API/contract version.
2. **Concrete implementation:** Use deterministic localized status keys for MiniComposer, not additional LLM-generated chat turns or unmetered commentary. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Atomic tool.accepted before external start for declared long tools; measured progress only, heartbeat means liveness, one native MAF result continuation. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify successful integration with canonical contract fixtures. Inject malformed input, cancellation and unavailable dependency; assert typed error/degraded behavior.
5. **Acceptance artifact:** Fault tests for disconnected client, 100% before commit, cancel race and uncertain external effect. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-ACK-017` only after verified pass.

#### OC2-D-ACK-018 — Test a disconnected client before ACK delivery, a second client reconnect and a tool finish under WS outage.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Tools/, src/OpenConnect.Sessions/, contracts/events/` or the existing equivalent; record the package/adapter owner. Check `S04,S06,S07` gate and the current source API/contract version.
2. **Concrete implementation:** Test a disconnected client before ACK delivery, a second client reconnect and a tool finish under WS outage. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Atomic tool.accepted before external start for declared long tools; measured progress only, heartbeat means liveness, one native MAF result continuation. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Run targeted success and deliberate negative regression in CI. Save machine-readable test artifacts and versioned audit evidence.
5. **Acceptance artifact:** Fault tests for disconnected client, 100% before commit, cancel race and uncertain external effect. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-ACK-018` only after verified pass.

#### OC2-D-ACK-019 — Test progress floods/backpressure, re-ordered events, duplicate NATS notices, age-bounded replay and percent=100-before-commit fault.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Tools/, src/OpenConnect.Sessions/, contracts/events/` or the existing equivalent; record the package/adapter owner. Check `S04,S06,S07` gate and the current source API/contract version.
2. **Concrete implementation:** Test progress floods/backpressure, re-ordered events, duplicate NATS notices, age-bounded replay and percent=100-before-commit fault. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Atomic tool.accepted before external start for declared long tools; measured progress only, heartbeat means liveness, one native MAF result continuation. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Run targeted success and deliberate negative regression in CI. Save machine-readable test artifacts and versioned audit evidence.
5. **Acceptance artifact:** Fault tests for disconnected client, 100% before commit, cancel race and uncertain external effect. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-ACK-019` only after verified pass.


### S09 — Host privacy interlock, vault and detector integrations

**Prerequisites:** `S03,S04,S06`. **Checklist section:** 3.10 Default-on privacy firewall, detector coverage, vault and information flow. **Scope:** 23 atomic deliverables. **Primary source paths:** `src/OpenConnect.Privacy/, workers/security/, tests/Privacy/`.

**Authority:** Inline allow/mask/block/review at every model/tool/context/workers/telemetry boundary, authenticated encrypted mapping and least-privilege restoration. **Required verification pattern:** Synthetic canary exfiltration, missing required detector, cross-scope restoration and model-output bypass.

#### OC2-D-PRIVACY-001 — Register .NET host privacy enforcement default-on and separate always-on authentication/authorization regardless privacy enabled state.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Privacy/, workers/security/, tests/Privacy/` or the existing equivalent; record the package/adapter owner. Check `S03,S04,S06` gate and the current source API/contract version.
2. **Concrete implementation:** Register .NET host privacy enforcement default-on and separate always-on authentication/authorization regardless privacy enabled state. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Inline allow/mask/block/review at every model/tool/context/workers/telemetry boundary, authenticated encrypted mapping and least-privilege restoration. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Exercise authorized and explicitly unauthorized principals/tenants; assert no side effect on denial. Verify no credentials, plaintext or raw user content leave the intended trust boundary.
5. **Acceptance artifact:** Synthetic canary exfiltration, missing required detector, cross-scope restoration and model-output bypass. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-PRIVACY-001` only after verified pass.

#### OC2-D-PRIVACY-002 — Define policy snapshots with trust zone, data class, actor, session/task, detector coverage requirements, destinations and revisions.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Privacy/, workers/security/, tests/Privacy/` or the existing equivalent; record the package/adapter owner. Check `S03,S04,S06` gate and the current source API/contract version.
2. **Concrete implementation:** Define policy snapshots with trust zone, data class, actor, session/task, detector coverage requirements, destinations and revisions. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Inline allow/mask/block/review at every model/tool/context/workers/telemetry boundary, authenticated encrypted mapping and least-privilege restoration. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Invoke an actual pinned MAF type and inspect real lifecycle/session data. Prove unsupported versions/profiles report unavailable instead of fabricating equivalent behavior.
5. **Acceptance artifact:** Synthetic canary exfiltration, missing required detector, cross-scope restoration and model-output bypass. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-PRIVACY-002` only after verified pass.

#### OC2-D-PRIVACY-003 — Implement authenticated local security-worker channel with strict size/deadline/resource bounds and no worker DB/vault key access.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Privacy/, workers/security/, tests/Privacy/` or the existing equivalent; record the package/adapter owner. Check `S03,S04,S06` gate and the current source API/contract version.
2. **Concrete implementation:** Implement authenticated local security-worker channel with strict size/deadline/resource bounds and no worker DB/vault key access. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Inline allow/mask/block/review at every model/tool/context/workers/telemetry boundary, authenticated encrypted mapping and least-privilege restoration. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Exercise authorized and explicitly unauthorized principals/tenants; assert no side effect on denial. Verify no credentials, plaintext or raw user content leave the intended trust boundary.
5. **Acceptance artifact:** Synthetic canary exfiltration, missing required detector, cross-scope restoration and model-output bypass. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-PRIVACY-003` only after verified pass.

#### OC2-D-PRIVACY-004 — Implement deterministic local PII/secrets/checksum rules and optional custom matchers with stable Unicode-safe offsets.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Privacy/, workers/security/, tests/Privacy/` or the existing equivalent; record the package/adapter owner. Check `S03,S04,S06` gate and the current source API/contract version.
2. **Concrete implementation:** Implement deterministic local PII/secrets/checksum rules and optional custom matchers with stable Unicode-safe offsets. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Inline allow/mask/block/review at every model/tool/context/workers/telemetry boundary, authenticated encrypted mapping and least-privilege restoration. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Prove commit/rollback behavior against an actual supported relational provider. Inject lost ACK, stale fencing token and concurrent writer where the contract touches durable state.
5. **Acceptance artifact:** Synthetic canary exfiltration, missing required detector, cross-scope restoration and model-output bypass. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-PRIVACY-004` only after verified pass.

#### OC2-D-PRIVACY-005 — Enable GLiNER semantic PII independently with pinned reviewed checkpoint and truthful supported language/entity capabilities.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Privacy/, workers/security/, tests/Privacy/` or the existing equivalent; record the package/adapter owner. Check `S03,S04,S06` gate and the current source API/contract version.
2. **Concrete implementation:** Enable GLiNER semantic PII independently with pinned reviewed checkpoint and truthful supported language/entity capabilities. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Inline allow/mask/block/review at every model/tool/context/workers/telemetry boundary, authenticated encrypted mapping and least-privilege restoration. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify disabled/uninstalled, ready, startup failure and shutdown behavior independently. Validate model/asset manifest hash, license, actual capabilities and bounded resources where applicable.
5. **Acceptance artifact:** Synthetic canary exfiltration, missing required detector, cross-scope restoration and model-output bypass. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-PRIVACY-005` only after verified pass.

#### OC2-D-PRIVACY-006 — Enable DeBERTa injection independently and distinguish probabilistic risk signal from permission authority.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Privacy/, workers/security/, tests/Privacy/` or the existing equivalent; record the package/adapter owner. Check `S03,S04,S06` gate and the current source API/contract version.
2. **Concrete implementation:** Enable DeBERTa injection independently and distinguish probabilistic risk signal from permission authority. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Inline allow/mask/block/review at every model/tool/context/workers/telemetry boundary, authenticated encrypted mapping and least-privilege restoration. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify successful integration with canonical contract fixtures. Inject malformed input, cancellation and unavailable dependency; assert typed error/degraded behavior.
5. **Acceptance artifact:** Synthetic canary exfiltration, missing required detector, cross-scope restoration and model-output bypass. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-PRIVACY-006` only after verified pass.

#### OC2-D-PRIVACY-007 — Enable Qwen3Guard independently with calibrated/tested safety categories and no universal safety claim.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Privacy/, workers/security/, tests/Privacy/` or the existing equivalent; record the package/adapter owner. Check `S03,S04,S06` gate and the current source API/contract version.
2. **Concrete implementation:** Enable Qwen3Guard independently with calibrated/tested safety categories and no universal safety claim. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Inline allow/mask/block/review at every model/tool/context/workers/telemetry boundary, authenticated encrypted mapping and least-privilege restoration. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Run targeted success and deliberate negative regression in CI. Save machine-readable test artifacts and versioned audit evidence.
5. **Acceptance artifact:** Synthetic canary exfiltration, missing required detector, cross-scope restoration and model-output bypass. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-PRIVACY-007` only after verified pass.

#### OC2-D-PRIVACY-008 — Enable PaddleOCR independently with original-image and PDF hidden-layer security tests.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Privacy/, workers/security/, tests/Privacy/` or the existing equivalent; record the package/adapter owner. Check `S03,S04,S06` gate and the current source API/contract version.
2. **Concrete implementation:** Enable PaddleOCR independently with original-image and PDF hidden-layer security tests. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Inline allow/mask/block/review at every model/tool/context/workers/telemetry boundary, authenticated encrypted mapping and least-privilege restoration. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Exercise authorized and explicitly unauthorized principals/tenants; assert no side effect on denial. Verify no credentials, plaintext or raw user content leave the intended trust boundary.
5. **Acceptance artifact:** Synthetic canary exfiltration, missing required detector, cross-scope restoration and model-output bypass. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-PRIVACY-008` only after verified pass.

#### OC2-D-PRIVACY-009 — Enable YuNet independently with actual image redaction/face detection verification when policy requires it.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Privacy/, workers/security/, tests/Privacy/` or the existing equivalent; record the package/adapter owner. Check `S03,S04,S06` gate and the current source API/contract version.
2. **Concrete implementation:** Enable YuNet independently with actual image redaction/face detection verification when policy requires it. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Inline allow/mask/block/review at every model/tool/context/workers/telemetry boundary, authenticated encrypted mapping and least-privilege restoration. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify disabled/uninstalled, ready, startup failure and shutdown behavior independently. Validate model/asset manifest hash, license, actual capabilities and bounded resources where applicable.
5. **Acceptance artifact:** Synthetic canary exfiltration, missing required detector, cross-scope restoration and model-output bypass. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-PRIVACY-009` only after verified pass.

#### OC2-D-PRIVACY-010 — Prove all disabled detector paths avoid downloading, importing model weights, allocating GPU sessions or advertising capabilities.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Privacy/, workers/security/, tests/Privacy/` or the existing equivalent; record the package/adapter owner. Check `S03,S04,S06` gate and the current source API/contract version.
2. **Concrete implementation:** Prove all disabled detector paths avoid downloading, importing model weights, allocating GPU sessions or advertising capabilities. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Inline allow/mask/block/review at every model/tool/context/workers/telemetry boundary, authenticated encrypted mapping and least-privilege restoration. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify disabled/uninstalled, ready, startup failure and shutdown behavior independently. Validate model/asset manifest hash, license, actual capabilities and bounded resources where applicable.
5. **Acceptance artifact:** Synthetic canary exfiltration, missing required detector, cross-scope restoration and model-output bypass. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-PRIVACY-010` only after verified pass.

#### OC2-D-PRIVACY-011 — Validate detector signals against host-owned input bounds; reject malicious/invalid offsets and forged allow decisions.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Privacy/, workers/security/, tests/Privacy/` or the existing equivalent; record the package/adapter owner. Check `S03,S04,S06` gate and the current source API/contract version.
2. **Concrete implementation:** Validate detector signals against host-owned input bounds; reject malicious/invalid offsets and forged allow decisions. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Inline allow/mask/block/review at every model/tool/context/workers/telemetry boundary, authenticated encrypted mapping and least-privilege restoration. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Run targeted success and deliberate negative regression in CI. Save machine-readable test artifacts and versioned audit evidence.
5. **Acceptance artifact:** Synthetic canary exfiltration, missing required detector, cross-scope restoration and model-output bypass. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-PRIVACY-011` only after verified pass.

#### OC2-D-PRIVACY-012 — Implement required vs achieved coverage matrix per operation/media/destination, with fail-closed or approved review/local-only routes.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Privacy/, workers/security/, tests/Privacy/` or the existing equivalent; record the package/adapter owner. Check `S03,S04,S06` gate and the current source API/contract version.
2. **Concrete implementation:** Implement required vs achieved coverage matrix per operation/media/destination, with fail-closed or approved review/local-only routes. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Inline allow/mask/block/review at every model/tool/context/workers/telemetry boundary, authenticated encrypted mapping and least-privilege restoration. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify successful integration with canonical contract fixtures. Inject malformed input, cancellation and unavailable dependency; assert typed error/degraded behavior.
5. **Acceptance artifact:** Synthetic canary exfiltration, missing required detector, cross-scope restoration and model-output bypass. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-PRIVACY-012` only after verified pass.

#### OC2-D-PRIVACY-013 — Tokenize with cryptographically random opaque placeholders and encrypted per-tenant/principal/session/turn mappings.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Privacy/, workers/security/, tests/Privacy/` or the existing equivalent; record the package/adapter owner. Check `S03,S04,S06` gate and the current source API/contract version.
2. **Concrete implementation:** Tokenize with cryptographically random opaque placeholders and encrypted per-tenant/principal/session/turn mappings. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Inline allow/mask/block/review at every model/tool/context/workers/telemetry boundary, authenticated encrypted mapping and least-privilege restoration. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Exercise authorized and explicitly unauthorized principals/tenants; assert no side effect on denial. Verify no credentials, plaintext or raw user content leave the intended trust boundary.
5. **Acceptance artifact:** Synthetic canary exfiltration, missing required detector, cross-scope restoration and model-output bypass. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-PRIVACY-013` only after verified pass.

#### OC2-D-PRIVACY-014 — Implement persistent protected local KEK/DEK envelope AES-GCM vault and optional key wrapping interface without plaintext database columns.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Privacy/, workers/security/, tests/Privacy/` or the existing equivalent; record the package/adapter owner. Check `S03,S04,S06` gate and the current source API/contract version.
2. **Concrete implementation:** Implement persistent protected local KEK/DEK envelope AES-GCM vault and optional key wrapping interface without plaintext database columns. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Inline allow/mask/block/review at every model/tool/context/workers/telemetry boundary, authenticated encrypted mapping and least-privilege restoration. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Prove commit/rollback behavior against an actual supported relational provider. Inject lost ACK, stale fencing token and concurrent writer where the contract touches durable state.
5. **Acceptance artifact:** Synthetic canary exfiltration, missing required detector, cross-scope restoration and model-output bypass. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-PRIVACY-014` only after verified pass.

#### OC2-D-PRIVACY-015 — Implement authorized path-specific restoration for final user output and separately trusted tool argument fields.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Privacy/, workers/security/, tests/Privacy/` or the existing equivalent; record the package/adapter owner. Check `S03,S04,S06` gate and the current source API/contract version.
2. **Concrete implementation:** Implement authorized path-specific restoration for final user output and separately trusted tool argument fields. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Inline allow/mask/block/review at every model/tool/context/workers/telemetry boundary, authenticated encrypted mapping and least-privilege restoration. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Run targeted success and deliberate negative regression in CI. Save machine-readable test artifacts and versioned audit evidence.
5. **Acceptance artifact:** Synthetic canary exfiltration, missing required detector, cross-scope restoration and model-output bypass. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-PRIVACY-015` only after verified pass.

#### OC2-D-PRIVACY-016 — Implement streaming placeholder split buffering with every split-point property tests and no partial secret emission.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Privacy/, workers/security/, tests/Privacy/` or the existing equivalent; record the package/adapter owner. Check `S03,S04,S06` gate and the current source API/contract version.
2. **Concrete implementation:** Implement streaming placeholder split buffering with every split-point property tests and no partial secret emission. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Inline allow/mask/block/review at every model/tool/context/workers/telemetry boundary, authenticated encrypted mapping and least-privilege restoration. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Run targeted success and deliberate negative regression in CI. Save machine-readable test artifacts and versioned audit evidence.
5. **Acceptance artifact:** Synthetic canary exfiltration, missing required detector, cross-scope restoration and model-output bypass. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-PRIVACY-016` only after verified pass.

#### OC2-D-PRIVACY-017 — Scan outgoing model/tool response and tool result content independently, including literal sensitive values not just known placeholders.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Privacy/, workers/security/, tests/Privacy/` or the existing equivalent; record the package/adapter owner. Check `S03,S04,S06` gate and the current source API/contract version.
2. **Concrete implementation:** Scan outgoing model/tool response and tool result content independently, including literal sensitive values not just known placeholders. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Inline allow/mask/block/review at every model/tool/context/workers/telemetry boundary, authenticated encrypted mapping and least-privilege restoration. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Run targeted success and deliberate negative regression in CI. Save machine-readable test artifacts and versioned audit evidence.
5. **Acceptance artifact:** Synthetic canary exfiltration, missing required detector, cross-scope restoration and model-output bypass. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-PRIVACY-017` only after verified pass.

#### OC2-D-PRIVACY-018 — Gate images, documents, EXIF, hidden PDF text and audio modality egress; never claim transcript-only redaction protects remote raw audio.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Privacy/, workers/security/, tests/Privacy/` or the existing equivalent; record the package/adapter owner. Check `S03,S04,S06` gate and the current source API/contract version.
2. **Concrete implementation:** Gate images, documents, EXIF, hidden PDF text and audio modality egress; never claim transcript-only redaction protects remote raw audio. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Inline allow/mask/block/review at every model/tool/context/workers/telemetry boundary, authenticated encrypted mapping and least-privilege restoration. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify disabled/uninstalled, ready, startup failure and shutdown behavior independently. Validate model/asset manifest hash, license, actual capabilities and bounded resources where applicable. Check sequence gaps, reconnect epochs and speech/privacy transitions without duplicate accepted turns.
5. **Acceptance artifact:** Synthetic canary exfiltration, missing required detector, cross-scope restoration and model-output bypass. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-PRIVACY-018` only after verified pass.

#### OC2-D-PRIVACY-019 — Prohibit raw prompts, secrets, token maps, sensitive tool args, audio and private checkpoints in NATS and telemetry.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Privacy/, workers/security/, tests/Privacy/` or the existing equivalent; record the package/adapter owner. Check `S03,S04,S06` gate and the current source API/contract version.
2. **Concrete implementation:** Prohibit raw prompts, secrets, token maps, sensitive tool args, audio and private checkpoints in NATS and telemetry. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Inline allow/mask/block/review at every model/tool/context/workers/telemetry boundary, authenticated encrypted mapping and least-privilege restoration. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Exercise authorized and explicitly unauthorized principals/tenants; assert no side effect on denial. Verify no credentials, plaintext or raw user content leave the intended trust boundary. Check sequence gaps, reconnect epochs and speech/privacy transitions without duplicate accepted turns.
5. **Acceptance artifact:** Synthetic canary exfiltration, missing required detector, cross-scope restoration and model-output bypass. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-PRIVACY-019` only after verified pass.

#### OC2-D-PRIVACY-020 — Run malicious prompt/MCP/skill/memory/child context test fixtures against all policy boundaries and verify no unsafe egress.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Privacy/, workers/security/, tests/Privacy/` or the existing equivalent; record the package/adapter owner. Check `S03,S04,S06` gate and the current source API/contract version.
2. **Concrete implementation:** Run malicious prompt/MCP/skill/memory/child context test fixtures against all policy boundaries and verify no unsafe egress. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Inline allow/mask/block/review at every model/tool/context/workers/telemetry boundary, authenticated encrypted mapping and least-privilege restoration. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Invoke an actual pinned MAF type and inspect real lifecycle/session data. Prove unsupported versions/profiles report unavailable instead of fabricating equivalent behavior. Verify unregistered/malicious MCP tool schemas cannot expand permissions, redirect network access or bypass approval.
5. **Acceptance artifact:** Synthetic canary exfiltration, missing required detector, cross-scope restoration and model-output bypass. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-PRIVACY-020` only after verified pass.

#### OC2-D-PRIVACY-021 — Verify privacy-disabled trusted-host option emits warning but leaves all auth, tenant and tool controls enforced.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Privacy/, workers/security/, tests/Privacy/` or the existing equivalent; record the package/adapter owner. Check `S03,S04,S06` gate and the current source API/contract version.
2. **Concrete implementation:** Verify privacy-disabled trusted-host option emits warning but leaves all auth, tenant and tool controls enforced. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Inline allow/mask/block/review at every model/tool/context/workers/telemetry boundary, authenticated encrypted mapping and least-privilege restoration. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Exercise authorized and explicitly unauthorized principals/tenants; assert no side effect on denial. Verify no credentials, plaintext or raw user content leave the intended trust boundary.
5. **Acceptance artifact:** Synthetic canary exfiltration, missing required detector, cross-scope restoration and model-output bypass. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-PRIVACY-021` only after verified pass.

#### OC2-D-PRIVACY-022 — Verify security-worker crash, stale readiness, wrong checkpoint hash, unsupported language and timeout block only policy-required paths.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Privacy/, workers/security/, tests/Privacy/` or the existing equivalent; record the package/adapter owner. Check `S03,S04,S06` gate and the current source API/contract version.
2. **Concrete implementation:** Verify security-worker crash, stale readiness, wrong checkpoint hash, unsupported language and timeout block only policy-required paths. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Inline allow/mask/block/review at every model/tool/context/workers/telemetry boundary, authenticated encrypted mapping and least-privilege restoration. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Exercise authorized and explicitly unauthorized principals/tenants; assert no side effect on denial. Verify no credentials, plaintext or raw user content leave the intended trust boundary.
5. **Acceptance artifact:** Synthetic canary exfiltration, missing required detector, cross-scope restoration and model-output bypass. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-PRIVACY-022` only after verified pass.

#### OC2-D-PRIVACY-023 — Create independent licensing/benchmark report with FP/FN, leakage/overmask, p50/p95 latency, models/languages/hardware.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Privacy/, workers/security/, tests/Privacy/` or the existing equivalent; record the package/adapter owner. Check `S03,S04,S06` gate and the current source API/contract version.
2. **Concrete implementation:** Create independent licensing/benchmark report with FP/FN, leakage/overmask, p50/p95 latency, models/languages/hardware. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Inline allow/mask/block/review at every model/tool/context/workers/telemetry boundary, authenticated encrypted mapping and least-privilege restoration. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Run targeted success and deliberate negative regression in CI. Save machine-readable test artifacts and versioned audit evidence.
5. **Acceptance artifact:** Synthetic canary exfiltration, missing required detector, cross-scope restoration and model-output bypass. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-PRIVACY-023` only after verified pass.


### S10 — Committed transport and public HTTP/WS/AG-UI

**Prerequisites:** `S06,S08,S09`. **Checklist section:** 3.11 NATS JetStream, committed subscriptions, HTTP/WS, AG-UI. **Scope:** 16 atomic deliverables. **Primary source paths:** `src/OpenConnect.Transport.Nats/, src/OpenConnect.Hosting.AspNetCore/, contracts/openapi/`.

**Authority:** Publish sanitized committed event notices after DB transaction; scope journal replay and client auth separately; never expose NATS to browsers. **Required verification pattern:** Broker restart/redelivery, cursor expiry, WS auth and optional AG-UI interop.

#### OC2-D-EVENTS-001 — Define canonical versioned OpenAPI routes, error ProblemDetails codes and authenticated tenant/actor derivation.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Transport.Nats/, src/OpenConnect.Hosting.AspNetCore/, contracts/openapi/` or the existing equivalent; record the package/adapter owner. Check `S06,S08,S09` gate and the current source API/contract version.
2. **Concrete implementation:** Define canonical versioned OpenAPI routes, error ProblemDetails codes and authenticated tenant/actor derivation. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Publish sanitized committed event notices after DB transaction; scope journal replay and client auth separately; never expose NATS to browsers. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Exercise authorized and explicitly unauthorized principals/tenants; assert no side effect on denial. Verify no credentials, plaintext or raw user content leave the intended trust boundary.
5. **Acceptance artifact:** Broker restart/redelivery, cursor expiry, WS auth and optional AG-UI interop. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-EVENTS-001` only after verified pass.

#### OC2-D-EVENTS-002 — Implement POST sessions/tasks with idempotency and durable 202 semantics only after relational commit.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Transport.Nats/, src/OpenConnect.Hosting.AspNetCore/, contracts/openapi/` or the existing equivalent; record the package/adapter owner. Check `S06,S08,S09` gate and the current source API/contract version.
2. **Concrete implementation:** Implement POST sessions/tasks with idempotency and durable 202 semantics only after relational commit. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Publish sanitized committed event notices after DB transaction; scope journal replay and client auth separately; never expose NATS to browsers. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Invoke an actual pinned MAF type and inspect real lifecycle/session data. Prove unsupported versions/profiles report unavailable instead of fabricating equivalent behavior.
5. **Acceptance artifact:** Broker restart/redelivery, cursor expiry, WS auth and optional AG-UI interop. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-EVENTS-002` only after verified pass.

#### OC2-D-EVENTS-003 — Implement GET task/session and bounded per-session replay with monotonic database cursor and replay-window expiry resnapshot.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Transport.Nats/, src/OpenConnect.Hosting.AspNetCore/, contracts/openapi/` or the existing equivalent; record the package/adapter owner. Check `S06,S08,S09` gate and the current source API/contract version.
2. **Concrete implementation:** Implement GET task/session and bounded per-session replay with monotonic database cursor and replay-window expiry resnapshot. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Publish sanitized committed event notices after DB transaction; scope journal replay and client auth separately; never expose NATS to browsers. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Prove commit/rollback behavior against an actual supported relational provider. Inject lost ACK, stale fencing token and concurrent writer where the contract touches durable state.
5. **Acceptance artifact:** Broker restart/redelivery, cursor expiry, WS auth and optional AG-UI interop. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-EVENTS-003` only after verified pass.

#### OC2-D-EVENTS-004 — Implement authenticated WebSocket subscribe/reconnect, origin verification, bounded backpressure and revocation-aware auth refresh.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Transport.Nats/, src/OpenConnect.Hosting.AspNetCore/, contracts/openapi/` or the existing equivalent; record the package/adapter owner. Check `S06,S08,S09` gate and the current source API/contract version.
2. **Concrete implementation:** Implement authenticated WebSocket subscribe/reconnect, origin verification, bounded backpressure and revocation-aware auth refresh. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Publish sanitized committed event notices after DB transaction; scope journal replay and client auth separately; never expose NATS to browsers. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Exercise authorized and explicitly unauthorized principals/tenants; assert no side effect on denial. Verify no credentials, plaintext or raw user content leave the intended trust boundary.
5. **Acceptance artifact:** Broker restart/redelivery, cursor expiry, WS auth and optional AG-UI interop. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-EVENTS-004` only after verified pass.

#### OC2-D-EVENTS-005 — Implement safe event schemas for task/agent/harness plan/todo/workflow/tool/approval/progress/voice and extension payloads.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Transport.Nats/, src/OpenConnect.Hosting.AspNetCore/, contracts/openapi/` or the existing equivalent; record the package/adapter owner. Check `S06,S08,S09` gate and the current source API/contract version.
2. **Concrete implementation:** Implement safe event schemas for task/agent/harness plan/todo/workflow/tool/approval/progress/voice and extension payloads. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Publish sanitized committed event notices after DB transaction; scope journal replay and client auth separately; never expose NATS to browsers. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Exercise authorized and explicitly unauthorized principals/tenants; assert no side effect on denial. Verify no credentials, plaintext or raw user content leave the intended trust boundary.
5. **Acceptance artifact:** Broker restart/redelivery, cursor expiry, WS auth and optional AG-UI interop. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-EVENTS-005` only after verified pass.

#### OC2-D-EVENTS-006 — Distinguish committed authoritative lifecycle events from non-durable high-frequency streaming deltas.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Transport.Nats/, src/OpenConnect.Hosting.AspNetCore/, contracts/openapi/` or the existing equivalent; record the package/adapter owner. Check `S06,S08,S09` gate and the current source API/contract version.
2. **Concrete implementation:** Distinguish committed authoritative lifecycle events from non-durable high-frequency streaming deltas. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Publish sanitized committed event notices after DB transaction; scope journal replay and client auth separately; never expose NATS to browsers. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify successful integration with canonical contract fixtures. Inject malformed input, cancellation and unavailable dependency; assert typed error/degraded behavior.
5. **Acceptance artifact:** Broker restart/redelivery, cursor expiry, WS auth and optional AG-UI interop. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-EVENTS-006` only after verified pass.

#### OC2-D-EVENTS-007 — Map native MAF events into OpenConnect client projections without fabricated execution trees or fake agent steps.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Transport.Nats/, src/OpenConnect.Hosting.AspNetCore/, contracts/openapi/` or the existing equivalent; record the package/adapter owner. Check `S06,S08,S09` gate and the current source API/contract version.
2. **Concrete implementation:** Map native MAF events into OpenConnect client projections without fabricated execution trees or fake agent steps. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Publish sanitized committed event notices after DB transaction; scope journal replay and client auth separately; never expose NATS to browsers. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Invoke an actual pinned MAF type and inspect real lifecycle/session data. Prove unsupported versions/profiles report unavailable instead of fabricating equivalent behavior.
5. **Acceptance artifact:** Broker restart/redelivery, cursor expiry, WS auth and optional AG-UI interop. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-EVENTS-007` only after verified pass.

#### OC2-D-EVENTS-008 — Persist sanitized notices in transactional outbox and deliver to NATS only after domain commit.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Transport.Nats/, src/OpenConnect.Hosting.AspNetCore/, contracts/openapi/` or the existing equivalent; record the package/adapter owner. Check `S06,S08,S09` gate and the current source API/contract version.
2. **Concrete implementation:** Persist sanitized notices in transactional outbox and deliver to NATS only after domain commit. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Publish sanitized committed event notices after DB transaction; scope journal replay and client auth separately; never expose NATS to browsers. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Prove commit/rollback behavior against an actual supported relational provider. Inject lost ACK, stale fencing token and concurrent writer where the contract touches durable state.
5. **Acceptance artifact:** Broker restart/redelivery, cursor expiry, WS auth and optional AG-UI interop. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-EVENTS-008` only after verified pass.

#### OC2-D-EVENTS-009 — Use NATS-Msg-Id and ACK-after-durable-consumer processing; test duplicate publish, consumer crash and broker outage.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Transport.Nats/, src/OpenConnect.Hosting.AspNetCore/, contracts/openapi/` or the existing equivalent; record the package/adapter owner. Check `S06,S08,S09` gate and the current source API/contract version.
2. **Concrete implementation:** Use NATS-Msg-Id and ACK-after-durable-consumer processing; test duplicate publish, consumer crash and broker outage. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Publish sanitized committed event notices after DB transaction; scope journal replay and client auth separately; never expose NATS to browsers. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Run targeted success and deliberate negative regression in CI. Save machine-readable test artifacts and versioned audit evidence.
5. **Acceptance artifact:** Broker restart/redelivery, cursor expiry, WS auth and optional AG-UI interop. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-EVENTS-009` only after verified pass.

#### OC2-D-EVENTS-010 — Keep IEventJournal replay scoped to known SessionId; expose distinct approved committed-event subscription abstraction.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Transport.Nats/, src/OpenConnect.Hosting.AspNetCore/, contracts/openapi/` or the existing equivalent; record the package/adapter owner. Check `S06,S08,S09` gate and the current source API/contract version.
2. **Concrete implementation:** Keep IEventJournal replay scoped to known SessionId; expose distinct approved committed-event subscription abstraction. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Publish sanitized committed event notices after DB transaction; scope journal replay and client auth separately; never expose NATS to browsers. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Exercise authorized and explicitly unauthorized principals/tenants; assert no side effect on denial. Verify no credentials, plaintext or raw user content leave the intended trust boundary.
5. **Acceptance artifact:** Broker restart/redelivery, cursor expiry, WS auth and optional AG-UI interop. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-EVENTS-010` only after verified pass.

#### OC2-D-EVENTS-011 — Implement per-consumer role-limited NATS subjects; workers cannot create/delete streams or see unrelated tenants.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Transport.Nats/, src/OpenConnect.Hosting.AspNetCore/, contracts/openapi/` or the existing equivalent; record the package/adapter owner. Check `S06,S08,S09` gate and the current source API/contract version.
2. **Concrete implementation:** Implement per-consumer role-limited NATS subjects; workers cannot create/delete streams or see unrelated tenants. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Publish sanitized committed event notices after DB transaction; scope journal replay and client auth separately; never expose NATS to browsers. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Exercise authorized and explicitly unauthorized principals/tenants; assert no side effect on denial. Verify no credentials, plaintext or raw user content leave the intended trust boundary.
5. **Acceptance artifact:** Broker restart/redelivery, cursor expiry, WS auth and optional AG-UI interop. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-EVENTS-011` only after verified pass.

#### OC2-D-EVENTS-012 — Use authenticated short-lived payload reference fetch for worker-private original content, not NATS body or URL query.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Transport.Nats/, src/OpenConnect.Hosting.AspNetCore/, contracts/openapi/` or the existing equivalent; record the package/adapter owner. Check `S06,S08,S09` gate and the current source API/contract version.
2. **Concrete implementation:** Use authenticated short-lived payload reference fetch for worker-private original content, not NATS body or URL query. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Publish sanitized committed event notices after DB transaction; scope journal replay and client auth separately; never expose NATS to browsers. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Exercise authorized and explicitly unauthorized principals/tenants; assert no side effect on denial. Verify no credentials, plaintext or raw user content leave the intended trust boundary.
5. **Acceptance artifact:** Broker restart/redelivery, cursor expiry, WS auth and optional AG-UI interop. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-EVENTS-012` only after verified pass.

#### OC2-D-EVENTS-013 — Implement optional AG-UI endpoint using MAF adapter when installed; preserve independent OpenConnect task API authority.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Transport.Nats/, src/OpenConnect.Hosting.AspNetCore/, contracts/openapi/` or the existing equivalent; record the package/adapter owner. Check `S06,S08,S09` gate and the current source API/contract version.
2. **Concrete implementation:** Implement optional AG-UI endpoint using MAF adapter when installed; preserve independent OpenConnect task API authority. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Publish sanitized committed event notices after DB transaction; scope journal replay and client auth separately; never expose NATS to browsers. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Invoke an actual pinned MAF type and inspect real lifecycle/session data. Prove unsupported versions/profiles report unavailable instead of fabricating equivalent behavior.
5. **Acceptance artifact:** Broker restart/redelivery, cursor expiry, WS auth and optional AG-UI interop. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-EVENTS-013` only after verified pass.

#### OC2-D-EVENTS-014 — Verify AG-UI/tool approval/session/authorization/cancellation parity with OpenConnect HTTP/WS behavior.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Transport.Nats/, src/OpenConnect.Hosting.AspNetCore/, contracts/openapi/` or the existing equivalent; record the package/adapter owner. Check `S06,S08,S09` gate and the current source API/contract version.
2. **Concrete implementation:** Verify AG-UI/tool approval/session/authorization/cancellation parity with OpenConnect HTTP/WS behavior. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Publish sanitized committed event notices after DB transaction; scope journal replay and client auth separately; never expose NATS to browsers. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Exercise authorized and explicitly unauthorized principals/tenants; assert no side effect on denial. Verify no credentials, plaintext or raw user content leave the intended trust boundary.
5. **Acceptance artifact:** Broker restart/redelivery, cursor expiry, WS auth and optional AG-UI interop. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-EVENTS-014` only after verified pass.

#### OC2-D-EVENTS-015 — Do not require NATS or AG-UI when only basic Direct agent and in-process execution are configured.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Transport.Nats/, src/OpenConnect.Hosting.AspNetCore/, contracts/openapi/` or the existing equivalent; record the package/adapter owner. Check `S06,S08,S09` gate and the current source API/contract version.
2. **Concrete implementation:** Do not require NATS or AG-UI when only basic Direct agent and in-process execution are configured. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Publish sanitized committed event notices after DB transaction; scope journal replay and client auth separately; never expose NATS to browsers. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Invoke an actual pinned MAF type and inspect real lifecycle/session data. Prove unsupported versions/profiles report unavailable instead of fabricating equivalent behavior.
5. **Acceptance artifact:** Broker restart/redelivery, cursor expiry, WS auth and optional AG-UI interop. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-EVENTS-015` only after verified pass.

#### OC2-D-EVENTS-016 — Document safe NATS subject/version migration, retention and loss/rebuild of ephemeral client events.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Transport.Nats/, src/OpenConnect.Hosting.AspNetCore/, contracts/openapi/` or the existing equivalent; record the package/adapter owner. Check `S06,S08,S09` gate and the current source API/contract version.
2. **Concrete implementation:** Document safe NATS subject/version migration, retention and loss/rebuild of ephemeral client events. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Publish sanitized committed event notices after DB transaction; scope journal replay and client auth separately; never expose NATS to browsers. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Prove commit/rollback behavior against an actual supported relational provider. Inject lost ACK, stale fencing token and concurrent writer where the contract touches durable state.
5. **Acceptance artifact:** Broker restart/redelivery, cursor expiry, WS auth and optional AG-UI interop. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-EVENTS-016` only after verified pass.


### S11 — Blazor/TS/Node clients and MiniComposer

**Prerequisites:** `S10`. **Checklist section:** 3.12 C# / Blazor / TypeScript / Node client parity and compact composer. **Scope:** 21 atomic deliverables. **Primary source paths:** `src/OpenConnect.Client/, src/OpenConnect.Components.Blazor/, packages/client-ts/`.

**Authority:** Consume one versioned task/event API, maintain session scope, show actual status and safe local renderer, preserve SSR/Blazor lifetimes. **Required verification pattern:** bUnit/Playwright, keyboard/IME, reconnect, XSS, CSS isolation and no-speech fallback.

#### OC2-D-CLIENT-001 — Implement net8/9/10 IOpenConnectClient with typed authenticated task/session/cancel/capability/subscription APIs.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Client/, src/OpenConnect.Components.Blazor/, packages/client-ts/` or the existing equivalent; record the package/adapter owner. Check `S10` gate and the current source API/contract version.
2. **Concrete implementation:** Implement net8/9/10 IOpenConnectClient with typed authenticated task/session/cancel/capability/subscription APIs. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Consume one versioned task/event API, maintain session scope, show actual status and safe local renderer, preserve SSR/Blazor lifetimes. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Exercise authorized and explicitly unauthorized principals/tenants; assert no side effect on denial. Verify no credentials, plaintext or raw user content leave the intended trust boundary. Build and run matching TFM consumers on net8.0/net9.0/net10.0 as claimed, with EOS compatibility labels.
5. **Acceptance artifact:** bUnit/Playwright, keyboard/IME, reconnect, XSS, CSS isolation and no-speech fallback. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-CLIENT-001` only after verified pass.

#### OC2-D-CLIENT-002 — Implement browser-and-Node @openconnect/client with typed OpenAPI DTOs, AbortSignal, WebSocket reconnect and SSR-safe import.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Client/, src/OpenConnect.Components.Blazor/, packages/client-ts/` or the existing equivalent; record the package/adapter owner. Check `S10` gate and the current source API/contract version.
2. **Concrete implementation:** Implement browser-and-Node @openconnect/client with typed OpenAPI DTOs, AbortSignal, WebSocket reconnect and SSR-safe import. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Consume one versioned task/event API, maintain session scope, show actual status and safe local renderer, preserve SSR/Blazor lifetimes. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify successful integration with canonical contract fixtures. Inject malformed input, cancellation and unavailable dependency; assert typed error/degraded behavior.
5. **Acceptance artifact:** bUnit/Playwright, keyboard/IME, reconnect, XSS, CSS isolation and no-speech fallback. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-CLIENT-002` only after verified pass.

#### OC2-D-CLIENT-003 — Implement shared client event dedup, cursor replay/resnapshot and auth switch/logout clearing of scoped projections.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Client/, src/OpenConnect.Components.Blazor/, packages/client-ts/` or the existing equivalent; record the package/adapter owner. Check `S10` gate and the current source API/contract version.
2. **Concrete implementation:** Implement shared client event dedup, cursor replay/resnapshot and auth switch/logout clearing of scoped projections. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Consume one versioned task/event API, maintain session scope, show actual status and safe local renderer, preserve SSR/Blazor lifetimes. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Exercise authorized and explicitly unauthorized principals/tenants; assert no side effect on denial. Verify no credentials, plaintext or raw user content leave the intended trust boundary.
5. **Acceptance artifact:** bUnit/Playwright, keyboard/IME, reconnect, XSS, CSS isolation and no-speech fallback. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-CLIENT-003` only after verified pass.

#### OC2-D-CLIENT-004 — Implement Blazor OpenConnectProvider with correct cascading child content and no duplicate MudBlazor providers.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Client/, src/OpenConnect.Components.Blazor/, packages/client-ts/` or the existing equivalent; record the package/adapter owner. Check `S10` gate and the current source API/contract version.
2. **Concrete implementation:** Implement Blazor OpenConnectProvider with correct cascading child content and no duplicate MudBlazor providers. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Consume one versioned task/event API, maintain session scope, show actual status and safe local renderer, preserve SSR/Blazor lifetimes. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify successful integration with canonical contract fixtures. Inject malformed input, cancellation and unavailable dependency; assert typed error/degraded behavior.
5. **Acceptance artifact:** bUnit/Playwright, keyboard/IME, reconnect, XSS, CSS isolation and no-speech fallback. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-CLIENT-004` only after verified pass.

#### OC2-D-CLIENT-005 — Implement default MiniComposer bottom-right 44–56px FAB, no document reflow or unsolicited popups.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Client/, src/OpenConnect.Components.Blazor/, packages/client-ts/` or the existing equivalent; record the package/adapter owner. Check `S10` gate and the current source API/contract version.
2. **Concrete implementation:** Implement default MiniComposer bottom-right 44–56px FAB, no document reflow or unsolicited popups. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Consume one versioned task/event API, maintain session scope, show actual status and safe local renderer, preserve SSR/Blazor lifetimes. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Validate generated/readme/code snippet claims against built assemblies or referenced source. Detect references to superseded APIs, stale paths and unsupported capabilities.
5. **Acceptance artifact:** bUnit/Playwright, keyboard/IME, reconnect, XSS, CSS isolation and no-speech fallback. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-CLIENT-005` only after verified pass.

#### OC2-D-CLIENT-006 — Implement compact 23.5–24rem responsive anchored bubble, subtle tail, drag handle, settings, stop and close.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Client/, src/OpenConnect.Components.Blazor/, packages/client-ts/` or the existing equivalent; record the package/adapter owner. Check `S10` gate and the current source API/contract version.
2. **Concrete implementation:** Implement compact 23.5–24rem responsive anchored bubble, subtle tail, drag handle, settings, stop and close. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Consume one versioned task/event API, maintain session scope, show actual status and safe local renderer, preserve SSR/Blazor lifetimes. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify successful integration with canonical contract fixtures. Inject malformed input, cancellation and unavailable dependency; assert typed error/degraded behavior.
5. **Acceptance artifact:** bUnit/Playwright, keyboard/IME, reconnect, XSS, CSS isolation and no-speech fallback. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-CLIENT-006` only after verified pass.

#### OC2-D-CLIENT-007 — Implement pointer/touch/keyboard drag constrained to viewport; arrows 24px and Home reset, mobile keyboard/safe-area zoom handling.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Client/, src/OpenConnect.Components.Blazor/, packages/client-ts/` or the existing equivalent; record the package/adapter owner. Check `S10` gate and the current source API/contract version.
2. **Concrete implementation:** Implement pointer/touch/keyboard drag constrained to viewport; arrows 24px and Home reset, mobile keyboard/safe-area zoom handling. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Consume one versioned task/event API, maintain session scope, show actual status and safe local renderer, preserve SSR/Blazor lifetimes. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify successful integration with canonical contract fixtures. Inject malformed input, cancellation and unavailable dependency; assert typed error/degraded behavior.
5. **Acceptance artifact:** bUnit/Playwright, keyboard/IME, reconnect, XSS, CSS isolation and no-speech fallback. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-CLIENT-007` only after verified pass.

#### OC2-D-CLIENT-008 — Implement textarea initial 1 row/max5/8000 chars and Enter/ShiftEnter/IME/double submit safe task admission.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Client/, src/OpenConnect.Components.Blazor/, packages/client-ts/` or the existing equivalent; record the package/adapter owner. Check `S10` gate and the current source API/contract version.
2. **Concrete implementation:** Implement textarea initial 1 row/max5/8000 chars and Enter/ShiftEnter/IME/double submit safe task admission. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Consume one versioned task/event API, maintain session scope, show actual status and safe local renderer, preserve SSR/Blazor lifetimes. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify successful integration with canonical contract fixtures. Inject malformed input, cancellation and unavailable dependency; assert typed error/degraded behavior.
5. **Acceptance artifact:** bUnit/Playwright, keyboard/IME, reconnect, XSS, CSS isolation and no-speech fallback. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-CLIENT-008` only after verified pass.

#### OC2-D-CLIENT-009 — Implement distinct Stop/Cancel versus Close/Minimize; close must not silently cancel durable run.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Client/, src/OpenConnect.Components.Blazor/, packages/client-ts/` or the existing equivalent; record the package/adapter owner. Check `S10` gate and the current source API/contract version.
2. **Concrete implementation:** Implement distinct Stop/Cancel versus Close/Minimize; close must not silently cancel durable run. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Consume one versioned task/event API, maintain session scope, show actual status and safe local renderer, preserve SSR/Blazor lifetimes. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify successful integration with canonical contract fixtures. Inject malformed input, cancellation and unavailable dependency; assert typed error/degraded behavior.
5. **Acceptance artifact:** bUnit/Playwright, keyboard/IME, reconnect, XSS, CSS isolation and no-speech fallback. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-CLIENT-009` only after verified pass.

#### OC2-D-CLIENT-010 — Implement bounded latest user/assistant/partial transcript snippets and real streaming output status.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Client/, src/OpenConnect.Components.Blazor/, packages/client-ts/` or the existing equivalent; record the package/adapter owner. Check `S10` gate and the current source API/contract version.
2. **Concrete implementation:** Implement bounded latest user/assistant/partial transcript snippets and real streaming output status. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Consume one versioned task/event API, maintain session scope, show actual status and safe local renderer, preserve SSR/Blazor lifetimes. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify disabled/uninstalled, ready, startup failure and shutdown behavior independently. Validate model/asset manifest hash, license, actual capabilities and bounded resources where applicable.
5. **Acceptance artifact:** bUnit/Playwright, keyboard/IME, reconnect, XSS, CSS isolation and no-speech fallback. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-CLIENT-010` only after verified pass.

#### OC2-D-CLIENT-011 — Implement concise Harness native plan/todo/Workflow execution status without inventing progress or exposing private reasoning.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Client/, src/OpenConnect.Components.Blazor/, packages/client-ts/` or the existing equivalent; record the package/adapter owner. Check `S10` gate and the current source API/contract version.
2. **Concrete implementation:** Implement concise Harness native plan/todo/Workflow execution status without inventing progress or exposing private reasoning. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Consume one versioned task/event API, maintain session scope, show actual status and safe local renderer, preserve SSR/Blazor lifetimes. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Invoke an actual pinned MAF type and inspect real lifecycle/session data. Prove unsupported versions/profiles report unavailable instead of fabricating equivalent behavior.
5. **Acceptance artifact:** bUnit/Playwright, keyboard/IME, reconnect, XSS, CSS isolation and no-speech fallback. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-CLIENT-011` only after verified pass.

#### OC2-D-CLIENT-012 — Implement long-running tool ACK/heartbeat display with accessible non-spam coalescing and approval state.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Client/, src/OpenConnect.Components.Blazor/, packages/client-ts/` or the existing equivalent; record the package/adapter owner. Check `S10` gate and the current source API/contract version.
2. **Concrete implementation:** Implement long-running tool ACK/heartbeat display with accessible non-spam coalescing and approval state. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Consume one versioned task/event API, maintain session scope, show actual status and safe local renderer, preserve SSR/Blazor lifetimes. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Exercise authorized and explicitly unauthorized principals/tenants; assert no side effect on denial. Verify no credentials, plaintext or raw user content leave the intended trust boundary.
5. **Acceptance artifact:** bUnit/Playwright, keyboard/IME, reconnect, XSS, CSS isolation and no-speech fallback. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-CLIENT-012` only after verified pass.

#### OC2-D-CLIENT-013 — Implement independent voice output and microphone controls, conditional availability and browser permission only from user gesture.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Client/, src/OpenConnect.Components.Blazor/, packages/client-ts/` or the existing equivalent; record the package/adapter owner. Check `S10` gate and the current source API/contract version.
2. **Concrete implementation:** Implement independent voice output and microphone controls, conditional availability and browser permission only from user gesture. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Consume one versioned task/event API, maintain session scope, show actual status and safe local renderer, preserve SSR/Blazor lifetimes. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify disabled/uninstalled, ready, startup failure and shutdown behavior independently. Validate model/asset manifest hash, license, actual capabilities and bounded resources where applicable.
5. **Acceptance artifact:** bUnit/Playwright, keyboard/IME, reconnect, XSS, CSS isolation and no-speech fallback. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-CLIENT-013` only after verified pass.

#### OC2-D-CLIENT-014 — Implement responsive light/dark/RTL/high-contrast/reduced-motion/a11y controls and accessible 44px touch hit regions.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Client/, src/OpenConnect.Components.Blazor/, packages/client-ts/` or the existing equivalent; record the package/adapter owner. Check `S10` gate and the current source API/contract version.
2. **Concrete implementation:** Implement responsive light/dark/RTL/high-contrast/reduced-motion/a11y controls and accessible 44px touch hit regions. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Consume one versioned task/event API, maintain session scope, show actual status and safe local renderer, preserve SSR/Blazor lifetimes. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify successful integration with canonical contract fixtures. Inject malformed input, cancellation and unavailable dependency; assert typed error/degraded behavior.
5. **Acceptance artifact:** bUnit/Playwright, keyboard/IME, reconnect, XSS, CSS isolation and no-speech fallback. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-CLIENT-014` only after verified pass.

#### OC2-D-CLIENT-015 — Implement optional component metadata (IOpenConnectComponent descriptor), schemas, capability maps and server-side filtering.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Client/, src/OpenConnect.Components.Blazor/, packages/client-ts/` or the existing equivalent; record the package/adapter owner. Check `S10` gate and the current source API/contract version.
2. **Concrete implementation:** Implement optional component metadata (IOpenConnectComponent descriptor), schemas, capability maps and server-side filtering. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Consume one versioned task/event API, maintain session scope, show actual status and safe local renderer, preserve SSR/Blazor lifetimes. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify successful integration with canonical contract fixtures. Inject malformed input, cancellation and unavailable dependency; assert typed error/degraded behavior.
5. **Acceptance artifact:** bUnit/Playwright, keyboard/IME, reconnect, XSS, CSS isolation and no-speech fallback. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-CLIENT-015` only after verified pass.

#### OC2-D-CLIENT-016 — Implement validated component proposal mapping to locally registered renderers only; deny arbitrary HTML/CSS/JS execution.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Client/, src/OpenConnect.Components.Blazor/, packages/client-ts/` or the existing equivalent; record the package/adapter owner. Check `S10` gate and the current source API/contract version.
2. **Concrete implementation:** Implement validated component proposal mapping to locally registered renderers only; deny arbitrary HTML/CSS/JS execution. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Consume one versioned task/event API, maintain session scope, show actual status and safe local renderer, preserve SSR/Blazor lifetimes. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Exercise authorized and explicitly unauthorized principals/tenants; assert no side effect on denial. Verify no credentials, plaintext or raw user content leave the intended trust boundary.
5. **Acceptance artifact:** bUnit/Playwright, keyboard/IME, reconnect, XSS, CSS isolation and no-speech fallback. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-CLIENT-016` only after verified pass.

#### OC2-D-CLIENT-017 — Implement framework-neutral browser composer package with stylesheet scope/ShadowRoot strategy and no global host CSS interference.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Client/, src/OpenConnect.Components.Blazor/, packages/client-ts/` or the existing equivalent; record the package/adapter owner. Check `S10` gate and the current source API/contract version.
2. **Concrete implementation:** Implement framework-neutral browser composer package with stylesheet scope/ShadowRoot strategy and no global host CSS interference. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Consume one versioned task/event API, maintain session scope, show actual status and safe local renderer, preserve SSR/Blazor lifetimes. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Exercise authorized and explicitly unauthorized principals/tenants; assert no side effect on denial. Verify no credentials, plaintext or raw user content leave the intended trust boundary.
5. **Acceptance artifact:** bUnit/Playwright, keyboard/IME, reconnect, XSS, CSS isolation and no-speech fallback. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-CLIENT-017` only after verified pass.

#### OC2-D-CLIENT-018 — Implement optional React wrapper sharing underlying client and state; no duplicate data model or session coordinator.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Client/, src/OpenConnect.Components.Blazor/, packages/client-ts/` or the existing equivalent; record the package/adapter owner. Check `S10` gate and the current source API/contract version.
2. **Concrete implementation:** Implement optional React wrapper sharing underlying client and state; no duplicate data model or session coordinator. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Consume one versioned task/event API, maintain session scope, show actual status and safe local renderer, preserve SSR/Blazor lifetimes. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Invoke an actual pinned MAF type and inspect real lifecycle/session data. Prove unsupported versions/profiles report unavailable instead of fabricating equivalent behavior.
5. **Acceptance artifact:** bUnit/Playwright, keyboard/IME, reconnect, XSS, CSS isolation and no-speech fallback. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-CLIENT-018` only after verified pass.

#### OC2-D-CLIENT-019 — Implement attached Navigator composer mode reusing same core editor state and hiding FAB/tail/drag.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Client/, src/OpenConnect.Components.Blazor/, packages/client-ts/` or the existing equivalent; record the package/adapter owner. Check `S10` gate and the current source API/contract version.
2. **Concrete implementation:** Implement attached Navigator composer mode reusing same core editor state and hiding FAB/tail/drag. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Consume one versioned task/event API, maintain session scope, show actual status and safe local renderer, preserve SSR/Blazor lifetimes. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify successful integration with canonical contract fixtures. Inject malformed input, cancellation and unavailable dependency; assert typed error/degraded behavior.
5. **Acceptance artifact:** bUnit/Playwright, keyboard/IME, reconnect, XSS, CSS isolation and no-speech fallback. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-CLIENT-019` only after verified pass.

#### OC2-D-CLIENT-020 — Run bUnit, Blazor Server/WASM, Playwright screenshot/visual parity, Node SSR and React StrictMode disposal tests.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Client/, src/OpenConnect.Components.Blazor/, packages/client-ts/` or the existing equivalent; record the package/adapter owner. Check `S10` gate and the current source API/contract version.
2. **Concrete implementation:** Run bUnit, Blazor Server/WASM, Playwright screenshot/visual parity, Node SSR and React StrictMode disposal tests. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Consume one versioned task/event API, maintain session scope, show actual status and safe local renderer, preserve SSR/Blazor lifetimes. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Run targeted success and deliberate negative regression in CI. Save machine-readable test artifacts and versioned audit evidence.
5. **Acceptance artifact:** bUnit/Playwright, keyboard/IME, reconnect, XSS, CSS isolation and no-speech fallback. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-CLIENT-020` only after verified pass.

#### OC2-D-CLIENT-021 — Test network loss, WS reconnection, auth expiry, two users, multiple renders, modal stacking and mobile viewport.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Client/, src/OpenConnect.Components.Blazor/, packages/client-ts/` or the existing equivalent; record the package/adapter owner. Check `S10` gate and the current source API/contract version.
2. **Concrete implementation:** Test network loss, WS reconnection, auth expiry, two users, multiple renders, modal stacking and mobile viewport. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Consume one versioned task/event API, maintain session scope, show actual status and safe local renderer, preserve SSR/Blazor lifetimes. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Run targeted success and deliberate negative regression in CI. Save machine-readable test artifacts and versioned audit evidence.
5. **Acceptance artifact:** bUnit/Playwright, keyboard/IME, reconnect, XSS, CSS isolation and no-speech fallback. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-CLIENT-021` only after verified pass.


### S12 — Client/server VAD and speech safety

**Prerequisites:** `S11,S09`. **Checklist section:** 3.13 Voice capture, VAD, transcribe and playback coordination. **Scope:** 14 atomic deliverables. **Primary source paths:** `src/OpenConnect.Voice/, packages/client-ts/, workers/realtime-voice/`.

**Authority:** Validate PCM sample rate/sequence/offset, isolate recurrent state, keep server authoritative, discard stale audio after barge-in. **Required verification pattern:** ONNX/browser waveform/silence tests, resampling, invalid frames, privacy and interruption.

#### OC2-D-VOICE-001 — Define typed client/server/hybrid/disabled VAD modes, threshold options, frame/session state and capability manifests.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Voice/, packages/client-ts/, workers/realtime-voice/` or the existing equivalent; record the package/adapter owner. Check `S11,S09` gate and the current source API/contract version.
2. **Concrete implementation:** Define typed client/server/hybrid/disabled VAD modes, threshold options, frame/session state and capability manifests. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Validate PCM sample rate/sequence/offset, isolate recurrent state, keep server authoritative, discard stale audio after barge-in. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify disabled/uninstalled, ready, startup failure and shutdown behavior independently. Validate model/asset manifest hash, license, actual capabilities and bounded resources where applicable. Check sequence gaps, reconnect epochs and speech/privacy transitions without duplicate accepted turns.
5. **Acceptance artifact:** ONNX/browser waveform/silence tests, resampling, invalid frames, privacy and interruption. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-VOICE-001` only after verified pass.

#### OC2-D-VOICE-002 — Reuse approved Silero ONNX model through optional .NET server VAD adapter, with independently scoped recurrent state per audio stream.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Voice/, packages/client-ts/, workers/realtime-voice/` or the existing equivalent; record the package/adapter owner. Check `S11,S09` gate and the current source API/contract version.
2. **Concrete implementation:** Reuse approved Silero ONNX model through optional .NET server VAD adapter, with independently scoped recurrent state per audio stream. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Validate PCM sample rate/sequence/offset, isolate recurrent state, keep server authoritative, discard stale audio after barge-in. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Exercise authorized and explicitly unauthorized principals/tenants; assert no side effect on denial. Verify no credentials, plaintext or raw user content leave the intended trust boundary. Check sequence gaps, reconnect epochs and speech/privacy transitions without duplicate accepted turns.
5. **Acceptance artifact:** ONNX/browser waveform/silence tests, resampling, invalid frames, privacy and interruption. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-VOICE-002` only after verified pass.

#### OC2-D-VOICE-003 — Verify server Silero frames use actual model-required rate/size (16kHz/512 samples for selected pinned model) and reject invalid PCM.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Voice/, packages/client-ts/, workers/realtime-voice/` or the existing equivalent; record the package/adapter owner. Check `S11,S09` gate and the current source API/contract version.
2. **Concrete implementation:** Verify server Silero frames use actual model-required rate/size (16kHz/512 samples for selected pinned model) and reject invalid PCM. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Validate PCM sample rate/sequence/offset, isolate recurrent state, keep server authoritative, discard stale audio after barge-in. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Run targeted success and deliberate negative regression in CI. Save machine-readable test artifacts and versioned audit evidence.
5. **Acceptance artifact:** ONNX/browser waveform/silence tests, resampling, invalid frames, privacy and interruption. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-VOICE-003` only after verified pass.

#### OC2-D-VOICE-004 — Implement bounded PCM16 resampler from client sample rate to selected detector, with explicit source absolute sample positions.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Voice/, packages/client-ts/, workers/realtime-voice/` or the existing equivalent; record the package/adapter owner. Check `S11,S09` gate and the current source API/contract version.
2. **Concrete implementation:** Implement bounded PCM16 resampler from client sample rate to selected detector, with explicit source absolute sample positions. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Validate PCM sample rate/sequence/offset, isolate recurrent state, keep server authoritative, discard stale audio after barge-in. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Run targeted success and deliberate negative regression in CI. Save machine-readable test artifacts and versioned audit evidence.
5. **Acceptance artifact:** ONNX/browser waveform/silence tests, resampling, invalid frames, privacy and interruption. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-VOICE-004` only after verified pass.

#### OC2-D-VOICE-005 — Reset VAD recurrent state on sequence discontinuity, sample-rate change, stream epoch reset or disposed session.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Voice/, packages/client-ts/, workers/realtime-voice/` or the existing equivalent; record the package/adapter owner. Check `S11,S09` gate and the current source API/contract version.
2. **Concrete implementation:** Reset VAD recurrent state on sequence discontinuity, sample-rate change, stream epoch reset or disposed session. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Validate PCM sample rate/sequence/offset, isolate recurrent state, keep server authoritative, discard stale audio after barge-in. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify disabled/uninstalled, ready, startup failure and shutdown behavior independently. Validate model/asset manifest hash, license, actual capabilities and bounded resources where applicable. Check sequence gaps, reconnect epochs and speech/privacy transitions without duplicate accepted turns.
5. **Acceptance artifact:** ONNX/browser waveform/silence tests, resampling, invalid frames, privacy and interruption. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-VOICE-005` only after verified pass.

#### OC2-D-VOICE-006 — Provide browser Silero ONNX/WASM local VAD with pinned assets and zero request-time model download; lazy load only after user action.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Voice/, packages/client-ts/, workers/realtime-voice/` or the existing equivalent; record the package/adapter owner. Check `S11,S09` gate and the current source API/contract version.
2. **Concrete implementation:** Provide browser Silero ONNX/WASM local VAD with pinned assets and zero request-time model download; lazy load only after user action. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Validate PCM sample rate/sequence/offset, isolate recurrent state, keep server authoritative, discard stale audio after barge-in. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify disabled/uninstalled, ready, startup failure and shutdown behavior independently. Validate model/asset manifest hash, license, actual capabilities and bounded resources where applicable. Check sequence gaps, reconnect epochs and speech/privacy transitions without duplicate accepted turns.
5. **Acceptance artifact:** ONNX/browser waveform/silence tests, resampling, invalid frames, privacy and interruption. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-VOICE-006` only after verified pass.

#### OC2-D-VOICE-007 — Provide WebRTC-class short-frame client barge-in detector with actual speech confirmation threshold and single-shot reset.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Voice/, packages/client-ts/, workers/realtime-voice/` or the existing equivalent; record the package/adapter owner. Check `S11,S09` gate and the current source API/contract version.
2. **Concrete implementation:** Provide WebRTC-class short-frame client barge-in detector with actual speech confirmation threshold and single-shot reset. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Validate PCM sample rate/sequence/offset, isolate recurrent state, keep server authoritative, discard stale audio after barge-in. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify successful integration with canonical contract fixtures. Inject malformed input, cancellation and unavailable dependency; assert typed error/degraded behavior.
5. **Acceptance artifact:** ONNX/browser waveform/silence tests, resampling, invalid frames, privacy and interruption. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-VOICE-007` only after verified pass.

#### OC2-D-VOICE-008 — Support optional native Android Silero VAD without duplicate microphone ownership or extra audio enhancement chain.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Voice/, packages/client-ts/, workers/realtime-voice/` or the existing equivalent; record the package/adapter owner. Check `S11,S09` gate and the current source API/contract version.
2. **Concrete implementation:** Support optional native Android Silero VAD without duplicate microphone ownership or extra audio enhancement chain. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Validate PCM sample rate/sequence/offset, isolate recurrent state, keep server authoritative, discard stale audio after barge-in. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify disabled/uninstalled, ready, startup failure and shutdown behavior independently. Validate model/asset manifest hash, license, actual capabilities and bounded resources where applicable. Check sequence gaps, reconnect epochs and speech/privacy transitions without duplicate accepted turns.
5. **Acceptance artifact:** ONNX/browser waveform/silence tests, resampling, invalid frames, privacy and interruption. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-VOICE-008` only after verified pass.

#### OC2-D-VOICE-009 — Implement client silence gating, pre-roll buffering, segment start/end/heartbeat packets and bounded resource limits.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Voice/, packages/client-ts/, workers/realtime-voice/` or the existing equivalent; record the package/adapter owner. Check `S11,S09` gate and the current source API/contract version.
2. **Concrete implementation:** Implement client silence gating, pre-roll buffering, segment start/end/heartbeat packets and bounded resource limits. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Validate PCM sample rate/sequence/offset, isolate recurrent state, keep server authoritative, discard stale audio after barge-in. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify successful integration with canonical contract fixtures. Inject malformed input, cancellation and unavailable dependency; assert typed error/degraded behavior.
5. **Acceptance artifact:** ONNX/browser waveform/silence tests, resampling, invalid frames, privacy and interruption. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-VOICE-009` only after verified pass.

#### OC2-D-VOICE-010 — Enforce server authority for accepted speech turns, final STT/transcripts and durable task admission regardless client VAD decisions.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Voice/, packages/client-ts/, workers/realtime-voice/` or the existing equivalent; record the package/adapter owner. Check `S11,S09` gate and the current source API/contract version.
2. **Concrete implementation:** Enforce server authority for accepted speech turns, final STT/transcripts and durable task admission regardless client VAD decisions. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Validate PCM sample rate/sequence/offset, isolate recurrent state, keep server authoritative, discard stale audio after barge-in. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify disabled/uninstalled, ready, startup failure and shutdown behavior independently. Validate model/asset manifest hash, license, actual capabilities and bounded resources where applicable. Check sequence gaps, reconnect epochs and speech/privacy transitions without duplicate accepted turns.
5. **Acceptance artifact:** ONNX/browser waveform/silence tests, resampling, invalid frames, privacy and interruption. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-VOICE-010` only after verified pass.

#### OC2-D-VOICE-011 — Fence speech epochs across microphone capture, STT, model response, TTS, output playback and interruption.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Voice/, packages/client-ts/, workers/realtime-voice/` or the existing equivalent; record the package/adapter owner. Check `S11,S09` gate and the current source API/contract version.
2. **Concrete implementation:** Fence speech epochs across microphone capture, STT, model response, TTS, output playback and interruption. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Validate PCM sample rate/sequence/offset, isolate recurrent state, keep server authoritative, discard stale audio after barge-in. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Prove commit/rollback behavior against an actual supported relational provider. Inject lost ACK, stale fencing token and concurrent writer where the contract touches durable state.
5. **Acceptance artifact:** ONNX/browser waveform/silence tests, resampling, invalid frames, privacy and interruption. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-VOICE-011` only after verified pass.

#### OC2-D-VOICE-012 — Test suppression of stale audio on barge-in and retained text transcript availability when TTS absent.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Voice/, packages/client-ts/, workers/realtime-voice/` or the existing equivalent; record the package/adapter owner. Check `S11,S09` gate and the current source API/contract version.
2. **Concrete implementation:** Test suppression of stale audio on barge-in and retained text transcript availability when TTS absent. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Validate PCM sample rate/sequence/offset, isolate recurrent state, keep server authoritative, discard stale audio after barge-in. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify disabled/uninstalled, ready, startup failure and shutdown behavior independently. Validate model/asset manifest hash, license, actual capabilities and bounded resources where applicable. Check sequence gaps, reconnect epochs and speech/privacy transitions without duplicate accepted turns.
5. **Acceptance artifact:** ONNX/browser waveform/silence tests, resampling, invalid frames, privacy and interruption. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-VOICE-012` only after verified pass.

#### OC2-D-VOICE-013 — Maintain privacy-zone policy: local VAD alone does not make remote raw audio privacy preserving.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Voice/, packages/client-ts/, workers/realtime-voice/` or the existing equivalent; record the package/adapter owner. Check `S11,S09` gate and the current source API/contract version.
2. **Concrete implementation:** Maintain privacy-zone policy: local VAD alone does not make remote raw audio privacy preserving. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Validate PCM sample rate/sequence/offset, isolate recurrent state, keep server authoritative, discard stale audio after barge-in. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Exercise authorized and explicitly unauthorized principals/tenants; assert no side effect on denial. Verify no credentials, plaintext or raw user content leave the intended trust boundary. Check sequence gaps, reconnect epochs and speech/privacy transitions without duplicate accepted turns.
5. **Acceptance artifact:** ONNX/browser waveform/silence tests, resampling, invalid frames, privacy and interruption. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-VOICE-013` only after verified pass.

#### OC2-D-VOICE-014 — Test frame gaps, concurrent sessions, silence, synthetic voiced signal, browser permission denial, missing model and clean disposal.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Voice/, packages/client-ts/, workers/realtime-voice/` or the existing equivalent; record the package/adapter owner. Check `S11,S09` gate and the current source API/contract version.
2. **Concrete implementation:** Test frame gaps, concurrent sessions, silence, synthetic voiced signal, browser permission denial, missing model and clean disposal. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Validate PCM sample rate/sequence/offset, isolate recurrent state, keep server authoritative, discard stale audio after barge-in. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify disabled/uninstalled, ready, startup failure and shutdown behavior independently. Validate model/asset manifest hash, license, actual capabilities and bounded resources where applicable.
5. **Acceptance artifact:** ONNX/browser waveform/silence tests, resampling, invalid frames, privacy and interruption. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-VOICE-014` only after verified pass.


### S13 — Optional isolated model workers and production images

**Prerequisites:** `S09,S10`. **Checklist section:** 3.14 Python worker common platform and each Dockerized Python capability. **Scope:** 18 atomic deliverables. **Primary source paths:** `workers/, infra/docker/, .github/workflows/workers-*.yml`.

**Authority:** Reuse common signed worker contract, opt-in model cache at startup, truthful readiness, no request-time downloads or DB/vault secrets. **Required verification pattern:** uv/pytest plus Docker start/stop, checksum mismatch, offline reuse, real model smoke only when advertised.

#### OC2-D-WORKERS-001 — Build common Python 3.12 uv-locked package with versioned Pydantic schemas and golden C#/TS parity fixtures.

1. **Repository and prerequisites:** Work in `workers/, infra/docker/, .github/workflows/workers-*.yml` or the existing equivalent; record the package/adapter owner. Check `S09,S10` gate and the current source API/contract version.
2. **Concrete implementation:** Build common Python 3.12 uv-locked package with versioned Pydantic schemas and golden C#/TS parity fixtures. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Reuse common signed worker contract, opt-in model cache at startup, truthful readiness, no request-time downloads or DB/vault secrets. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify successful integration with canonical contract fixtures. Inject malformed input, cancellation and unavailable dependency; assert typed error/degraded behavior.
5. **Acceptance artifact:** uv/pytest plus Docker start/stop, checksum mismatch, offline reuse, real model smoke only when advertised. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-WORKERS-001` only after verified pass.

#### OC2-D-WORKERS-002 — Provide worker machine authentication, scoped payload fetch, NATS pull/ACK and bounded leases/retries/cancellation.

1. **Repository and prerequisites:** Work in `workers/, infra/docker/, .github/workflows/workers-*.yml` or the existing equivalent; record the package/adapter owner. Check `S09,S10` gate and the current source API/contract version.
2. **Concrete implementation:** Provide worker machine authentication, scoped payload fetch, NATS pull/ACK and bounded leases/retries/cancellation. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Reuse common signed worker contract, opt-in model cache at startup, truthful readiness, no request-time downloads or DB/vault secrets. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Prove commit/rollback behavior against an actual supported relational provider. Inject lost ACK, stale fencing token and concurrent writer where the contract touches durable state.
5. **Acceptance artifact:** uv/pytest plus Docker start/stop, checksum mismatch, offline reuse, real model smoke only when advertised. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-WORKERS-002` only after verified pass.

#### OC2-D-WORKERS-003 — Separate liveness from model-backed true readiness and report health TTL, model ID/revision/license and installed capabilities.

1. **Repository and prerequisites:** Work in `workers/, infra/docker/, .github/workflows/workers-*.yml` or the existing equivalent; record the package/adapter owner. Check `S09,S10` gate and the current source API/contract version.
2. **Concrete implementation:** Separate liveness from model-backed true readiness and report health TTL, model ID/revision/license and installed capabilities. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Reuse common signed worker contract, opt-in model cache at startup, truthful readiness, no request-time downloads or DB/vault secrets. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify successful integration with canonical contract fixtures. Inject malformed input, cancellation and unavailable dependency; assert typed error/degraded behavior.
5. **Acceptance artifact:** uv/pytest plus Docker start/stop, checksum mismatch, offline reuse, real model smoke only when advertised. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-WORKERS-003` only after verified pass.

#### OC2-D-WORKERS-004 — Create security Python image with selectable rules, GLiNER, DeBERTa, Qwen3Guard, PaddleOCR and YuNet detectors.

1. **Repository and prerequisites:** Work in `workers/, infra/docker/, .github/workflows/workers-*.yml` or the existing equivalent; record the package/adapter owner. Check `S09,S10` gate and the current source API/contract version.
2. **Concrete implementation:** Create security Python image with selectable rules, GLiNER, DeBERTa, Qwen3Guard, PaddleOCR and YuNet detectors. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Reuse common signed worker contract, opt-in model cache at startup, truthful readiness, no request-time downloads or DB/vault secrets. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Prove commit/rollback behavior against an actual supported relational provider. Inject lost ACK, stale fencing token and concurrent writer where the contract touches durable state.
5. **Acceptance artifact:** uv/pytest plus Docker start/stop, checksum mismatch, offline reuse, real model smoke only when advertised. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-WORKERS-004` only after verified pass.

#### OC2-D-WORKERS-005 — Create classification Python image with selectable licensed models, language/label taxonomy and abstention semantics.

1. **Repository and prerequisites:** Work in `workers/, infra/docker/, .github/workflows/workers-*.yml` or the existing equivalent; record the package/adapter owner. Check `S09,S10` gate and the current source API/contract version.
2. **Concrete implementation:** Create classification Python image with selectable licensed models, language/label taxonomy and abstention semantics. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Reuse common signed worker contract, opt-in model cache at startup, truthful readiness, no request-time downloads or DB/vault secrets. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Prove commit/rollback behavior against an actual supported relational provider. Inject lost ACK, stale fencing token and concurrent writer where the contract touches durable state.
5. **Acceptance artifact:** uv/pytest plus Docker start/stop, checksum mismatch, offline reuse, real model smoke only when advertised. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-WORKERS-005` only after verified pass.

#### OC2-D-WORKERS-006 — Create NLLB Python translation image with pinned model and explicit supported language pairs and license clearance.

1. **Repository and prerequisites:** Work in `workers/, infra/docker/, .github/workflows/workers-*.yml` or the existing equivalent; record the package/adapter owner. Check `S09,S10` gate and the current source API/contract version.
2. **Concrete implementation:** Create NLLB Python translation image with pinned model and explicit supported language pairs and license clearance. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Reuse common signed worker contract, opt-in model cache at startup, truthful readiness, no request-time downloads or DB/vault secrets. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify disabled/uninstalled, ready, startup failure and shutdown behavior independently. Validate model/asset manifest hash, license, actual capabilities and bounded resources where applicable.
5. **Acceptance artifact:** uv/pytest plus Docker start/stop, checksum mismatch, offline reuse, real model smoke only when advertised. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-WORKERS-006` only after verified pass.

#### OC2-D-WORKERS-007 — Create self-hosted inference Python image that advertises chat/stream/tool/embedding only when backend actually supports each.

1. **Repository and prerequisites:** Work in `workers/, infra/docker/, .github/workflows/workers-*.yml` or the existing equivalent; record the package/adapter owner. Check `S09,S10` gate and the current source API/contract version.
2. **Concrete implementation:** Create self-hosted inference Python image that advertises chat/stream/tool/embedding only when backend actually supports each. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Reuse common signed worker contract, opt-in model cache at startup, truthful readiness, no request-time downloads or DB/vault secrets. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify disabled/uninstalled, ready, startup failure and shutdown behavior independently. Validate model/asset manifest hash, license, actual capabilities and bounded resources where applicable.
5. **Acceptance artifact:** uv/pytest plus Docker start/stop, checksum mismatch, offline reuse, real model smoke only when advertised. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-WORKERS-007` only after verified pass.

#### OC2-D-WORKERS-008 — Create transcription Python image with codec/sample-rate/locale and actual recorded/streaming STT capability declaration.

1. **Repository and prerequisites:** Work in `workers/, infra/docker/, .github/workflows/workers-*.yml` or the existing equivalent; record the package/adapter owner. Check `S09,S10` gate and the current source API/contract version.
2. **Concrete implementation:** Create transcription Python image with codec/sample-rate/locale and actual recorded/streaming STT capability declaration. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Reuse common signed worker contract, opt-in model cache at startup, truthful readiness, no request-time downloads or DB/vault secrets. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify disabled/uninstalled, ready, startup failure and shutdown behavior independently. Validate model/asset manifest hash, license, actual capabilities and bounded resources where applicable.
5. **Acceptance artifact:** uv/pytest plus Docker start/stop, checksum mismatch, offline reuse, real model smoke only when advertised. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-WORKERS-008` only after verified pass.

#### OC2-D-WORKERS-009 — Create realtime-voice Python image with VAD/STT/TTS adapters, speech epochs and interruption/cancellation semantics.

1. **Repository and prerequisites:** Work in `workers/, infra/docker/, .github/workflows/workers-*.yml` or the existing equivalent; record the package/adapter owner. Check `S09,S10` gate and the current source API/contract version.
2. **Concrete implementation:** Create realtime-voice Python image with VAD/STT/TTS adapters, speech epochs and interruption/cancellation semantics. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Reuse common signed worker contract, opt-in model cache at startup, truthful readiness, no request-time downloads or DB/vault secrets. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify disabled/uninstalled, ready, startup failure and shutdown behavior independently. Validate model/asset manifest hash, license, actual capabilities and bounded resources where applicable. Check sequence gaps, reconnect epochs and speech/privacy transitions without duplicate accepted turns.
5. **Acceptance artifact:** uv/pytest plus Docker start/stop, checksum mismatch, offline reuse, real model smoke only when advertised. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-WORKERS-009` only after verified pass.

#### OC2-D-WORKERS-010 — Create independent Kokoro Python image with English af_heart preferred only when approved model/voice installed.

1. **Repository and prerequisites:** Work in `workers/, infra/docker/, .github/workflows/workers-*.yml` or the existing equivalent; record the package/adapter owner. Check `S09,S10` gate and the current source API/contract version.
2. **Concrete implementation:** Create independent Kokoro Python image with English af_heart preferred only when approved model/voice installed. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Reuse common signed worker contract, opt-in model cache at startup, truthful readiness, no request-time downloads or DB/vault secrets. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify disabled/uninstalled, ready, startup failure and shutdown behavior independently. Validate model/asset manifest hash, license, actual capabilities and bounded resources where applicable.
5. **Acceptance artifact:** uv/pytest plus Docker start/stop, checksum mismatch, offline reuse, real model smoke only when advertised. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-WORKERS-010` only after verified pass.

#### OC2-D-WORKERS-011 — Create independent MMS-TTS Python image with opt-in permitted language checkpoints and noncommercial licensing guard.

1. **Repository and prerequisites:** Work in `workers/, infra/docker/, .github/workflows/workers-*.yml` or the existing equivalent; record the package/adapter owner. Check `S09,S10` gate and the current source API/contract version.
2. **Concrete implementation:** Create independent MMS-TTS Python image with opt-in permitted language checkpoints and noncommercial licensing guard. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Reuse common signed worker contract, opt-in model cache at startup, truthful readiness, no request-time downloads or DB/vault secrets. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify disabled/uninstalled, ready, startup failure and shutdown behavior independently. Validate model/asset manifest hash, license, actual capabilities and bounded resources where applicable.
5. **Acceptance artifact:** uv/pytest plus Docker start/stop, checksum mismatch, offline reuse, real model smoke only when advertised. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-WORKERS-011` only after verified pass.

#### OC2-D-WORKERS-012 — For every Python image create own Dockerfile, pyproject.toml, uv.lock, tests, manifest, health endpoints and Compose profile.

1. **Repository and prerequisites:** Work in `workers/, infra/docker/, .github/workflows/workers-*.yml` or the existing equivalent; record the package/adapter owner. Check `S09,S10` gate and the current source API/contract version.
2. **Concrete implementation:** For every Python image create own Dockerfile, pyproject.toml, uv.lock, tests, manifest, health endpoints and Compose profile. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Reuse common signed worker contract, opt-in model cache at startup, truthful readiness, no request-time downloads or DB/vault secrets. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify disabled/uninstalled, ready, startup failure and shutdown behavior independently. Validate model/asset manifest hash, license, actual capabilities and bounded resources where applicable.
5. **Acceptance artifact:** uv/pytest plus Docker start/stop, checksum mismatch, offline reuse, real model smoke only when advertised. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-WORKERS-012` only after verified pass.

#### OC2-D-WORKERS-013 — Provision only enabled worker model assets at first startup using signed/pinned manifests, checksum and audited license.

1. **Repository and prerequisites:** Work in `workers/, infra/docker/, .github/workflows/workers-*.yml` or the existing equivalent; record the package/adapter owner. Check `S09,S10` gate and the current source API/contract version.
2. **Concrete implementation:** Provision only enabled worker model assets at first startup using signed/pinned manifests, checksum and audited license. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Reuse common signed worker contract, opt-in model cache at startup, truthful readiness, no request-time downloads or DB/vault secrets. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify disabled/uninstalled, ready, startup failure and shutdown behavior independently. Validate model/asset manifest hash, license, actual capabilities and bounded resources where applicable.
5. **Acceptance artifact:** uv/pytest plus Docker start/stop, checksum mismatch, offline reuse, real model smoke only when advertised. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-WORKERS-013` only after verified pass.

#### OC2-D-WORKERS-014 — Persist model cache in dedicated named volume with atomic staging/checksum/promotion, lock and offline reuse.

1. **Repository and prerequisites:** Work in `workers/, infra/docker/, .github/workflows/workers-*.yml` or the existing equivalent; record the package/adapter owner. Check `S09,S10` gate and the current source API/contract version.
2. **Concrete implementation:** Persist model cache in dedicated named volume with atomic staging/checksum/promotion, lock and offline reuse. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Reuse common signed worker contract, opt-in model cache at startup, truthful readiness, no request-time downloads or DB/vault secrets. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Run targeted success and deliberate negative regression in CI. Save machine-readable test artifacts and versioned audit evidence.
5. **Acceptance artifact:** uv/pytest plus Docker start/stop, checksum mismatch, offline reuse, real model smoke only when advertised. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-WORKERS-014` only after verified pass.

#### OC2-D-WORKERS-015 — Never download models during inference/synthesis request or import disabled detector models as side effects.

1. **Repository and prerequisites:** Work in `workers/, infra/docker/, .github/workflows/workers-*.yml` or the existing equivalent; record the package/adapter owner. Check `S09,S10` gate and the current source API/contract version.
2. **Concrete implementation:** Never download models during inference/synthesis request or import disabled detector models as side effects. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Reuse common signed worker contract, opt-in model cache at startup, truthful readiness, no request-time downloads or DB/vault secrets. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify successful integration with canonical contract fixtures. Inject malformed input, cancellation and unavailable dependency; assert typed error/degraded behavior.
5. **Acceptance artifact:** uv/pytest plus Docker start/stop, checksum mismatch, offline reuse, real model smoke only when advertised. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-WORKERS-015` only after verified pass.

#### OC2-D-WORKERS-016 — Create CPU-only smoke, optional GPU profile, memory/concurrency admission and clean SIGTERM/health behavior for each image.

1. **Repository and prerequisites:** Work in `workers/, infra/docker/, .github/workflows/workers-*.yml` or the existing equivalent; record the package/adapter owner. Check `S09,S10` gate and the current source API/contract version.
2. **Concrete implementation:** Create CPU-only smoke, optional GPU profile, memory/concurrency admission and clean SIGTERM/health behavior for each image. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Reuse common signed worker contract, opt-in model cache at startup, truthful readiness, no request-time downloads or DB/vault secrets. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify disabled/uninstalled, ready, startup failure and shutdown behavior independently. Validate model/asset manifest hash, license, actual capabilities and bounded resources where applicable.
5. **Acceptance artifact:** uv/pytest plus Docker start/stop, checksum mismatch, offline reuse, real model smoke only when advertised. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-WORKERS-016` only after verified pass.

#### OC2-D-WORKERS-017 — Verify each worker independently can be absent without breaking unrelated text agent, MCP and MiniComposer functionality.

1. **Repository and prerequisites:** Work in `workers/, infra/docker/, .github/workflows/workers-*.yml` or the existing equivalent; record the package/adapter owner. Check `S09,S10` gate and the current source API/contract version.
2. **Concrete implementation:** Verify each worker independently can be absent without breaking unrelated text agent, MCP and MiniComposer functionality. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Reuse common signed worker contract, opt-in model cache at startup, truthful readiness, no request-time downloads or DB/vault secrets. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify disabled/uninstalled, ready, startup failure and shutdown behavior independently. Validate model/asset manifest hash, license, actual capabilities and bounded resources where applicable. Verify unregistered/malicious MCP tool schemas cannot expand permissions, redirect network access or bypass approval.
5. **Acceptance artifact:** uv/pytest plus Docker start/stop, checksum mismatch, offline reuse, real model smoke only when advertised. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-WORKERS-017` only after verified pass.

#### OC2-D-WORKERS-018 — Test broker redelivery and completion ACK only after durable OpenConnect host outcome for each durable worker job.

1. **Repository and prerequisites:** Work in `workers/, infra/docker/, .github/workflows/workers-*.yml` or the existing equivalent; record the package/adapter owner. Check `S09,S10` gate and the current source API/contract version.
2. **Concrete implementation:** Test broker redelivery and completion ACK only after durable OpenConnect host outcome for each durable worker job. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Reuse common signed worker contract, opt-in model cache at startup, truthful readiness, no request-time downloads or DB/vault secrets. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify disabled/uninstalled, ready, startup failure and shutdown behavior independently. Validate model/asset manifest hash, license, actual capabilities and bounded resources where applicable.
5. **Acceptance artifact:** uv/pytest plus Docker start/stop, checksum mismatch, offline reuse, real model smoke only when advertised. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-WORKERS-018` only after verified pass.


### S14 — Navigator, Playwright and .NET worker host composition

**Prerequisites:** `S11,S04`. **Checklist section:** 3.15 Navigator semantic UI, Playwright MCP and optional standalone .NET worker hosts. **Scope:** 14 atomic deliverables. **Primary source paths:** `src/OpenConnect.Navigator*/, packages/navigator/, workers/navigator-playwright/`.

**Authority:** Reuse the common OpenConnect principal, capabilities, task/event transport and MAF workflow; browser commands require bounded grants. **Required verification pattern:** No-Playwright fallback, selector spoof, SSRF, consent revocation, worker lease and UI capture tests.

#### OC2-D-BROWSER-001 — Implement optional Navigator semantic targets with stable registered ID/route/action/sensitivity/capability metadata.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Navigator*/, packages/navigator/, workers/navigator-playwright/` or the existing equivalent; record the package/adapter owner. Check `S11,S04` gate and the current source API/contract version.
2. **Concrete implementation:** Implement optional Navigator semantic targets with stable registered ID/route/action/sensitivity/capability metadata. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Reuse the common OpenConnect principal, capabilities, task/event transport and MAF workflow; browser commands require bounded grants. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Prove commit/rollback behavior against an actual supported relational provider. Inject lost ACK, stale fencing token and concurrent writer where the contract touches durable state.
5. **Acceptance artifact:** No-Playwright fallback, selector spoof, SSRF, consent revocation, worker lease and UI capture tests. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-BROWSER-001` only after verified pass.

#### OC2-D-BROWSER-002 — Implement read-only tours, highlighting, focus/scroll/next/back/pause/skip and a11y without Playwright/TTS.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Navigator*/, packages/navigator/, workers/navigator-playwright/` or the existing equivalent; record the package/adapter owner. Check `S11,S04` gate and the current source API/contract version.
2. **Concrete implementation:** Implement read-only tours, highlighting, focus/scroll/next/back/pause/skip and a11y without Playwright/TTS. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Reuse the common OpenConnect principal, capabilities, task/event transport and MAF workflow; browser commands require bounded grants. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify disabled/uninstalled, ready, startup failure and shutdown behavior independently. Validate model/asset manifest hash, license, actual capabilities and bounded resources where applicable.
5. **Acceptance artifact:** No-Playwright fallback, selector spoof, SSRF, consent revocation, worker lease and UI capture tests. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-BROWSER-002` only after verified pass.

#### OC2-D-BROWSER-003 — Implement bounded DOM observation with permission filtering, no secret/input value capture and injection-safe context treatment.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Navigator*/, packages/navigator/, workers/navigator-playwright/` or the existing equivalent; record the package/adapter owner. Check `S11,S04` gate and the current source API/contract version.
2. **Concrete implementation:** Implement bounded DOM observation with permission filtering, no secret/input value capture and injection-safe context treatment. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Reuse the common OpenConnect principal, capabilities, task/event transport and MAF workflow; browser commands require bounded grants. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify successful integration with canonical contract fixtures. Inject malformed input, cancellation and unavailable dependency; assert typed error/degraded behavior.
5. **Acceptance artifact:** No-Playwright fallback, selector spoof, SSRF, consent revocation, worker lease and UI capture tests. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-BROWSER-003` only after verified pass.

#### OC2-D-BROWSER-004 — Implement short-lived authenticated client bridge grants scoped to user/tenant/route/target/revision/action digest.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Navigator*/, packages/navigator/, workers/navigator-playwright/` or the existing equivalent; record the package/adapter owner. Check `S11,S04` gate and the current source API/contract version.
2. **Concrete implementation:** Implement short-lived authenticated client bridge grants scoped to user/tenant/route/target/revision/action digest. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Reuse the common OpenConnect principal, capabilities, task/event transport and MAF workflow; browser commands require bounded grants. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Exercise authorized and explicitly unauthorized principals/tenants; assert no side effect on denial. Verify no credentials, plaintext or raw user content leave the intended trust boundary.
5. **Acceptance artifact:** No-Playwright fallback, selector spoof, SSRF, consent revocation, worker lease and UI capture tests. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-BROWSER-004` only after verified pass.

#### OC2-D-BROWSER-005 — Require independent explicit consent for screenshots/capture and separate authorization for UI mutations.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Navigator*/, packages/navigator/, workers/navigator-playwright/` or the existing equivalent; record the package/adapter owner. Check `S11,S04` gate and the current source API/contract version.
2. **Concrete implementation:** Require independent explicit consent for screenshots/capture and separate authorization for UI mutations. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Reuse the common OpenConnect principal, capabilities, task/event transport and MAF workflow; browser commands require bounded grants. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Exercise authorized and explicitly unauthorized principals/tenants; assert no side effect on denial. Verify no credentials, plaintext or raw user content leave the intended trust boundary.
5. **Acceptance artifact:** No-Playwright fallback, selector spoof, SSRF, consent revocation, worker lease and UI capture tests. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-BROWSER-005` only after verified pass.

#### OC2-D-BROWSER-006 — Require registered target + fresh reauthorization + actual postcondition receipts for any mutating UI operation.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Navigator*/, packages/navigator/, workers/navigator-playwright/` or the existing equivalent; record the package/adapter owner. Check `S11,S04` gate and the current source API/contract version.
2. **Concrete implementation:** Require registered target + fresh reauthorization + actual postcondition receipts for any mutating UI operation. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Reuse the common OpenConnect principal, capabilities, task/event transport and MAF workflow; browser commands require bounded grants. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Exercise authorized and explicitly unauthorized principals/tenants; assert no side effect on denial. Verify no credentials, plaintext or raw user content leave the intended trust boundary.
5. **Acceptance artifact:** No-Playwright fallback, selector spoof, SSRF, consent revocation, worker lease and UI capture tests. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-BROWSER-006` only after verified pass.

#### OC2-D-BROWSER-007 — Create Node.js/Chromium Playwright MCP Docker image with per-job isolated browser profiles and explicit tool allowlist.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Navigator*/, packages/navigator/, workers/navigator-playwright/` or the existing equivalent; record the package/adapter owner. Check `S11,S04` gate and the current source API/contract version.
2. **Concrete implementation:** Create Node.js/Chromium Playwright MCP Docker image with per-job isolated browser profiles and explicit tool allowlist. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Reuse the common OpenConnect principal, capabilities, task/event transport and MAF workflow; browser commands require bounded grants. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify disabled/uninstalled, ready, startup failure and shutdown behavior independently. Validate model/asset manifest hash, license, actual capabilities and bounded resources where applicable. Verify unregistered/malicious MCP tool schemas cannot expand permissions, redirect network access or bypass approval.
5. **Acceptance artifact:** No-Playwright fallback, selector spoof, SSRF, consent revocation, worker lease and UI capture tests. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-BROWSER-007` only after verified pass.

#### OC2-D-BROWSER-008 — Enforce network origin allowlists, SSRF/redirect/DNS rebinding defenses and ban unrestricted JS evaluation by default.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Navigator*/, packages/navigator/, workers/navigator-playwright/` or the existing equivalent; record the package/adapter owner. Check `S11,S04` gate and the current source API/contract version.
2. **Concrete implementation:** Enforce network origin allowlists, SSRF/redirect/DNS rebinding defenses and ban unrestricted JS evaluation by default. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Reuse the common OpenConnect principal, capabilities, task/event transport and MAF workflow; browser commands require bounded grants. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Exercise authorized and explicitly unauthorized principals/tenants; assert no side effect on denial. Verify no credentials, plaintext or raw user content leave the intended trust boundary.
5. **Acceptance artifact:** No-Playwright fallback, selector spoof, SSRF, consent revocation, worker lease and UI capture tests. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-BROWSER-008` only after verified pass.

#### OC2-D-BROWSER-009 — Ensure Playwright absence does not affect semantic read-only navigation or unrelated MCP services.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Navigator*/, packages/navigator/, workers/navigator-playwright/` or the existing equivalent; record the package/adapter owner. Check `S11,S04` gate and the current source API/contract version.
2. **Concrete implementation:** Ensure Playwright absence does not affect semantic read-only navigation or unrelated MCP services. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Reuse the common OpenConnect principal, capabilities, task/event transport and MAF workflow; browser commands require bounded grants. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify successful integration with canonical contract fixtures. Inject malformed input, cancellation and unavailable dependency; assert typed error/degraded behavior. Verify unregistered/malicious MCP tool schemas cannot expand permissions, redirect network access or bypass approval.
5. **Acceptance artifact:** No-Playwright fallback, selector spoof, SSRF, consent revocation, worker lease and UI capture tests. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-BROWSER-009` only after verified pass.

#### OC2-D-BROWSER-010 — Wire operational Navigator through existing workload identity and selected durable run owner, not another scheduler.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Navigator*/, packages/navigator/, workers/navigator-playwright/` or the existing equivalent; record the package/adapter owner. Check `S11,S04` gate and the current source API/contract version.
2. **Concrete implementation:** Wire operational Navigator through existing workload identity and selected durable run owner, not another scheduler. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Reuse the common OpenConnect principal, capabilities, task/event transport and MAF workflow; browser commands require bounded grants. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify successful integration with canonical contract fixtures. Inject malformed input, cancellation and unavailable dependency; assert typed error/degraded behavior.
5. **Acceptance artifact:** No-Playwright fallback, selector spoof, SSRF, consent revocation, worker lease and UI capture tests. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-BROWSER-010` only after verified pass.

#### OC2-D-BROWSER-011 — Provide optional standalone .NET operational-agent worker-host image reusing same native MAF durable backend.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Navigator*/, packages/navigator/, workers/navigator-playwright/` or the existing equivalent; record the package/adapter owner. Check `S11,S04` gate and the current source API/contract version.
2. **Concrete implementation:** Provide optional standalone .NET operational-agent worker-host image reusing same native MAF durable backend. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Reuse the common OpenConnect principal, capabilities, task/event transport and MAF workflow; browser commands require bounded grants. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify disabled/uninstalled, ready, startup failure and shutdown behavior independently. Validate model/asset manifest hash, license, actual capabilities and bounded resources where applicable.
5. **Acceptance artifact:** No-Playwright fallback, selector spoof, SSRF, consent revocation, worker lease and UI capture tests. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-BROWSER-011` only after verified pass.

#### OC2-D-BROWSER-012 — Provide optional standalone .NET outbox-dispatcher worker-host image, with single consumer ownership and safe embedded alternative.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Navigator*/, packages/navigator/, workers/navigator-playwright/` or the existing equivalent; record the package/adapter owner. Check `S11,S04` gate and the current source API/contract version.
2. **Concrete implementation:** Provide optional standalone .NET outbox-dispatcher worker-host image, with single consumer ownership and safe embedded alternative. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Reuse the common OpenConnect principal, capabilities, task/event transport and MAF workflow; browser commands require bounded grants. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Prove commit/rollback behavior against an actual supported relational provider. Inject lost ACK, stale fencing token and concurrent writer where the contract touches durable state.
5. **Acceptance artifact:** No-Playwright fallback, selector spoof, SSRF, consent revocation, worker lease and UI capture tests. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-BROWSER-012` only after verified pass.

#### OC2-D-BROWSER-013 — Prevent embedded and standalone supervisor/dispatcher from simultaneously owning same lease or publishing twice.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Navigator*/, packages/navigator/, workers/navigator-playwright/` or the existing equivalent; record the package/adapter owner. Check `S11,S04` gate and the current source API/contract version.
2. **Concrete implementation:** Prevent embedded and standalone supervisor/dispatcher from simultaneously owning same lease or publishing twice. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Reuse the common OpenConnect principal, capabilities, task/event transport and MAF workflow; browser commands require bounded grants. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Prove commit/rollback behavior against an actual supported relational provider. Inject lost ACK, stale fencing token and concurrent writer where the contract touches durable state.
5. **Acceptance artifact:** No-Playwright fallback, selector spoof, SSRF, consent revocation, worker lease and UI capture tests. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-BROWSER-013` only after verified pass.

#### OC2-D-BROWSER-014 — Test capture redaction on actual image bytes/metadata and deny uncertain sensitive pixels/frames.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Navigator*/, packages/navigator/, workers/navigator-playwright/` or the existing equivalent; record the package/adapter owner. Check `S11,S04` gate and the current source API/contract version.
2. **Concrete implementation:** Test capture redaction on actual image bytes/metadata and deny uncertain sensitive pixels/frames. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Reuse the common OpenConnect principal, capabilities, task/event transport and MAF workflow; browser commands require bounded grants. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Exercise authorized and explicitly unauthorized principals/tenants; assert no side effect on denial. Verify no credentials, plaintext or raw user content leave the intended trust boundary.
5. **Acceptance artifact:** No-Playwright fallback, selector spoof, SSRF, consent revocation, worker lease and UI capture tests. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-BROWSER-014` only after verified pass.


### S15 — Optional semantic memory, KMS, vector subscribers

**Prerequisites:** `S05,S06,S09`. **Checklist section:** 3.16 Qdrant semantic projections, Infisical key wrapping and extension contracts. **Scope:** 11 atomic deliverables. **Primary source paths:** `src/OpenConnect.QdrantVactor/, src/OpenConnect.Cryptography.Infisical/`.

**Authority:** Treat as optional packages; consume authorized safe projections or DEK wrapping only, not global journal scans or plaintext restore. **Required verification pattern:** Outage without core regression, tenant isolation, deletion/tombstone and KMS key rotation.

#### OC2-D-EXT-001 — Implement independently installable QdrantVactor registration with no Qdrant client/consumer when absent.

1. **Repository and prerequisites:** Work in `src/OpenConnect.QdrantVactor/, src/OpenConnect.Cryptography.Infisical/` or the existing equivalent; record the package/adapter owner. Check `S05,S06,S09` gate and the current source API/contract version.
2. **Concrete implementation:** Implement independently installable QdrantVactor registration with no Qdrant client/consumer when absent. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Treat as optional packages; consume authorized safe projections or DEK wrapping only, not global journal scans or plaintext restore. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify successful integration with canonical contract fixtures. Inject malformed input, cancellation and unavailable dependency; assert typed error/degraded behavior.
5. **Acceptance artifact:** Outage without core regression, tenant isolation, deletion/tombstone and KMS key rotation. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-EXT-001` only after verified pass.

#### OC2-D-EXT-002 — Subscribe to committed safe agent events using approved IAgentEventSubscription, not unbounded journal-wide scan.

1. **Repository and prerequisites:** Work in `src/OpenConnect.QdrantVactor/, src/OpenConnect.Cryptography.Infisical/` or the existing equivalent; record the package/adapter owner. Check `S05,S06,S09` gate and the current source API/contract version.
2. **Concrete implementation:** Subscribe to committed safe agent events using approved IAgentEventSubscription, not unbounded journal-wide scan. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Treat as optional packages; consume authorized safe projections or DEK wrapping only, not global journal scans or plaintext restore. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Invoke an actual pinned MAF type and inspect real lifecycle/session data. Prove unsupported versions/profiles report unavailable instead of fabricating equivalent behavior.
5. **Acceptance artifact:** Outage without core regression, tenant isolation, deletion/tombstone and KMS key rotation. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-EXT-002` only after verified pass.

#### OC2-D-EXT-003 — Retrieve sanitized policy-approved projection with tenant/actor authorization before embedding any memory content.

1. **Repository and prerequisites:** Work in `src/OpenConnect.QdrantVactor/, src/OpenConnect.Cryptography.Infisical/` or the existing equivalent; record the package/adapter owner. Check `S05,S06,S09` gate and the current source API/contract version.
2. **Concrete implementation:** Retrieve sanitized policy-approved projection with tenant/actor authorization before embedding any memory content. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Treat as optional packages; consume authorized safe projections or DEK wrapping only, not global journal scans or plaintext restore. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Exercise authorized and explicitly unauthorized principals/tenants; assert no side effect on denial. Verify no credentials, plaintext or raw user content leave the intended trust boundary.
5. **Acceptance artifact:** Outage without core regression, tenant isolation, deletion/tombstone and KMS key rotation. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-EXT-003` only after verified pass.

#### OC2-D-EXT-004 — Generate embedding asynchronously with pinned model revision and idempotent vector IDs; no vectors added to EventRecord.

1. **Repository and prerequisites:** Work in `src/OpenConnect.QdrantVactor/, src/OpenConnect.Cryptography.Infisical/` or the existing equivalent; record the package/adapter owner. Check `S05,S06,S09` gate and the current source API/contract version.
2. **Concrete implementation:** Generate embedding asynchronously with pinned model revision and idempotent vector IDs; no vectors added to EventRecord. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Treat as optional packages; consume authorized safe projections or DEK wrapping only, not global journal scans or plaintext restore. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify successful integration with canonical contract fixtures. Inject malformed input, cancellation and unavailable dependency; assert typed error/degraded behavior.
5. **Acceptance artifact:** Outage without core regression, tenant isolation, deletion/tombstone and KMS key rotation. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-EXT-004` only after verified pass.

#### OC2-D-EXT-005 — Filter Qdrant retrieval by tenant/agent/principal scopes at query time; prevent stale indexing from widening rights.

1. **Repository and prerequisites:** Work in `src/OpenConnect.QdrantVactor/, src/OpenConnect.Cryptography.Infisical/` or the existing equivalent; record the package/adapter owner. Check `S05,S06,S09` gate and the current source API/contract version.
2. **Concrete implementation:** Filter Qdrant retrieval by tenant/agent/principal scopes at query time; prevent stale indexing from widening rights. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Treat as optional packages; consume authorized safe projections or DEK wrapping only, not global journal scans or plaintext restore. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Exercise authorized and explicitly unauthorized principals/tenants; assert no side effect on denial. Verify no credentials, plaintext or raw user content leave the intended trust boundary.
5. **Acceptance artifact:** Outage without core regression, tenant isolation, deletion/tombstone and KMS key rotation. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-EXT-005` only after verified pass.

#### OC2-D-EXT-006 — Propagate source deletion, retention expiry and consent revocation to Qdrant vector removal/quarantine.

1. **Repository and prerequisites:** Work in `src/OpenConnect.QdrantVactor/, src/OpenConnect.Cryptography.Infisical/` or the existing equivalent; record the package/adapter owner. Check `S05,S06,S09` gate and the current source API/contract version.
2. **Concrete implementation:** Propagate source deletion, retention expiry and consent revocation to Qdrant vector removal/quarantine. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Treat as optional packages; consume authorized safe projections or DEK wrapping only, not global journal scans or plaintext restore. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify successful integration with canonical contract fixtures. Inject malformed input, cancellation and unavailable dependency; assert typed error/degraded behavior.
5. **Acceptance artifact:** Outage without core regression, tenant isolation, deletion/tombstone and KMS key rotation. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-EXT-006` only after verified pass.

#### OC2-D-EXT-007 — Prove Qdrant outage or missing embedder leaves direct/harness/workflow tasks independently usable.

1. **Repository and prerequisites:** Work in `src/OpenConnect.QdrantVactor/, src/OpenConnect.Cryptography.Infisical/` or the existing equivalent; record the package/adapter owner. Check `S05,S06,S09` gate and the current source API/contract version.
2. **Concrete implementation:** Prove Qdrant outage or missing embedder leaves direct/harness/workflow tasks independently usable. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Treat as optional packages; consume authorized safe projections or DEK wrapping only, not global journal scans or plaintext restore. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Invoke an actual pinned MAF type and inspect real lifecycle/session data. Prove unsupported versions/profiles report unavailable instead of fabricating equivalent behavior.
5. **Acceptance artifact:** Outage without core regression, tenant isolation, deletion/tombstone and KMS key rotation. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-EXT-007` only after verified pass.

#### OC2-D-EXT-008 — Implement default encrypted local key-wrapping provider with securely persistent rotated master key policy.

1. **Repository and prerequisites:** Work in `src/OpenConnect.QdrantVactor/, src/OpenConnect.Cryptography.Infisical/` or the existing equivalent; record the package/adapter owner. Check `S05,S06,S09` gate and the current source API/contract version.
2. **Concrete implementation:** Implement default encrypted local key-wrapping provider with securely persistent rotated master key policy. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Treat as optional packages; consume authorized safe projections or DEK wrapping only, not global journal scans or plaintext restore. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify successful integration with canonical contract fixtures. Inject malformed input, cancellation and unavailable dependency; assert typed error/degraded behavior.
5. **Acceptance artifact:** Outage without core regression, tenant isolation, deletion/tombstone and KMS key rotation. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-EXT-008` only after verified pass.

#### OC2-D-EXT-009 — Provide optional Infisical KMS adapter strictly for KEK wrap/unwrap; never export original PII or vault mappings.

1. **Repository and prerequisites:** Work in `src/OpenConnect.QdrantVactor/, src/OpenConnect.Cryptography.Infisical/` or the existing equivalent; record the package/adapter owner. Check `S05,S06,S09` gate and the current source API/contract version.
2. **Concrete implementation:** Provide optional Infisical KMS adapter strictly for KEK wrap/unwrap; never export original PII or vault mappings. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Treat as optional packages; consume authorized safe projections or DEK wrapping only, not global journal scans or plaintext restore. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Exercise authorized and explicitly unauthorized principals/tenants; assert no side effect on denial. Verify no credentials, plaintext or raw user content leave the intended trust boundary.
5. **Acceptance artifact:** Outage without core regression, tenant isolation, deletion/tombstone and KMS key rotation. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-EXT-009` only after verified pass.

#### OC2-D-EXT-010 — Validate key rotation, unwrap outage, cache TTL, scope binding and inability to restore across tenants.

1. **Repository and prerequisites:** Work in `src/OpenConnect.QdrantVactor/, src/OpenConnect.Cryptography.Infisical/` or the existing equivalent; record the package/adapter owner. Check `S05,S06,S09` gate and the current source API/contract version.
2. **Concrete implementation:** Validate key rotation, unwrap outage, cache TTL, scope binding and inability to restore across tenants. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Treat as optional packages; consume authorized safe projections or DEK wrapping only, not global journal scans or plaintext restore. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Exercise authorized and explicitly unauthorized principals/tenants; assert no side effect on denial. Verify no credentials, plaintext or raw user content leave the intended trust boundary.
5. **Acceptance artifact:** Outage without core regression, tenant isolation, deletion/tombstone and KMS key rotation. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-EXT-010` only after verified pass.

#### OC2-D-EXT-011 — Test extension registration/unregistration has no mandatory DB/worker/transport dependency leak.

1. **Repository and prerequisites:** Work in `src/OpenConnect.QdrantVactor/, src/OpenConnect.Cryptography.Infisical/` or the existing equivalent; record the package/adapter owner. Check `S05,S06,S09` gate and the current source API/contract version.
2. **Concrete implementation:** Test extension registration/unregistration has no mandatory DB/worker/transport dependency leak. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Treat as optional packages; consume authorized safe projections or DEK wrapping only, not global journal scans or plaintext restore. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify disabled/uninstalled, ready, startup failure and shutdown behavior independently. Validate model/asset manifest hash, license, actual capabilities and bounded resources where applicable.
5. **Acceptance artifact:** Outage without core regression, tenant isolation, deletion/tombstone and KMS key rotation. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-EXT-011` only after verified pass.


### S16 — Telemetry, financial metering, evaluations and admin

**Prerequisites:** `S03,S06,S10`. **Checklist section:** 3.17 Observability, financial metering, evaluation, budgets and operator UX. **Scope:** 17 atomic deliverables. **Primary source paths:** `src/OpenConnect.Observability/, src/OpenConnect.Metering/, tests/Evaluation/`.

**Authority:** Avoid duplicate spans, keep provider attempts independent of sampling, derive costs with versioned decimal pricing and approved datasets. **Required verification pattern:** Canary-free telemetry, failed exporter, duplicate attempts, budgets/evaluations and RBAC.

#### OC2-D-OBS-001 — Use existing MAF instrumentation and add OpenConnect ActivitySources/Meters only for application-specific stages; avoid duplicate spans.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Observability/, src/OpenConnect.Metering/, tests/Evaluation/` or the existing equivalent; record the package/adapter owner. Check `S03,S06,S10` gate and the current source API/contract version.
2. **Concrete implementation:** Use existing MAF instrumentation and add OpenConnect ActivitySources/Meters only for application-specific stages; avoid duplicate spans. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Avoid duplicate spans, keep provider attempts independent of sampling, derive costs with versioned decimal pricing and approved datasets. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Invoke an actual pinned MAF type and inspect real lifecycle/session data. Prove unsupported versions/profiles report unavailable instead of fabricating equivalent behavior.
5. **Acceptance artifact:** Canary-free telemetry, failed exporter, duplicate attempts, budgets/evaluations and RBAC. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-OBS-001` only after verified pass.

#### OC2-D-OBS-002 — Implement W3C trace context over approved HTTP/JetStream metadata and agent/harness/tool/workflow lineage.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Observability/, src/OpenConnect.Metering/, tests/Evaluation/` or the existing equivalent; record the package/adapter owner. Check `S03,S06,S10` gate and the current source API/contract version.
2. **Concrete implementation:** Implement W3C trace context over approved HTTP/JetStream metadata and agent/harness/tool/workflow lineage. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Avoid duplicate spans, keep provider attempts independent of sampling, derive costs with versioned decimal pricing and approved datasets. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Invoke an actual pinned MAF type and inspect real lifecycle/session data. Prove unsupported versions/profiles report unavailable instead of fabricating equivalent behavior.
5. **Acceptance artifact:** Canary-free telemetry, failed exporter, duplicate attempts, budgets/evaluations and RBAC. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-OBS-002` only after verified pass.

#### OC2-D-OBS-003 — Use metadata-only default for all logs/traces/metrics with bounded-cardinality dimensions and synthetic canary tests.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Observability/, src/OpenConnect.Metering/, tests/Evaluation/` or the existing equivalent; record the package/adapter owner. Check `S03,S06,S10` gate and the current source API/contract version.
2. **Concrete implementation:** Use metadata-only default for all logs/traces/metrics with bounded-cardinality dimensions and synthetic canary tests. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Avoid duplicate spans, keep provider attempts independent of sampling, derive costs with versioned decimal pricing and approved datasets. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Run targeted success and deliberate negative regression in CI. Save machine-readable test artifacts and versioned audit evidence.
5. **Acceptance artifact:** Canary-free telemetry, failed exporter, duplicate attempts, budgets/evaluations and RBAC. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-OBS-003` only after verified pass.

#### OC2-D-OBS-004 — Instrument detector state Disabled/NotInstalled/Executed/Failed/TimedOut and measured firewall queue/latency separately.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Observability/, src/OpenConnect.Metering/, tests/Evaluation/` or the existing equivalent; record the package/adapter owner. Check `S03,S06,S10` gate and the current source API/contract version.
2. **Concrete implementation:** Instrument detector state Disabled/NotInstalled/Executed/Failed/TimedOut and measured firewall queue/latency separately. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Avoid duplicate spans, keep provider attempts independent of sampling, derive costs with versioned decimal pricing and approved datasets. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify successful integration with canonical contract fixtures. Inject malformed input, cancellation and unavailable dependency; assert typed error/degraded behavior.
5. **Acceptance artifact:** Canary-free telemetry, failed exporter, duplicate attempts, budgets/evaluations and RBAC. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-OBS-004` only after verified pass.

#### OC2-D-OBS-005 — Implement durable model attempt ledger outside OTLP/sampled traces with provider reported/estimated/unknown provenance.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Observability/, src/OpenConnect.Metering/, tests/Evaluation/` or the existing equivalent; record the package/adapter owner. Check `S03,S06,S10` gate and the current source API/contract version.
2. **Concrete implementation:** Implement durable model attempt ledger outside OTLP/sampled traces with provider reported/estimated/unknown provenance. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Avoid duplicate spans, keep provider attempts independent of sampling, derive costs with versioned decimal pricing and approved datasets. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify successful integration with canonical contract fixtures. Inject malformed input, cancellation and unavailable dependency; assert typed error/degraded behavior.
5. **Acceptance artifact:** Canary-free telemetry, failed exporter, duplicate attempts, budgets/evaluations and RBAC. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-OBS-005` only after verified pass.

#### OC2-D-OBS-006 — Record pricing catalog with immutable effective dates, decimal rates, cache/reasoning modality overlap and currency provenance.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Observability/, src/OpenConnect.Metering/, tests/Evaluation/` or the existing equivalent; record the package/adapter owner. Check `S03,S06,S10` gate and the current source API/contract version.
2. **Concrete implementation:** Record pricing catalog with immutable effective dates, decimal rates, cache/reasoning modality overlap and currency provenance. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Avoid duplicate spans, keep provider attempts independent of sampling, derive costs with versioned decimal pricing and approved datasets. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Prove commit/rollback behavior against an actual supported relational provider. Inject lost ACK, stale fencing token and concurrent writer where the contract touches durable state.
5. **Acceptance artifact:** Canary-free telemetry, failed exporter, duplicate attempts, budgets/evaluations and RBAC. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-OBS-006` only after verified pass.

#### OC2-D-OBS-007 — Record retries, streaming cancellation, unknown usage and late provider correction without double billing.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Observability/, src/OpenConnect.Metering/, tests/Evaluation/` or the existing equivalent; record the package/adapter owner. Check `S03,S06,S10` gate and the current source API/contract version.
2. **Concrete implementation:** Record retries, streaming cancellation, unknown usage and late provider correction without double billing. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Avoid duplicate spans, keep provider attempts independent of sampling, derive costs with versioned decimal pricing and approved datasets. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify successful integration with canonical contract fixtures. Inject malformed input, cancellation and unavailable dependency; assert typed error/degraded behavior.
5. **Acceptance artifact:** Canary-free telemetry, failed exporter, duplicate attempts, budgets/evaluations and RBAC. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-OBS-007` only after verified pass.

#### OC2-D-OBS-008 — Implement hard budget reservation/settlement transaction and cost-aware model routing thresholds with safe unknown-cost policy.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Observability/, src/OpenConnect.Metering/, tests/Evaluation/` or the existing equivalent; record the package/adapter owner. Check `S03,S06,S10` gate and the current source API/contract version.
2. **Concrete implementation:** Implement hard budget reservation/settlement transaction and cost-aware model routing thresholds with safe unknown-cost policy. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Avoid duplicate spans, keep provider attempts independent of sampling, derive costs with versioned decimal pricing and approved datasets. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Prove commit/rollback behavior against an actual supported relational provider. Inject lost ACK, stale fencing token and concurrent writer where the contract touches durable state.
5. **Acceptance artifact:** Canary-free telemetry, failed exporter, duplicate attempts, budgets/evaluations and RBAC. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-OBS-008` only after verified pass.

#### OC2-D-OBS-009 — Distinguish local CPU/GPU infrastructure estimates from actual externally billed charges in every admin report.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Observability/, src/OpenConnect.Metering/, tests/Evaluation/` or the existing equivalent; record the package/adapter owner. Check `S03,S06,S10` gate and the current source API/contract version.
2. **Concrete implementation:** Distinguish local CPU/GPU infrastructure estimates from actual externally billed charges in every admin report. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Avoid duplicate spans, keep provider attempts independent of sampling, derive costs with versioned decimal pricing and approved datasets. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify successful integration with canonical contract fixtures. Inject malformed input, cancellation and unavailable dependency; assert typed error/degraded behavior.
5. **Acceptance artifact:** Canary-free telemetry, failed exporter, duplicate attempts, budgets/evaluations and RBAC. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-OBS-009` only after verified pass.

#### OC2-D-OBS-010 — Implement native MAF evaluation harness using Microsoft.Extensions.AI.Evaluation where supported; avoid custom evaluator engine.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Observability/, src/OpenConnect.Metering/, tests/Evaluation/` or the existing equivalent; record the package/adapter owner. Check `S03,S06,S10` gate and the current source API/contract version.
2. **Concrete implementation:** Implement native MAF evaluation harness using Microsoft.Extensions.AI.Evaluation where supported; avoid custom evaluator engine. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Avoid duplicate spans, keep provider attempts independent of sampling, derive costs with versioned decimal pricing and approved datasets. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Invoke an actual pinned MAF type and inspect real lifecycle/session data. Prove unsupported versions/profiles report unavailable instead of fabricating equivalent behavior.
5. **Acceptance artifact:** Canary-free telemetry, failed exporter, duplicate attempts, budgets/evaluations and RBAC. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-OBS-010` only after verified pass.

#### OC2-D-OBS-011 — Create reproducible labeled eval sets for tool choice, grounding, instruction hierarchy, workflow outcome, privacy and injection.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Observability/, src/OpenConnect.Metering/, tests/Evaluation/` or the existing equivalent; record the package/adapter owner. Check `S03,S06,S10` gate and the current source API/contract version.
2. **Concrete implementation:** Create reproducible labeled eval sets for tool choice, grounding, instruction hierarchy, workflow outcome, privacy and injection. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Avoid duplicate spans, keep provider attempts independent of sampling, derive costs with versioned decimal pricing and approved datasets. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Exercise authorized and explicitly unauthorized principals/tenants; assert no side effect on denial. Verify no credentials, plaintext or raw user content leave the intended trust boundary.
5. **Acceptance artifact:** Canary-free telemetry, failed exporter, duplicate attempts, budgets/evaluations and RBAC. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-OBS-011` only after verified pass.

#### OC2-D-OBS-012 — Track eval dataset version, model checkpoint, run config, metrics, CI trends and test evidence—not unsupported global accuracy claim.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Observability/, src/OpenConnect.Metering/, tests/Evaluation/` or the existing equivalent; record the package/adapter owner. Check `S03,S06,S10` gate and the current source API/contract version.
2. **Concrete implementation:** Track eval dataset version, model checkpoint, run config, metrics, CI trends and test evidence—not unsupported global accuracy claim. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Avoid duplicate spans, keep provider attempts independent of sampling, derive costs with versioned decimal pricing and approved datasets. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify disabled/uninstalled, ready, startup failure and shutdown behavior independently. Validate model/asset manifest hash, license, actual capabilities and bounded resources where applicable.
5. **Acceptance artifact:** Canary-free telemetry, failed exporter, duplicate attempts, budgets/evaluations and RBAC. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-OBS-012` only after verified pass.

#### OC2-D-OBS-013 — Provide authenticated tenant/operator Admin API for workflow cost, reliability, firewall and budgets with strict RBAC.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Observability/, src/OpenConnect.Metering/, tests/Evaluation/` or the existing equivalent; record the package/adapter owner. Check `S03,S06,S10` gate and the current source API/contract version.
2. **Concrete implementation:** Provide authenticated tenant/operator Admin API for workflow cost, reliability, firewall and budgets with strict RBAC. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Avoid duplicate spans, keep provider attempts independent of sampling, derive costs with versioned decimal pricing and approved datasets. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Exercise authorized and explicitly unauthorized principals/tenants; assert no side effect on denial. Verify no credentials, plaintext or raw user content leave the intended trust boundary.
5. **Acceptance artifact:** Canary-free telemetry, failed exporter, duplicate attempts, budgets/evaluations and RBAC. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-OBS-013` only after verified pass.

#### OC2-D-OBS-014 — Provide optional embeddable admin UI without turning it into another official public OpenConnect application.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Observability/, src/OpenConnect.Metering/, tests/Evaluation/` or the existing equivalent; record the package/adapter owner. Check `S03,S06,S10` gate and the current source API/contract version.
2. **Concrete implementation:** Provide optional embeddable admin UI without turning it into another official public OpenConnect application. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Avoid duplicate spans, keep provider attempts independent of sampling, derive costs with versioned decimal pricing and approved datasets. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Run targeted success and deliberate negative regression in CI. Save machine-readable test artifacts and versioned audit evidence.
5. **Acceptance artifact:** Canary-free telemetry, failed exporter, duplicate attempts, budgets/evaluations and RBAC. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-OBS-014` only after verified pass.

#### OC2-D-OBS-015 — Integrate optional OpenTelemetry OTLP exporter and .NET Aspire dashboard/AppHost without making either mandatory.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Observability/, src/OpenConnect.Metering/, tests/Evaluation/` or the existing equivalent; record the package/adapter owner. Check `S03,S06,S10` gate and the current source API/contract version.
2. **Concrete implementation:** Integrate optional OpenTelemetry OTLP exporter and .NET Aspire dashboard/AppHost without making either mandatory. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Avoid duplicate spans, keep provider attempts independent of sampling, derive costs with versioned decimal pricing and approved datasets. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify successful integration with canonical contract fixtures. Inject malformed input, cancellation and unavailable dependency; assert typed error/degraded behavior.
5. **Acceptance artifact:** Canary-free telemetry, failed exporter, duplicate attempts, budgets/evaluations and RBAC. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-OBS-015` only after verified pass.

#### OC2-D-OBS-016 — Fault-inject OTLP outage and dashboard disconnection; gateway firewall and durable cost ledger continue independently.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Observability/, src/OpenConnect.Metering/, tests/Evaluation/` or the existing equivalent; record the package/adapter owner. Check `S03,S06,S10` gate and the current source API/contract version.
2. **Concrete implementation:** Fault-inject OTLP outage and dashboard disconnection; gateway firewall and durable cost ledger continue independently. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Avoid duplicate spans, keep provider attempts independent of sampling, derive costs with versioned decimal pricing and approved datasets. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify successful integration with canonical contract fixtures. Inject malformed input, cancellation and unavailable dependency; assert typed error/degraded behavior.
5. **Acceptance artifact:** Canary-free telemetry, failed exporter, duplicate attempts, budgets/evaluations and RBAC. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-OBS-016` only after verified pass.

#### OC2-D-OBS-017 — Prove alerts, retention, trace links and export/pagination cannot reveal another tenant's data.

1. **Repository and prerequisites:** Work in `src/OpenConnect.Observability/, src/OpenConnect.Metering/, tests/Evaluation/` or the existing equivalent; record the package/adapter owner. Check `S03,S06,S10` gate and the current source API/contract version.
2. **Concrete implementation:** Prove alerts, retention, trace links and export/pagination cannot reveal another tenant's data. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Avoid duplicate spans, keep provider attempts independent of sampling, derive costs with versioned decimal pricing and approved datasets. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Exercise authorized and explicitly unauthorized principals/tenants; assert no side effect on denial. Verify no credentials, plaintext or raw user content leave the intended trust boundary.
5. **Acceptance artifact:** Canary-free telemetry, failed exporter, duplicate attempts, budgets/evaluations and RBAC. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-OBS-017` only after verified pass.


### S17 — Cross-framework compatibility and clean consumers

**Prerequisites:** `S01-S16`. **Checklist section:** 3.18 Cross-framework .NET 8/9/10 build, EF dependencies and runtime testing. **Scope:** 13 atomic deliverables. **Primary source paths:** `Directory.Build.props, Directory.Packages.props, tests/PackageConsumers/`.

**Authority:** Resolve compatible exact TFM dependency groups, compile once per TFM and execute under each matching .NET runtime. **Required verification pattern:** Clean net8/net9/net10 package consumers and MAF/EF provider compatibility checks.

#### OC2-D-TFM-001 — Multi-target all public .NET libraries for net8.0, net9.0 and net10.0 only where pinned dependencies support each target.

1. **Repository and prerequisites:** Work in `Directory.Build.props, Directory.Packages.props, tests/PackageConsumers/` or the existing equivalent; record the package/adapter owner. Check `S01-S16` gate and the current source API/contract version.
2. **Concrete implementation:** Multi-target all public .NET libraries for net8.0, net9.0 and net10.0 only where pinned dependencies support each target. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Resolve compatible exact TFM dependency groups, compile once per TFM and execute under each matching .NET runtime. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Run targeted success and deliberate negative regression in CI. Save machine-readable test artifacts and versioned audit evidence. Build and run matching TFM consumers on net8.0/net9.0/net10.0 as claimed, with EOS compatibility labels.
5. **Acceptance artifact:** Clean net8/net9/net10 package consumers and MAF/EF provider compatibility checks. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-TFM-001` only after verified pass.

#### OC2-D-TFM-002 — Pin exact centrally managed dependency versions and target-conditional references for EF Core 8/9/10 where required.

1. **Repository and prerequisites:** Work in `Directory.Build.props, Directory.Packages.props, tests/PackageConsumers/` or the existing equivalent; record the package/adapter owner. Check `S01-S16` gate and the current source API/contract version.
2. **Concrete implementation:** Pin exact centrally managed dependency versions and target-conditional references for EF Core 8/9/10 where required. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Resolve compatible exact TFM dependency groups, compile once per TFM and execute under each matching .NET runtime. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Prove commit/rollback behavior against an actual supported relational provider. Inject lost ACK, stale fencing token and concurrent writer where the contract touches durable state.
5. **Acceptance artifact:** Clean net8/net9/net10 package consumers and MAF/EF provider compatibility checks. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-TFM-002` only after verified pass.

#### OC2-D-TFM-003 — Pin actual MAF/Harness/Durable/MCP/MudBlazor compatibility for each TFM or declare individual feature unavailable before packaging.

1. **Repository and prerequisites:** Work in `Directory.Build.props, Directory.Packages.props, tests/PackageConsumers/` or the existing equivalent; record the package/adapter owner. Check `S01-S16` gate and the current source API/contract version.
2. **Concrete implementation:** Pin actual MAF/Harness/Durable/MCP/MudBlazor compatibility for each TFM or declare individual feature unavailable before packaging. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Resolve compatible exact TFM dependency groups, compile once per TFM and execute under each matching .NET runtime. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Invoke an actual pinned MAF type and inspect real lifecycle/session data. Prove unsupported versions/profiles report unavailable instead of fabricating equivalent behavior. Verify unregistered/malicious MCP tool schemas cannot expand permissions, redirect network access or bypass approval.
5. **Acceptance artifact:** Clean net8/net9/net10 package consumers and MAF/EF provider compatibility checks. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-TFM-003` only after verified pass.

#### OC2-D-TFM-004 — Verify public API parity across TFMs with baseline assembly/API compatibility and no accidental conditional member loss.

1. **Repository and prerequisites:** Work in `Directory.Build.props, Directory.Packages.props, tests/PackageConsumers/` or the existing equivalent; record the package/adapter owner. Check `S01-S16` gate and the current source API/contract version.
2. **Concrete implementation:** Verify public API parity across TFMs with baseline assembly/API compatibility and no accidental conditional member loss. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Resolve compatible exact TFM dependency groups, compile once per TFM and execute under each matching .NET runtime. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Run targeted success and deliberate negative regression in CI. Save machine-readable test artifacts and versioned audit evidence.
5. **Acceptance artifact:** Clean net8/net9/net10 package consumers and MAF/EF provider compatibility checks. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-TFM-004` only after verified pass.

#### OC2-D-TFM-005 — Run net8 targeted test assemblies under installed .NET 8 runtime, not merely compile using .NET 10 SDK.

1. **Repository and prerequisites:** Work in `Directory.Build.props, Directory.Packages.props, tests/PackageConsumers/` or the existing equivalent; record the package/adapter owner. Check `S01-S16` gate and the current source API/contract version.
2. **Concrete implementation:** Run net8 targeted test assemblies under installed .NET 8 runtime, not merely compile using .NET 10 SDK. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Resolve compatible exact TFM dependency groups, compile once per TFM and execute under each matching .NET runtime. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Run targeted success and deliberate negative regression in CI. Save machine-readable test artifacts and versioned audit evidence. Build and run matching TFM consumers on net8.0/net9.0/net10.0 as claimed, with EOS compatibility labels.
5. **Acceptance artifact:** Clean net8/net9/net10 package consumers and MAF/EF provider compatibility checks. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-TFM-005` only after verified pass.

#### OC2-D-TFM-006 — Run net9 targeted test assemblies under installed .NET 9 runtime, not merely compile using .NET 10 SDK.

1. **Repository and prerequisites:** Work in `Directory.Build.props, Directory.Packages.props, tests/PackageConsumers/` or the existing equivalent; record the package/adapter owner. Check `S01-S16` gate and the current source API/contract version.
2. **Concrete implementation:** Run net9 targeted test assemblies under installed .NET 9 runtime, not merely compile using .NET 10 SDK. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Resolve compatible exact TFM dependency groups, compile once per TFM and execute under each matching .NET runtime. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Run targeted success and deliberate negative regression in CI. Save machine-readable test artifacts and versioned audit evidence. Build and run matching TFM consumers on net8.0/net9.0/net10.0 as claimed, with EOS compatibility labels.
5. **Acceptance artifact:** Clean net8/net9/net10 package consumers and MAF/EF provider compatibility checks. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-TFM-006` only after verified pass.

#### OC2-D-TFM-007 — Run net10 targeted test assemblies under installed .NET 10 runtime, not merely compile using .NET 10 SDK.

1. **Repository and prerequisites:** Work in `Directory.Build.props, Directory.Packages.props, tests/PackageConsumers/` or the existing equivalent; record the package/adapter owner. Check `S01-S16` gate and the current source API/contract version.
2. **Concrete implementation:** Run net10 targeted test assemblies under installed .NET 10 runtime, not merely compile using .NET 10 SDK. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Resolve compatible exact TFM dependency groups, compile once per TFM and execute under each matching .NET runtime. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Run targeted success and deliberate negative regression in CI. Save machine-readable test artifacts and versioned audit evidence. Build and run matching TFM consumers on net8.0/net9.0/net10.0 as claimed, with EOS compatibility labels.
5. **Acceptance artifact:** Clean net8/net9/net10 package consumers and MAF/EF provider compatibility checks. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-TFM-007` only after verified pass.

#### OC2-D-TFM-008 — Run EF SQLServer/PostgreSQL/SQLite provider matrix on representative TFM-compatible EF provider majors.

1. **Repository and prerequisites:** Work in `Directory.Build.props, Directory.Packages.props, tests/PackageConsumers/` or the existing equivalent; record the package/adapter owner. Check `S01-S16` gate and the current source API/contract version.
2. **Concrete implementation:** Run EF SQLServer/PostgreSQL/SQLite provider matrix on representative TFM-compatible EF provider majors. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Resolve compatible exact TFM dependency groups, compile once per TFM and execute under each matching .NET runtime. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Prove commit/rollback behavior against an actual supported relational provider. Inject lost ACK, stale fencing token and concurrent writer where the contract touches durable state.
5. **Acceptance artifact:** Clean net8/net9/net10 package consumers and MAF/EF provider compatibility checks. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-TFM-008` only after verified pass.

#### OC2-D-TFM-009 — Run real Blazor server/WASM and OpenConnect.Client smoke tests on each advertised compatible TFM.

1. **Repository and prerequisites:** Work in `Directory.Build.props, Directory.Packages.props, tests/PackageConsumers/` or the existing equivalent; record the package/adapter owner. Check `S01-S16` gate and the current source API/contract version.
2. **Concrete implementation:** Run real Blazor server/WASM and OpenConnect.Client smoke tests on each advertised compatible TFM. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Resolve compatible exact TFM dependency groups, compile once per TFM and execute under each matching .NET runtime. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Run targeted success and deliberate negative regression in CI. Save machine-readable test artifacts and versioned audit evidence.
5. **Acceptance artifact:** Clean net8/net9/net10 package consumers and MAF/EF provider compatibility checks. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-TFM-009` only after verified pass.

#### OC2-D-TFM-010 — Build standalone .NET worker images on supported runtime (default net10) and verify wire interoperability with net8/9 libraries.

1. **Repository and prerequisites:** Work in `Directory.Build.props, Directory.Packages.props, tests/PackageConsumers/` or the existing equivalent; record the package/adapter owner. Check `S01-S16` gate and the current source API/contract version.
2. **Concrete implementation:** Build standalone .NET worker images on supported runtime (default net10) and verify wire interoperability with net8/9 libraries. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Resolve compatible exact TFM dependency groups, compile once per TFM and execute under each matching .NET runtime. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify disabled/uninstalled, ready, startup failure and shutdown behavior independently. Validate model/asset manifest hash, license, actual capabilities and bounded resources where applicable. Build and run matching TFM consumers on net8.0/net9.0/net10.0 as claimed, with EOS compatibility labels.
5. **Acceptance artifact:** Clean net8/net9/net10 package consumers and MAF/EF provider compatibility checks. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-TFM-010` only after verified pass.

#### OC2-D-TFM-011 — Document .NET 8/9 vendor end-of-support as compatibility-only and .NET 10 production recommendation.

1. **Repository and prerequisites:** Work in `Directory.Build.props, Directory.Packages.props, tests/PackageConsumers/` or the existing equivalent; record the package/adapter owner. Check `S01-S16` gate and the current source API/contract version.
2. **Concrete implementation:** Document .NET 8/9 vendor end-of-support as compatibility-only and .NET 10 production recommendation. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Resolve compatible exact TFM dependency groups, compile once per TFM and execute under each matching .NET runtime. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Validate generated/readme/code snippet claims against built assemblies or referenced source. Detect references to superseded APIs, stale paths and unsupported capabilities.
5. **Acceptance artifact:** Clean net8/net9/net10 package consumers and MAF/EF provider compatibility checks. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-TFM-011` only after verified pass.

#### OC2-D-TFM-012 — Verify single NuGet package per library contains lib/net8.0, lib/net9.0, lib/net10.0 and correct framework-specific dependency groups.

1. **Repository and prerequisites:** Work in `Directory.Build.props, Directory.Packages.props, tests/PackageConsumers/` or the existing equivalent; record the package/adapter owner. Check `S01-S16` gate and the current source API/contract version.
2. **Concrete implementation:** Verify single NuGet package per library contains lib/net8.0, lib/net9.0, lib/net10.0 and correct framework-specific dependency groups. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Resolve compatible exact TFM dependency groups, compile once per TFM and execute under each matching .NET runtime. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Run targeted success and deliberate negative regression in CI. Save machine-readable test artifacts and versioned audit evidence. Build and run matching TFM consumers on net8.0/net9.0/net10.0 as claimed, with EOS compatibility labels.
5. **Acceptance artifact:** Clean net8/net9/net10 package consumers and MAF/EF provider compatibility checks. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-TFM-012` only after verified pass.

#### OC2-D-TFM-013 — Run clean third-party app package consumers on all advertised TFMs with no source project references.

1. **Repository and prerequisites:** Work in `Directory.Build.props, Directory.Packages.props, tests/PackageConsumers/` or the existing equivalent; record the package/adapter owner. Check `S01-S16` gate and the current source API/contract version.
2. **Concrete implementation:** Run clean third-party app package consumers on all advertised TFMs with no source project references. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Resolve compatible exact TFM dependency groups, compile once per TFM and execute under each matching .NET runtime. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify successful integration with canonical contract fixtures. Inject malformed input, cancellation and unavailable dependency; assert typed error/degraded behavior.
5. **Acceptance artifact:** Clean net8/net9/net10 package consumers and MAF/EF provider compatibility checks. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-TFM-013` only after verified pass.


### S18 — Release pipelines and documentation

**Prerequisites:** `S01-S17`. **Checklist section:** 3.19 GitHub Actions, 11-image Docker Hub publication, NuGet/npm and documentation. **Scope:** 27 atomic deliverables. **Primary source paths:** `.github/workflows/, scripts/release/, site/, examples/PackageConsumers/`.

**Authority:** Use protected tags, pinned actions, independently built/tested 11 images; promote :latest only after all versioned digests verify. **Required verification pattern:** No PR publishing, registry clean pulls by digest, SBOM/rollback, doc offline smoke.

#### OC2-D-REL-001 — Implement protected branch PR checks for locked dotnet restore/build/test, MAF native integration, format and warning-as-error.

1. **Repository and prerequisites:** Work in `.github/workflows/, scripts/release/, site/, examples/PackageConsumers/` or the existing equivalent; record the package/adapter owner. Check `S01-S17` gate and the current source API/contract version.
2. **Concrete implementation:** Implement protected branch PR checks for locked dotnet restore/build/test, MAF native integration, format and warning-as-error. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Use protected tags, pinned actions, independently built/tested 11 images; promote :latest only after all versioned digests verify. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Invoke an actual pinned MAF type and inspect real lifecycle/session data. Prove unsupported versions/profiles report unavailable instead of fabricating equivalent behavior.
5. **Acceptance artifact:** No PR publishing, registry clean pulls by digest, SBOM/rollback, doc offline smoke. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-REL-001` only after verified pass.

#### OC2-D-REL-002 — Implement per-TFM net8/net9/net10 runtime test matrix with net8/9 compatibility only where supported.

1. **Repository and prerequisites:** Work in `.github/workflows/, scripts/release/, site/, examples/PackageConsumers/` or the existing equivalent; record the package/adapter owner. Check `S01-S17` gate and the current source API/contract version.
2. **Concrete implementation:** Implement per-TFM net8/net9/net10 runtime test matrix with net8/9 compatibility only where supported. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Use protected tags, pinned actions, independently built/tested 11 images; promote :latest only after all versioned digests verify. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Run targeted success and deliberate negative regression in CI. Save machine-readable test artifacts and versioned audit evidence. Build and run matching TFM consumers on net8.0/net9.0/net10.0 as claimed, with EOS compatibility labels.
5. **Acceptance artifact:** No PR publishing, registry clean pulls by digest, SBOM/rollback, doc offline smoke. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-REL-002` only after verified pass.

#### OC2-D-REL-003 — Implement provider-specific SQL Server/Postgres/SQLite integration CI with Testcontainers and NATS crash/replay fault tests.

1. **Repository and prerequisites:** Work in `.github/workflows/, scripts/release/, site/, examples/PackageConsumers/` or the existing equivalent; record the package/adapter owner. Check `S01-S17` gate and the current source API/contract version.
2. **Concrete implementation:** Implement provider-specific SQL Server/Postgres/SQLite integration CI with Testcontainers and NATS crash/replay fault tests. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Use protected tags, pinned actions, independently built/tested 11 images; promote :latest only after all versioned digests verify. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Prove commit/rollback behavior against an actual supported relational provider. Inject lost ACK, stale fencing token and concurrent writer where the contract touches durable state.
5. **Acceptance artifact:** No PR publishing, registry clean pulls by digest, SBOM/rollback, doc offline smoke. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-REL-003` only after verified pass.

#### OC2-D-REL-004 — Implement Python uv/Ruff/mypy/pytest worker matrix and Dockerfile builds for each of eight Python images.

1. **Repository and prerequisites:** Work in `.github/workflows/, scripts/release/, site/, examples/PackageConsumers/` or the existing equivalent; record the package/adapter owner. Check `S01-S17` gate and the current source API/contract version.
2. **Concrete implementation:** Implement Python uv/Ruff/mypy/pytest worker matrix and Dockerfile builds for each of eight Python images. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Use protected tags, pinned actions, independently built/tested 11 images; promote :latest only after all versioned digests verify. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify disabled/uninstalled, ready, startup failure and shutdown behavior independently. Validate model/asset manifest hash, license, actual capabilities and bounded resources where applicable.
5. **Acceptance artifact:** No PR publishing, registry clean pulls by digest, SBOM/rollback, doc offline smoke. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-REL-004` only after verified pass.

#### OC2-D-REL-005 — Implement Node Playwright worker image build, browser security and CVE scanning.

1. **Repository and prerequisites:** Work in `.github/workflows/, scripts/release/, site/, examples/PackageConsumers/` or the existing equivalent; record the package/adapter owner. Check `S01-S17` gate and the current source API/contract version.
2. **Concrete implementation:** Implement Node Playwright worker image build, browser security and CVE scanning. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Use protected tags, pinned actions, independently built/tested 11 images; promote :latest only after all versioned digests verify. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Exercise authorized and explicitly unauthorized principals/tenants; assert no side effect on denial. Verify no credentials, plaintext or raw user content leave the intended trust boundary.
5. **Acceptance artifact:** No PR publishing, registry clean pulls by digest, SBOM/rollback, doc offline smoke. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-REL-005` only after verified pass.

#### OC2-D-REL-006 — Implement two standalone .NET worker-host Docker image builds and in-process/standalone ownership smoke.

1. **Repository and prerequisites:** Work in `.github/workflows/, scripts/release/, site/, examples/PackageConsumers/` or the existing equivalent; record the package/adapter owner. Check `S01-S17` gate and the current source API/contract version.
2. **Concrete implementation:** Implement two standalone .NET worker-host Docker image builds and in-process/standalone ownership smoke. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Use protected tags, pinned actions, independently built/tested 11 images; promote :latest only after all versioned digests verify. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify disabled/uninstalled, ready, startup failure and shutdown behavior independently. Validate model/asset manifest hash, license, actual capabilities and bounded resources where applicable.
5. **Acceptance artifact:** No PR publishing, registry clean pulls by digest, SBOM/rollback, doc offline smoke. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-REL-006` only after verified pass.

#### OC2-D-REL-007 — Implement client TypeScript/Node/browser/React and Blazor bUnit/Playwright screenshot/a11y CI jobs.

1. **Repository and prerequisites:** Work in `.github/workflows/, scripts/release/, site/, examples/PackageConsumers/` or the existing equivalent; record the package/adapter owner. Check `S01-S17` gate and the current source API/contract version.
2. **Concrete implementation:** Implement client TypeScript/Node/browser/React and Blazor bUnit/Playwright screenshot/a11y CI jobs. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Use protected tags, pinned actions, independently built/tested 11 images; promote :latest only after all versioned digests verify. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Run targeted success and deliberate negative regression in CI. Save machine-readable test artifacts and versioned audit evidence.
5. **Acceptance artifact:** No PR publishing, registry clean pulls by digest, SBOM/rollback, doc offline smoke. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-REL-007` only after verified pass.

#### OC2-D-REL-008 — Implement C#/TS/Python OpenAPI/JSON Schema golden contract parity and breaking-change release gates.

1. **Repository and prerequisites:** Work in `.github/workflows/, scripts/release/, site/, examples/PackageConsumers/` or the existing equivalent; record the package/adapter owner. Check `S01-S17` gate and the current source API/contract version.
2. **Concrete implementation:** Implement C#/TS/Python OpenAPI/JSON Schema golden contract parity and breaking-change release gates. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Use protected tags, pinned actions, independently built/tested 11 images; promote :latest only after all versioned digests verify. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Prove commit/rollback behavior against an actual supported relational provider. Inject lost ACK, stale fencing token and concurrent writer where the contract touches durable state.
5. **Acceptance artifact:** No PR publishing, registry clean pulls by digest, SBOM/rollback, doc offline smoke. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-REL-008` only after verified pass.

#### OC2-D-REL-009 — Implement MAF native Harness/Workflow/Durable proof tests with pinned dependency snapshot in CI.

1. **Repository and prerequisites:** Work in `.github/workflows/, scripts/release/, site/, examples/PackageConsumers/` or the existing equivalent; record the package/adapter owner. Check `S01-S17` gate and the current source API/contract version.
2. **Concrete implementation:** Implement MAF native Harness/Workflow/Durable proof tests with pinned dependency snapshot in CI. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Use protected tags, pinned actions, independently built/tested 11 images; promote :latest only after all versioned digests verify. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Invoke an actual pinned MAF type and inspect real lifecycle/session data. Prove unsupported versions/profiles report unavailable instead of fabricating equivalent behavior.
5. **Acceptance artifact:** No PR publishing, registry clean pulls by digest, SBOM/rollback, doc offline smoke. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-REL-009` only after verified pass.

#### OC2-D-REL-010 — Implement firewall/security/adversarial canary suites for worker, tool, MCP, session memory and UI leakage.

1. **Repository and prerequisites:** Work in `.github/workflows/, scripts/release/, site/, examples/PackageConsumers/` or the existing equivalent; record the package/adapter owner. Check `S01-S17` gate and the current source API/contract version.
2. **Concrete implementation:** Implement firewall/security/adversarial canary suites for worker, tool, MCP, session memory and UI leakage. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Use protected tags, pinned actions, independently built/tested 11 images; promote :latest only after all versioned digests verify. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Exercise authorized and explicitly unauthorized principals/tenants; assert no side effect on denial. Verify no credentials, plaintext or raw user content leave the intended trust boundary. Verify unregistered/malicious MCP tool schemas cannot expand permissions, redirect network access or bypass approval.
5. **Acceptance artifact:** No PR publishing, registry clean pulls by digest, SBOM/rollback, doc offline smoke. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-REL-010` only after verified pass.

#### OC2-D-REL-011 — Implement selected model-backed scheduled/manual tests gated on checkpoint/license approvals and truthful SKIP status.

1. **Repository and prerequisites:** Work in `.github/workflows/, scripts/release/, site/, examples/PackageConsumers/` or the existing equivalent; record the package/adapter owner. Check `S01-S17` gate and the current source API/contract version.
2. **Concrete implementation:** Implement selected model-backed scheduled/manual tests gated on checkpoint/license approvals and truthful SKIP status. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Use protected tags, pinned actions, independently built/tested 11 images; promote :latest only after all versioned digests verify. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Exercise authorized and explicitly unauthorized principals/tenants; assert no side effect on denial. Verify no credentials, plaintext or raw user content leave the intended trust boundary.
5. **Acceptance artifact:** No PR publishing, registry clean pulls by digest, SBOM/rollback, doc offline smoke. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-REL-011` only after verified pass.

#### OC2-D-REL-012 — Define Docker Hub image matrix: matibevuminkosi/openconnect-worker-security, classification, nllb, inference, transcription, realtime-voice, kokoro, mms-tts, navigator-playwright, operational-agents, outbox.

1. **Repository and prerequisites:** Work in `.github/workflows/, scripts/release/, site/, examples/PackageConsumers/` or the existing equivalent; record the package/adapter owner. Check `S01-S17` gate and the current source API/contract version.
2. **Concrete implementation:** Define Docker Hub image matrix: matibevuminkosi/openconnect-worker-security, classification, nllb, inference, transcription, realtime-voice, kokoro, mms-tts, navigator-playwright, operational-agents, outbox. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Use protected tags, pinned actions, independently built/tested 11 images; promote :latest only after all versioned digests verify. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Prove commit/rollback behavior against an actual supported relational provider. Inject lost ACK, stale fencing token and concurrent writer where the contract touches durable state.
5. **Acceptance artifact:** No PR publishing, registry clean pulls by digest, SBOM/rollback, doc offline smoke. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-REL-012` only after verified pass.

#### OC2-D-REL-013 — Use canonical full image name matibevuminkosi/openconnect-worker-<name>:<version> for all 11, with immutable SHA/digest manifest.

1. **Repository and prerequisites:** Work in `.github/workflows/, scripts/release/, site/, examples/PackageConsumers/` or the existing equivalent; record the package/adapter owner. Check `S01-S17` gate and the current source API/contract version.
2. **Concrete implementation:** Use canonical full image name matibevuminkosi/openconnect-worker-<name>:<version> for all 11, with immutable SHA/digest manifest. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Use protected tags, pinned actions, independently built/tested 11 images; promote :latest only after all versioned digests verify. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Prove commit/rollback behavior against an actual supported relational provider. Inject lost ACK, stale fencing token and concurrent writer where the contract touches durable state.
5. **Acceptance artifact:** No PR publishing, registry clean pulls by digest, SBOM/rollback, doc offline smoke. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-REL-013` only after verified pass.

#### OC2-D-REL-014 — Implement PR image build/test/scan with no registry push and restricted untrusted workflow permissions.

1. **Repository and prerequisites:** Work in `.github/workflows/, scripts/release/, site/, examples/PackageConsumers/` or the existing equivalent; record the package/adapter owner. Check `S01-S17` gate and the current source API/contract version.
2. **Concrete implementation:** Implement PR image build/test/scan with no registry push and restricted untrusted workflow permissions. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Use protected tags, pinned actions, independently built/tested 11 images; promote :latest only after all versioned digests verify. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify disabled/uninstalled, ready, startup failure and shutdown behavior independently. Validate model/asset manifest hash, license, actual capabilities and bounded resources where applicable.
5. **Acceptance artifact:** No PR publishing, registry clean pulls by digest, SBOM/rollback, doc offline smoke. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-REL-014` only after verified pass.

#### OC2-D-REL-015 — Implement protected tag release that builds and pushes exact version tags for each of 11 images only after passing image gates.

1. **Repository and prerequisites:** Work in `.github/workflows/, scripts/release/, site/, examples/PackageConsumers/` or the existing equivalent; record the package/adapter owner. Check `S01-S17` gate and the current source API/contract version.
2. **Concrete implementation:** Implement protected tag release that builds and pushes exact version tags for each of 11 images only after passing image gates. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Use protected tags, pinned actions, independently built/tested 11 images; promote :latest only after all versioned digests verify. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Prove commit/rollback behavior against an actual supported relational provider. Inject lost ACK, stale fencing token and concurrent writer where the contract touches durable state.
5. **Acceptance artifact:** No PR publishing, registry clean pulls by digest, SBOM/rollback, doc offline smoke. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-REL-015` only after verified pass.

#### OC2-D-REL-016 — Verify all version images by digest pull/start/health and artifact provenance before promoting :latest tags.

1. **Repository and prerequisites:** Work in `.github/workflows/, scripts/release/, site/, examples/PackageConsumers/` or the existing equivalent; record the package/adapter owner. Check `S01-S17` gate and the current source API/contract version.
2. **Concrete implementation:** Verify all version images by digest pull/start/health and artifact provenance before promoting :latest tags. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Use protected tags, pinned actions, independently built/tested 11 images; promote :latest only after all versioned digests verify. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify disabled/uninstalled, ready, startup failure and shutdown behavior independently. Validate model/asset manifest hash, license, actual capabilities and bounded resources where applicable.
5. **Acceptance artifact:** No PR publishing, registry clean pulls by digest, SBOM/rollback, doc offline smoke. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-REL-016` only after verified pass.

#### OC2-D-REL-017 — Document Docker Hub bot token/credential scope, secret handling and protected release environment approvals.

1. **Repository and prerequisites:** Work in `.github/workflows/, scripts/release/, site/, examples/PackageConsumers/` or the existing equivalent; record the package/adapter owner. Check `S01-S17` gate and the current source API/contract version.
2. **Concrete implementation:** Document Docker Hub bot token/credential scope, secret handling and protected release environment approvals. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Use protected tags, pinned actions, independently built/tested 11 images; promote :latest only after all versioned digests verify. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Prove commit/rollback behavior against an actual supported relational provider. Inject lost ACK, stale fencing token and concurrent writer where the contract touches durable state.
5. **Acceptance artifact:** No PR publishing, registry clean pulls by digest, SBOM/rollback, doc offline smoke. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-REL-017` only after verified pass.

#### OC2-D-REL-018 — Publish GHCR only as optional mirror, never silently substitute GHCR for primary Docker Hub registry.

1. **Repository and prerequisites:** Work in `.github/workflows/, scripts/release/, site/, examples/PackageConsumers/` or the existing equivalent; record the package/adapter owner. Check `S01-S17` gate and the current source API/contract version.
2. **Concrete implementation:** Publish GHCR only as optional mirror, never silently substitute GHCR for primary Docker Hub registry. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Use protected tags, pinned actions, independently built/tested 11 images; promote :latest only after all versioned digests verify. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify disabled/uninstalled, ready, startup failure and shutdown behavior independently. Validate model/asset manifest hash, license, actual capabilities and bounded resources where applicable.
5. **Acceptance artifact:** No PR publishing, registry clean pulls by digest, SBOM/rollback, doc offline smoke. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-REL-018` only after verified pass.

#### OC2-D-REL-019 — Implement signed/SBOM/provenance/vulnerability/license reports for NuGet/npm/containers and maintain release manifest.

1. **Repository and prerequisites:** Work in `.github/workflows/, scripts/release/, site/, examples/PackageConsumers/` or the existing equivalent; record the package/adapter owner. Check `S01-S17` gate and the current source API/contract version.
2. **Concrete implementation:** Implement signed/SBOM/provenance/vulnerability/license reports for NuGet/npm/containers and maintain release manifest. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Use protected tags, pinned actions, independently built/tested 11 images; promote :latest only after all versioned digests verify. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Prove commit/rollback behavior against an actual supported relational provider. Inject lost ACK, stale fencing token and concurrent writer where the contract touches durable state.
5. **Acceptance artifact:** No PR publishing, registry clean pulls by digest, SBOM/rollback, doc offline smoke. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-REL-019` only after verified pass.

#### OC2-D-REL-020 — Implement nuget.org trusted publishing where account supports it and inspect nupkg/snupkg/XML docs/source links.

1. **Repository and prerequisites:** Work in `.github/workflows/, scripts/release/, site/, examples/PackageConsumers/` or the existing equivalent; record the package/adapter owner. Check `S01-S17` gate and the current source API/contract version.
2. **Concrete implementation:** Implement nuget.org trusted publishing where account supports it and inspect nupkg/snupkg/XML docs/source links. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Use protected tags, pinned actions, independently built/tested 11 images; promote :latest only after all versioned digests verify. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Validate generated/readme/code snippet claims against built assemblies or referenced source. Detect references to superseded APIs, stale paths and unsupported capabilities.
5. **Acceptance artifact:** No PR publishing, registry clean pulls by digest, SBOM/rollback, doc offline smoke. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-REL-020` only after verified pass.

#### OC2-D-REL-021 — Implement npm @openconnect/client/composer/navigator packages with ESM/type exports, SSR-safe headless modules and optional React bindings.

1. **Repository and prerequisites:** Work in `.github/workflows/, scripts/release/, site/, examples/PackageConsumers/` or the existing equivalent; record the package/adapter owner. Check `S01-S17` gate and the current source API/contract version.
2. **Concrete implementation:** Implement npm @openconnect/client/composer/navigator packages with ESM/type exports, SSR-safe headless modules and optional React bindings. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Use protected tags, pinned actions, independently built/tested 11 images; promote :latest only after all versioned digests verify. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify successful integration with canonical contract fixtures. Inject malformed input, cancellation and unavailable dependency; assert typed error/degraded behavior.
5. **Acceptance artifact:** No PR publishing, registry clean pulls by digest, SBOM/rollback, doc offline smoke. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-REL-021` only after verified pass.

#### OC2-D-REL-022 — Verify registry-installed NuGet/npm artifacts in clean external consumers, not source project references.

1. **Repository and prerequisites:** Work in `.github/workflows/, scripts/release/, site/, examples/PackageConsumers/` or the existing equivalent; record the package/adapter owner. Check `S01-S17` gate and the current source API/contract version.
2. **Concrete implementation:** Verify registry-installed NuGet/npm artifacts in clean external consumers, not source project references. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Use protected tags, pinned actions, independently built/tested 11 images; promote :latest only after all versioned digests verify. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Run targeted success and deliberate negative regression in CI. Save machine-readable test artifacts and versioned audit evidence.
5. **Acceptance artifact:** No PR publishing, registry clean pulls by digest, SBOM/rollback, doc offline smoke. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-REL-022` only after verified pass.

#### OC2-D-REL-023 — Create Astro Starlight static docs, versioned navigation/search, DocFX .NET API and generated OpenAPI contracts from actual assemblies.

1. **Repository and prerequisites:** Work in `.github/workflows/, scripts/release/, site/, examples/PackageConsumers/` or the existing equivalent; record the package/adapter owner. Check `S01-S17` gate and the current source API/contract version.
2. **Concrete implementation:** Create Astro Starlight static docs, versioned navigation/search, DocFX .NET API and generated OpenAPI contracts from actual assemblies. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Use protected tags, pinned actions, independently built/tested 11 images; promote :latest only after all versioned digests verify. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Validate generated/readme/code snippet claims against built assemblies or referenced source. Detect references to superseded APIs, stale paths and unsupported capabilities.
5. **Acceptance artifact:** No PR publishing, registry clean pulls by digest, SBOM/rollback, doc offline smoke. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-REL-023` only after verified pass.

#### OC2-D-REL-024 — Implement optional docs-only read-agent through published OpenConnect APIs with grounded citations, authentication and read-only tools.

1. **Repository and prerequisites:** Work in `.github/workflows/, scripts/release/, site/, examples/PackageConsumers/` or the existing equivalent; record the package/adapter owner. Check `S01-S17` gate and the current source API/contract version.
2. **Concrete implementation:** Implement optional docs-only read-agent through published OpenConnect APIs with grounded citations, authentication and read-only tools. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Use protected tags, pinned actions, independently built/tested 11 images; promote :latest only after all versioned digests verify. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Validate generated/readme/code snippet claims against built assemblies or referenced source. Detect references to superseded APIs, stale paths and unsupported capabilities.
5. **Acceptance artifact:** No PR publishing, registry clean pulls by digest, SBOM/rollback, doc offline smoke. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-REL-024` only after verified pass.

#### OC2-D-REL-025 — Verify docs site/search works when all AI/worker/backend services are offline.

1. **Repository and prerequisites:** Work in `.github/workflows/, scripts/release/, site/, examples/PackageConsumers/` or the existing equivalent; record the package/adapter owner. Check `S01-S17` gate and the current source API/contract version.
2. **Concrete implementation:** Verify docs site/search works when all AI/worker/backend services are offline. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Use protected tags, pinned actions, independently built/tested 11 images; promote :latest only after all versioned digests verify. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Verify disabled/uninstalled, ready, startup failure and shutdown behavior independently. Validate model/asset manifest hash, license, actual capabilities and bounded resources where applicable.
5. **Acceptance artifact:** No PR publishing, registry clean pulls by digest, SBOM/rollback, doc offline smoke. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-REL-025` only after verified pass.

#### OC2-D-REL-026 — Define staged release, partial failure, immutable-version recovery, rollback and image digest re-deploy procedures.

1. **Repository and prerequisites:** Work in `.github/workflows/, scripts/release/, site/, examples/PackageConsumers/` or the existing equivalent; record the package/adapter owner. Check `S01-S17` gate and the current source API/contract version.
2. **Concrete implementation:** Define staged release, partial failure, immutable-version recovery, rollback and image digest re-deploy procedures. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Use protected tags, pinned actions, independently built/tested 11 images; promote :latest only after all versioned digests verify. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Prove commit/rollback behavior against an actual supported relational provider. Inject lost ACK, stale fencing token and concurrent writer where the contract touches durable state.
5. **Acceptance artifact:** No PR publishing, registry clean pulls by digest, SBOM/rollback, doc offline smoke. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-REL-026` only after verified pass.

#### OC2-D-REL-027 — Ensure deployment/manifests and operator docs document optional worker absence, cache persistence, credential rotation and profile-level readiness.

1. **Repository and prerequisites:** Work in `.github/workflows/, scripts/release/, site/, examples/PackageConsumers/` or the existing equivalent; record the package/adapter owner. Check `S01-S17` gate and the current source API/contract version.
2. **Concrete implementation:** Ensure deployment/manifests and operator docs document optional worker absence, cache persistence, credential rotation and profile-level readiness. Scope the change to a single public behavior and add typed options/validation at the boundary where an option exists.
3. **Ownership constraint:** Use protected tags, pinned actions, independently built/tested 11 images; promote :latest only after all versioned digests verify. Native MAF functionality must be configured/decorated, never reimplemented as a competing agent or workflow engine.
4. **Failure/negative verification:** Exercise authorized and explicitly unauthorized principals/tenants; assert no side effect on denial. Verify no credentials, plaintext or raw user content leave the intended trust boundary.
5. **Acceptance artifact:** No PR publishing, registry clean pulls by digest, SBOM/rollback, doc offline smoke. Record exact command, test fixture IDs, expected/observed output, runtime/TFM, provider/model where relevant, commit and CI link. Update `OC2-D-REL-027` only after verified pass.

## 6. Pull request protocol, delivery artifacts and rollback

Each PR records `{task IDs, requirement IDs, spec section, native MAF API proof, code paths, contract/DDL changes, positive tests, negative tests, full command and run artifact, TFM/provider/worker/model/browser, source license/provenance, security reviewer, migration/rollback notes}`. A PR must include tests demonstrating **the selected feature is absent without breaking independent Direct/text/MCP flows**, unless the configured operation requires unavailable privacy coverage. If it affects historical application data, include a downgrade/rolling-upgrade decision; do not delete or rewrite committed ledger events to make old clients pass. The checklist controls PASS; this plan alone never does.

### End-to-end release test families

`OC2-AT-001–071` remain independent, initially NOT RUN. Real MAF direct/harness/workflow and session continuation, runtime auth revocation, tool/MCP malicious-input denial, EF SQL Server/PostgreSQL/SQLite fault tests, NATS outbox crash/replay, client privacy/authorization and VAD/barge-in, all 11 independently delivered Docker images on Docker Hub, clean net8/net9/net10 consumers and release provenance must be demonstrated separately before marketing support. Historical ID crosswalks remain in `checklist.md` and `requirements.md` and are not automatically considered passing.
