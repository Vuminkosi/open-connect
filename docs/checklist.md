# OpenConnect — Engineering Delivery and Release Checklist

**Version:** 1.1-draft | **Date:** 2026-11-06  
**Status:** All items initially unchecked. Never mark an item completed until evidence is available.  
**Source of truth:** `spec.md` and `firewall.md` (requirements), `implementation.md` (work packages).

## 0. Rules for automated coding agents

- Execute tasks in dependency order. Each checkbox corresponds to a deliverable with tests, not a narrative update.
- Change `[ ]` to `[x]` only after its acceptance evidence exists. Record PR/commit, exact test command, result and date in §15 evidence log; use `BLOCKED` in log rather than ticking a failed task.
- No model-generated success report substitutes for actual test logs. Do not claim live inference/voice or published package without real endpoints/registry evidence.
- `spec.md` and `firewall.md` control product behavior. `implementation.md` controls task sequencing. This checklist controls verification. If an inconsistency appears, open an ADR/issue and update all four documents before implementing contradictory interfaces.
- Critical paths: **license/source lock -> contracts -> core/MAF/privacy -> authorization -> persistence/outbox -> NATS/HTTP/WS -> clients/components -> workers -> docs -> release**.
- All `[SEC]` tasks are stable-release blocking. Missing model/GPU support may be deferred only when the corresponding capability remains experimental/unavailable in published descriptors.
- Strictly no maction of references (swift-aid/pii-guard ) on any openconnect docs/source code treat them as references

## 1. Project governance and audit — Stage S0

- [ ] **IMP-001** New `openconnect` repository, LICENSE, README, AGENTS.md, CODEOWNERS and CONTRIBUTING.md established.
- [ ] **IMP-001** .NET solution, central package versions, lockfile, `global.json`, `.editorconfig`, source-link and build warnings-as-errors configured.
- [ ] **IMP-001** npm and Python uv lockfiles committed and reproducible on clean checkout.
- [ ] **IMP-002** Swift-Aid `master` commit SHA pinned in `docs/migration/SOURCE-LOCK.md` (not merely `master` URL).
- [ ] **IMP-002** Full migration ledger covers `src/.../Ellai`, JetStream messaging, persistence, gateway, `workers/src`, `infra/ellai-tts` and relevant tests.
- [ ] **IMP-002** Every migrated file has source SHA, license, destination, modification summary, test mapping and decision (adapt/rewrite/reject).
- [ ] **IMP-003 [SEC]** Swift-Aid source license/copyright obligations reviewed; no unlicensed redistribution.
- [ ] **IMP-003 [SEC]** Python dependency, model weight, ONNX, voice/checkpoint and data license audits completed; notices included.
- [ ] **IMP-003 [SEC]** All test/sample data synthetic; no Swift-Aid sensitive records, secrets or case domain assertions copied.
- [ ] **IMP-004** Canonical OpenAPI v1 and event/worker/component/agent/capability schemas created.
- [ ] **IMP-004** C#, Pydantic and TS validators reject same invalid fixtures and accept valid ones.
- [ ] **IMP-005 [SEC]** Threat model documents trust boundaries, attacker actions and mitigations.
- [ ] **IMP-006** Baseline PR CI required by protected branch; any intentional failing test blocks merge.

**Stage S0 evidence:** source SHA = `TBD`; licensing review PR = `TBD`; CI run = `TBD`.

## 2. Core abstractions and initialization — Stage S1

- [ ] **IMP-010** Stable typed identifiers and DTOs, error codes, cancellation and serialization.
- [ ] **IMP-010** Interfaces for clock, models, agent registry, tool broker, privacy, sessions, journal, outbox and worker router.
- [ ] **IMP-010** `OpenConnect.Abstractions` compiles without ASP.NET, Blazor, NATS, EF, PostgreSQL or cloud SDK.
- [ ] **IMP-011** `.AddOpenConnect()` registers default lead agent, privacy firewall enabled and options validation.
- [ ] **IMP-011** `.AddOpenConnect()` is idempotent; missing model/transport/persistence configuration reports actionable error.
- [ ] **IMP-011 [SEC]** Explicit `Privacy.Enabled=false` works, emits policy warning, **does not disable authN/authZ/tool authorization**.
- [ ] **IMP-012** `agent-runtime.md` safe path and instruction loading, hashing, size validation and config resolution.
- [ ] **IMP-012 [SEC]** Markdown cannot grant tool rights, change tenant, disable privacy or override host policy.
- [ ] **IMP-013** `openconnect.yaml` validated: agents, delegates, models, capabilities, budgets, worker profiles and policies.
- [ ] **IMP-013** Cycles, unknown IDs, duplicate IDs, invalid refs, oversized budgets, path escapes rejected at startup.
- [ ] **IMP-014** Actual supported Microsoft Agent Framework APIs used; pinned dependency smoke compiles.
- [ ] **IMP-014** Real lead -> subagent execution trace with parent-child IDs and actual model/tool events.
- [ ] **IMP-015** Runtime capability registry supports missing/healthy/stale, provider feature matrix and privacy location labels.
- [ ] **IMP-016 [SEC]** Agent-scoped tool discovery intersects agent allowlists, principal, tenant and contextual grants.
- [ ] **IMP-016 [SEC]** Tool calls reauthorized and schema-validated at execution time, approvals exact-argument scoped.
- [ ] **IMP-016 [SEC]** Invalid/expired approval and prompt-injected tool escalation denied before tool runs.
- [ ] **IMP-017 [SEC]** Privacy defaults on; host-owned enforcement integrates dedicated Python `security` worker and fails closed if it is missing or unready.
- [ ] **IMP-017 [SEC]** Mask mappings encrypted and scoped to tenant+principal+session+turn with TTL/rotation.
- [ ] **IMP-017 [SEC]** Tool restoration only for authorized field(s), never elevated tool or ACL privileges.
- [ ] **IMP-017 [SEC]** NATS/logs/telemetry/worker messages exclude canary sensitive payloads.
- [ ] **IMP-018** Model/provider adapters declare actual chat, tool, streaming and structured output support.
- [ ] **IMP-018** Unsupported capabilities fail predictably; no implicit privacy-changing fallback.
- [ ] **IMP-019** Minimal host executes real permitted task through default lead, MAF and privacy boundary.

**Stage S1 gate:** `AT-001`, `AT-002`, `AT-003`, `AT-004`, `AT-008` passed; code review required.

## 2A. Typed options and interactive/operational execution modes — Stage S1/S2/S4 (v1.1)

- [ ] **IMP-090 [SEC]** All closed choices represented by public, documented C# enums; stable wire names (`strict-local`, `interactive`, etc.) across JSON/YAML/C#/Pydantic/TypeScript; unknown/numeric input rejected. **AT-031**.
- [ ] **IMP-091 [SEC]** `AddOpenConnect` typed defaults verified: lead interactive, privacy enabled, rules detector enabled, all model-backed detectors disabled; no implicit Docker/model boot. **AT-032**.
- [ ] **IMP-091 [SEC]** Invalid mode/identity, misfire, time zone, required coverage/disabled detector, missing durable backend, unsafe fail-open configuration rejected during startup. **AT-032**.
- [ ] **IMP-092 [SEC]** Interactive agent inherits actual active principal's bounded authorization snapshot; no raw tokens transmitted to models/NATS/worker; per-tool reauthorization and revoked session tests pass. **AT-033/034**.
- [ ] **IMP-092 [SEC]** Subagent delegation cannot enlarge effective tool/tenant grants; WS reconnect cannot restore revoked grants. **AT-034**.
- [ ] **IMP-093 [SEC]** .NET `BackgroundService` supervises operational agents with constrained workload identity and scoped DI, not the current user's `HttpContext`; no user-cookie inheritance. **AT-035**.
- [ ] **IMP-093** Scheduled/event/continuous activation, invalid cron/time zone, misfire, overlap, pause/resume, retries and cancellation verified with deterministic clock. **AT-036**.
- [ ] **IMP-094 [SEC]** Operational runs/leases/fences, idempotency, outbox and JetStream crash recovery protect from duplicate committed state/side effects. **AT-037**.
- [ ] **IMP-095 [SEC]** Operational controls/operator RBAC, authorizations, exact review receipts and redacted status/metrics; denied cross-tenant action never runs. **AT-038**.
- [ ] **IMP-096 [SEC]** Independently test Rules/GLiNER/DeBERTa/Qwen3Guard/PaddleOCR/YuNet ON/OFF; disabled models never import-initialize, load, download or advertise capabilities. **AT-039**.
- [ ] **IMP-097 [SEC]** Coverage-aware policy checks required-vs-ready modalities/detectors; unrelated rules-only workload succeeds but protected external free text/image fails closed when coverage missing. **AT-040**.
- [ ] **IMP-098** Rules-only low-memory CPU Docker with no model weights, targeted model volumes, resource limits/readiness and shutdown tests. **AT-041**.
- [ ] **IMP-099 [SEC]** Required CI checks include enum/JSON/TS/Pydantic parity, both execution modes, isolation, detector toggle, low-resource Docker, clean-package consumers and docs. **AT-042**.

**Release policy:** Security gates AT-031–040 and AT-042 required for stable advertised features; AT-041 required for marketing the minimal Docker profile. All tasks remain unchecked until real evidence is logged. AT-022/IMP-082 local NER regression applies to **enabled semantic PII detector builds**; strict-local + rules-only mode may operate only where the operator's declared coverage policy permits it, never marketed as semantic protection.


## 3. Persistence, NATS and gateway — Stage S2

- [ ] **IMP-020** PostgreSQL migrations for sessions/tasks/turns/events/outbox/leases/approvals/proposals/token vault implemented.
- [ ] **IMP-020** Migration application tested on new DB and upgraded disposable fixture; schema indexes and uniqueness validated.
- [ ] **IMP-020 [SEC]** All ownership lookups enforce tenant/principal; no direct worker DB permission.
- [ ] **IMP-021** Task acceptance, event and outbox committed atomically; retries with same clientTaskId return same task.
- [ ] **IMP-021** Same idempotency key with different input responds 409; concurrency tests run.
- [ ] **IMP-022** JetStream subjects `oc.v1.*`, stream provisioning and explicit ACLs documented.
- [ ] **IMP-022 [SEC]** Worker accounts cannot provision streams, read other worker subjects or subscribe to all task data.
- [ ] **IMP-022** Publish ACK, bounded envelope, message dedup and broker unavailable behavior tested.
- [ ] **IMP-023** Outbox resumes after host restart and broker outage, retries without duplicate committed state.
- [ ] **IMP-024** Worker lease/claim/complete/fail/renew protocol with committed-before-ACK semantics.
- [ ] **IMP-024** Lost completion, expired lease, worker kill and malformed/oversized envelope tests pass.
- [ ] **IMP-025** Per-session monotonic sequence and persisted replay implement cursor expiry/resnapshot.
- [ ] **IMP-026** HTTP routes implemented per generated OpenAPI, typed errors and proper authentication.
- [ ] **IMP-026 [SEC]** Client-supplied tenant/owner ignored; IDOR, request size limits, rate limits tested.
- [ ] **IMP-027** WS authenticated subscription, keepalive, bounded queue, resume cursor and cancellation tested.
- [ ] **IMP-027 [SEC]** WS origin/ticket/session scopes and auth expiration tested; tokens absent from URL logs.
- [ ] **IMP-028** OpenTelemetry traces and redacted metrics across HTTP/MAF/NATS/worker.
- [ ] **IMP-029** Ten consecutive conversation turns survive restart and replay without duplicates.
- [ ] **IMP-029** NATS outage/resume and dispatcher/worker crash recovery integration completed.

**Stage S2 gate:** `AT-005`, `AT-006`, `AT-007`, `AT-016` passed with actual PostgreSQL + NATS.

## 3A. Privacy firewall and dedicated security-worker workstream — Stage S1/S4 security

**Normative spec:** `firewall.md` OC-FW-001–048. All checked items require reproducible evidence (not a green build only). Text firewall stable gates are mandatory for stable P0; image/audio may remain explicitly unavailable until verified.

- [ ] **IMP-080 [SEC]** Security scan/response versioned JSON schemas, threat model, synthetic golden fixtures and research-license/model-provenance audit approved.
- [ ] **IMP-081 [SEC]** Separate `workers/security` Python 3.12+ uv project: authenticated private scan API, true liveness/readiness and no vault/host authorization privilege.
- [ ] **IMP-081 [SEC]** Missing/invalid model and worker timeout block strict-mode model egress (no silent rules-only downgrade unless policy explicitly allows a truthful, limited capability).
- [ ] **IMP-082 [SEC]** Local rules/checksums/secrets/known values + OPTIONAL pinned licensed local NER (required only when semantic PII coverage is advertised); tested unicode offsets, overlap, multilingual and financial blind fixtures.
- [ ] **IMP-082 [SEC]** Unsupported languages are marked unavailable or risk-classified; no borrowed benchmark results claimed as OpenConnect performance.
- [ ] **IMP-083 [SEC]** Host validates worker span output, creates unpredictable placeholders and securely stores AES-GCM equivalent encrypted per-scope token mappings with TTL/key rotation.
- [ ] **IMP-083 [SEC]** Worker cannot decrypt/restore mappings; cross-tenant/principal/session/turn replay, token forgery and collision tests pass.
- [ ] **IMP-084 [SEC]** Host egress gates cover MAF model calls, subagents, tool arguments/results, retrieval, workers, WS/client component payloads, NATS/logs and telemetry.
- [ ] **IMP-084 [SEC]** Restoration allowlisted by destination and schema path; streaming split-token/reconnect tests prevent partial or cross-scope leakage.
- [ ] **IMP-085 [SEC]** Local image/PDF/OCR/face/signature processing only advertised as protected after actual local model/hidden-layer and metadata tests; otherwise strict-mode egress blocked.
- [ ] **IMP-086 [SEC]** Audio/STT/realtime strict-local boundaries proven; unsupported remote audio rejected rather than transcript-only masking claim.
- [ ] **IMP-087 [SEC]** `security` Compose profile, GHCR image, locked Python dependencies, nonroot, read-only pinned model volume, local-only network policy, readiness, no external detector calls.
- [ ] **IMP-088 [SEC]** CI required jobs: `security-worker-unit`, `security-worker-contracts`, `firewall-integration`, `firewall-adversarial`, `firewall-image`, plus scheduled model-backed validation.
- [ ] **IMP-088 [SEC]** Deliberate missing worker, malicious span, forged token, cross-tenant value and log-canary tests fail closed and block protected PR.
- [ ] **IMP-089 [SEC]** Security image signed and scanned with SBOM/provenance; digest deployment and fail-closed outage verified with real host.
- [ ] **IMP-089 [SEC]** Operator documentation covers detector model acquisition, privacy-off warning, key rotation, failure/incident handling and honest unsupported capabilities.

**Security worker stable gates:** AT-008, AT-021–025, AT-028–030, plus full P0 security gates. AT-026/027 required before image/audio marketed as strict-local privacy preserving. Do not tick until measured proof exists.

## 4. Python worker common platform — Stage S4 foundation

- [ ] **IMP-030** All worker runtimes Python 3.12+ using reproducible `uv.lock`, Pydantic schema parity.
- [ ] **IMP-030** Shared worker library supports scoped authenticated machine ID, NATS pull/ack, HTTP claim/commit, leases, retries, timeouts, cancellation.
- [ ] **IMP-030 [SEC]** Raw prompt/PII absent from broker payloads; gateway-authorized reference retrieval only.
- [ ] **IMP-030** Health = process liveness; readiness = actual model loaded + dependencies OK + consumer bound.
- [ ] **IMP-030** Worker model manifest includes checkpoint, revision, checksum, license and runtime resources.
- [ ] **IMP-030** `ruff`, `mypy`, `pytest`, contract parity and fault injection pass without model download.
- [ ] **IMP-037** Per-worker non-root Docker builds, pinned base digests, locked dependencies, graceful shutdown.
- [ ] **IMP-037 [SEC]** No private weights/secrets in final layers, image history, container logs or SBOM.
- [ ] **IMP-038** Registered capabilities reflect actual loaded model, languages and health; stale registrations unavailable.
- [ ] **IMP-039** Compose starts core without optional worker image/model; independent profiles documented. When strict privacy is enabled, external egress fails closed until the separately deployed security worker becomes ready.
- [ ] **IMP-039** Persistent NATS/Postgres/model volumes, ACL bootstrap and backup policies documented.

## 5. Python worker capability delivery — Stage S4 capabilities

### Classification
- [ ] **IMP-031** Reviewed source from `workers/src/swiftaid_workers/classification` migrated or rewritten with attribution.
- [ ] **IMP-031** Generic classification labels/threshold/abstention/language control; no medical/case assumptions.
- [ ] **IMP-031** Classification worker produces durable, schema-checked result and ACK after host commit.
- [ ] **IMP-031** Real pinned classification model smoke against synthetic test fixture (record model/version/hardware).

### MMS-TTS
- [ ] **IMP-032** Reviewed `infra/ellai-tts` Python source migrated/reworked, preserving licensed model settings and synthetic tests.
- [ ] **IMP-032** Voice ID/language/checkpoint validation, LRU/cache budget and audio sample-rate metadata pass.
- [ ] **IMP-032** Real model text-to-audio smoke outputs playable audio with correct metadata.
- [ ] **IMP-032** Worker interruption/timeout limits and concurrency/backpressure tested.

### NLLB
- [ ] **IMP-033** Separate Python NLLB worker, pinned checkpoint, approved license and documented supported language-pair list.
- [ ] **IMP-033** Unavailable language pair fails explicitly without substituting a different language.
- [ ] **IMP-033** Real model translation quality fixture and long-input handling recorded.

### Self-hosted inference
- [ ] **IMP-034** Python inference worker matches real model serving engine capabilities (chat, tools, stream, embeddings only where supported).
- [ ] **IMP-034** Local model isolation, explicit model-id routing and optional GPU resource requirements implemented.
- [ ] **IMP-034** Real inference + cancellation + unsupported tool-call tests pass.

### Voice transcription
- [ ] **IMP-035** Python transcription worker for recorded files with explicit codec/rate/size/language.
- [ ] **IMP-035** Partial/final streaming STT exposed **only if model/provider actually supports it**.
- [ ] **IMP-035 [SEC]** Strict-local speech privacy and remote-audio processing boundaries documented/tested.
- [ ] **IMP-035** Real model transcript smoke for supported languages; multilingual exclusions documented.

### Realtime voice dialog
- [ ] **IMP-036** Python worker state machine with VAD/listening/transcribing/reasoning/speaking/interrupt/cancel.
- [ ] **IMP-036** Speech epoch prevents late audio chunks after barge-in.
- [ ] **IMP-036** Five real conversational voice turns + one interruption with captured telemetry.
- [ ] **IMP-036** Network drop, race and reconnect/cancel tests pass.

### Worker release readiness
- [ ] All workers independently tagged stable/experimental by actual test evidence; registry reports unavailable for missing models.
- [ ] CI builds worker images independently and runs health/readiness and worker protocol smoke.
- [ ] Model licenses, artifacts, checksum validation and startup mount instructions published.

## 6. Blazor client and component UX — Stage S3

- [ ] **IMP-040** .NET task client supports create/get/cancel/session/capabilities/events with `CancellationToken`.
- [ ] **IMP-041** `.AddOpenConnectClient()` accepts configured HTTP(S) task API and WS(S) addresses.
- [ ] **IMP-041** Secure URLs/auth handlers validated, including refresh/dispose/reconnect behavior.
- [ ] **IMP-042** `<OpenConnectProvider />` coordinates session, event stream, UI registry and component activation; working layout examples.
- [ ] **IMP-042** No duplicate Mud providers; tested with Blazor Server and WASM where officially supported.
- [ ] **IMP-043** `<MiniComposer />` renders bottom-right MudFAB with safe-area responsive placement.
- [ ] **IMP-043** Expand/collapse/hide, submit, stream, cancel, error, reconnect and suggestion interactions functional.
- [ ] **IMP-043** Accessibility: keyboard, focus, ARIA, contrast, high zoom and small viewport tests.
- [ ] **IMP-044** `IOpenConnectComponent` concept implemented as portable metadata contract plus safe Blazor renderer registration.
- [ ] **IMP-044** Descriptor includes identity/version, description, UI schemas, capability map, suggestions and required authorization.
- [ ] **IMP-045 [SEC]** Component discovery and proposals filtered by principal/agent/task and validated before render.
- [ ] **IMP-045 [SEC]** No arbitrary model-produced Razor/JS/HTML; property schema injection tests pass.
- [ ] **IMP-045 [SEC]** Privileged UI action requires explicit local user consent and server reauthorization.
- [ ] **IMP-046** Agent execution tree renders **real runtime events**, not a fabricated plan.
- [ ] **IMP-049** Playwright and component unit tests cover mobile, WS disconnect, auth expiry and malicious proposals.

**Stage S3 gate:** `AT-014`, `AT-015`, `AT-016` passed; no UI security failures.

## 7. TypeScript/npm SDK — Stage S3 parallel

- [ ] **IMP-047** `@openconnect/client` ESM + types package with correct exports and `sideEffects` metadata.
- [ ] **IMP-047** Generated API and event schemas synchronized with C# and Python golden fixtures.
- [ ] **IMP-047** Browser `fetch`/`WebSocket`, Node support, AbortSignal, typed errors and reconnect/resume.
- [ ] **IMP-047** No React dependency in core TS SDK; React package deferred.
- [ ] **IMP-047** TS lint, strict typecheck, unit tests, browser tests and package bundle inspection pass.
- [ ] **IMP-048** New Node sample installs tarball and executes task against live disposable gateway.
- [ ] **IMP-048** New .NET sample installs generated NuGet `.nupkg` without direct project references.

**Stage gate:** `AT-017` passed.

## 8. Website and assistant — Stage S5

- [ ] **IMP-050** Responsive website ships docs, getting-started, installation, workers, privacy, components and API reference.
- [ ] **IMP-050** Docs versioned by release; site remains accessible if model/assistant unavailable.
- [ ] **IMP-051** Documentation AI assistant is implemented with `AddOpenConnect()` lead + read-only docs retrieval agent/tools.
- [ ] **IMP-051 [SEC]** Assistant cannot access arbitrary private repositories, server files, write tools or hidden production secrets.
- [ ] **IMP-051** Assistant answers with cited source docs and real trace/stream output; no fake tool results.
- [ ] **IMP-052** Five-minute .NET host quickstart and Blazor MiniComposer quickstart run from clean checkout.
- [ ] **IMP-052** Self-hosted Python worker Compose recipes require explicit opt-in and model acceptance.
- [ ] **IMP-053** OpenAPI/JSON schema reference generated and included in docs build.
- [ ] **IMP-054** Migration provenance and source notices available on website/repo.
- [ ] **IMP-055** PR site preview protected; docs build/link checker/browser smoke pass before publish.

**Stage gate:** `AT-018` passed with actual published packages where available.

## 9. CI workflow checklist — Stage S6

- [ ] **IMP-060** `.github/workflows/ci.yml` triggers on PR, push to main and manual dispatch.
- [ ] **IMP-060** Workflow permissions minimum `contents: read`; avoid release secrets in PR jobs.
- [ ] **IMP-060** .NET job: locked restore, format verify, Release build, warnings as errors, unit tests + TRX.
- [ ] **IMP-061** Python per-worker matrix includes `security`: `uv sync --locked`, Ruff, mypy, pytest; no mandatory model downloads.
- [ ] **IMP-060** npm job: Node 24+, `npm ci`, lint/typecheck/unit/build/pack dry run.
- [ ] **IMP-062** Contract CI: OpenAPI lint, schema fixtures, C#/TS/Python parity, breaking-diff gate.
- [ ] **IMP-063** Integration CI uses disposable PostgreSQL + NATS and fake Python worker; no production credentials.
- [ ] **IMP-063** Crash/replay/ACK, tenant-boundary, event ordering and clean package consumers tested.
- [ ] **IMP-088 [SEC]** Dedicated security-worker CI integration/adversarial/contract/image jobs required on PR, with actual NATS/model/telemetry canary tests.
- [ ] **IMP-064 [SEC]** CodeQL, dependency/security and secret scans plus defined failure policy.
- [ ] **IMP-064 [SEC]** Container scan, SBOM and dependency license report archived.
- [ ] **IMP-065** Website docs build, snippets, links and basic accessibility validated.
- [ ] **IMP-066** Nightly/method-triggered real model/gpu tests with explicit SKIP/BLOCK reason when unavailable.
- [ ] **IMP-060** GitHub branch protection requires jobs and code-owner reviews, no bypass by untrusted actor.
- [ ] **IMP-060 [SEC]** Release workflows and GitHub actions pinned to reviewed immutable SHAs.

## 10. NuGet release checklist — Stage S6

- [ ] **IMP-070** Protected annotated tag and `release` environment approvals configured.
- [ ] **IMP-070** Release version matches package metadata, schema and changelog; no existing conflicting package version.
- [ ] **IMP-071** Restore/test/pack all meaningful NuGet projects in Release mode with deterministic metadata.
- [ ] **IMP-071** `.nupkg` includes license, README, version, repo URL, source-link, XML docs and approved dependencies.
- [ ] **IMP-071** `.snupkg` generated where supported and contents inspected.
- [ ] **IMP-071** SHA256 manifest/SBOM/artifact provenance created from exact tag SHA.
- [ ] **IMP-072** Nuget.org Trusted Publisher policy configured for repository + exact workflow filename + environment.
- [ ] **IMP-072 [SEC]** `NuGet/login@v1` OIDC exchange used; no long-lived NuGet API key in workflow.
- [ ] **IMP-072** Only release publisher job has `id-token: write`; short-lived key acquired immediately before push.
- [ ] **IMP-072** NuGet push includes `.nupkg` and optional symbols; partial publishing failures detected/recorded.
- [ ] **IMP-075** Clean external .NET consumer installs published release package(s) and compiles/runs.
- [ ] **IMP-076** Published package hashes/release manifest linked from GitHub Release.

## 11. npm release checklist — Stage S6

- [ ] **IMP-073** `@openconnect/client` name/scope claim and ownership confirmed on npm registry.
- [ ] **IMP-073** npm Trusted Publisher mapped to exact GitHub workflow/repository/environment.
- [ ] **IMP-073 [SEC]** Node 24+, npm >=11.5.1, GitHub-hosted runner, `id-token: write`; no long-lived npm auth token.
- [ ] **IMP-073 [SEC]** Release job disables untrusted shared package-manager caches.
- [ ] **IMP-071** `npm pack` artifact verified and package exports/types/README/license inspected.
- [ ] **IMP-073** First prerelease uses npm `next`; stable release uses `latest`; correct SemVer.
- [ ] **IMP-073** Npm OIDC provenance/attestation verified on package page where supported.
- [ ] **IMP-075** Clean Node consumer installs exact registry version and verifies SDK call.
- [ ] **IMP-076** npm tarball SHA and registry URL included in release manifest.

## 12. Docker/GHCR release checklist — Stage S6

- [ ] **IMP-074** Each worker image, including `openconnect-worker-security`, uses locked deps, reviewed base image digest and non-root user.
- [ ] **IMP-074** Buildx/metadata actions SHA pinned, GHCR publishing `packages:write` only on release job.
- [ ] **IMP-074** Per-worker image `vX.Y.Z`, Git SHA and immutable digest stored in manifest.
- [ ] **IMP-074 [SEC]** Image scan, SBOM and provenance/signature policy passes.
- [ ] **IMP-074 [SEC]** Model weights, credentials and `.env` files absent from published image layers.
- [ ] **IMP-075** Clean host pulls image by digest, validates liveness/readiness and executes worker protocol smoke.
- [ ] **IMP-089 [SEC]** Security worker missing/unready blocks strict-mode external inference; model-verified ready worker allows only sanitized output and correct token restoration.
- [ ] **IMP-075** GPU images published only where tested hardware/model set passes.
- [ ] **IMP-077** Previous image digest redeploy documented; NATS stream/database rollback is manual/reviewed.

## 13. Cross-channel release integrity

- [ ] **IMP-070** Release cannot start from PR or unprotected ref; tag commit CI green.
- [ ] **IMP-071** Exact compiled artifacts promoted to NuGet/npm/GHCR (no separate unnoticed rebuild).
- [ ] **IMP-075** NuGet/npm installation after registry publication verified; GHCR pull by digest verified.
- [ ] **IMP-076** GitHub Release posted only after successes, otherwise clearly labeled incomplete.
- [ ] **IMP-077** Recovery documented for NuGet/npm immutable package versions and partial failures.
- [ ] **IMP-078** Protected release environment and CODEOWNERS review enforced.
- [ ] **IMP-079** SHA manifest, SBOM, licenses, source attribution and provenance links attached.
- [ ] **IMP-079 [SEC]** Release artifacts free of secrets, sensitive test data and unlicensed model content.

## 14. Acceptance test matrix (MUST pass before claiming stable v1)

| ID | Requirement | Evidence to record | Status |
|---|---|---|---|
| AT-001 | Default lead + privacy on | DI test + live minimal host | NOT RUN |
| AT-002 | Firewall off retains security | Negative tool and auth tests + config warning | NOT RUN |
| AT-003 | agent-runtime/config safe | Schema + path and grant escalation tests | NOT RUN |
| AT-004 | Agent-scoped tools/subagents | Unauthorized invocation counter remains zero | NOT RUN |
| AT-005 | Idempotent acceptance | Concurrent duplicate client task test | NOT RUN |
| AT-006 | Durable session restart | 10-turn Postgres restart/resume trace | NOT RUN |
| AT-007 | NATS crash ACK semantics | Host+worker crash, redelivery/commit audit | NOT RUN |
| AT-008 | Tenant/privacy nonleakage | Adversarial test report; zero cross-tenant leaks | NOT RUN |
| AT-009 | Worker capability truthfulness | Missing/stale model registration probe | NOT RUN |
| AT-010 | Real classifier | Pinned model and durable claim/commit result | NOT RUN |
| AT-011 | Real MMS-TTS | Checkpoint hash, correct sample rate and audio output | NOT RUN |
| AT-012 | Real NLLB/inference/STT | Separate real model smoke per stable-labelled worker | NOT RUN |
| AT-013 | Voice dialogue interruption | Five real turns; one barge-in, no stale audio | NOT RUN |
| AT-014 | Blazor integration | Provider + MiniComposer external sample screenshot/test | NOT RUN |
| AT-015 | UI component admission | Negative schema/permission and positive render tests | NOT RUN |
| AT-016 | WS resume no duplication | Disconnect/reconnect durable event sequence report | NOT RUN |
| AT-017 | TS interoperability | External Node/browser consumer with same gateway | NOT RUN |
| AT-018 | Docs assistant dogfooding | Real docs tool/agent trace and source-backed answer | NOT RUN |
| AT-019 | CI enforcement | PR with deliberate failure blocked; security checks | NOT RUN |
| AT-020 | NuGet/npm/GHCR delivery | Registry links, package consumers and image digest smoke | NOT RUN |
| AT-021 | Security worker identity/readiness/fail-closed | Authenticated API and missing-model/missing-worker probe | NOT RUN |
| AT-022 | Local rules + opt-in NER | Checksum/secret/NER/Unicode/multilingual evaluation | NOT RUN |
| AT-023 | Vault and scoped restoration | Encryption/TTL/collision/rotation and zero cross-tenant token replay | NOT RUN |
| AT-024 | Egress no-leak enforcement | Controlled model/tool/worker/NATS/telemetry canary sink report | NOT RUN |
| AT-025 | Streaming restoration safety | Every split offset, WS reconnect and authorization proof | NOT RUN |
| AT-026 | Local image/PDF privacy | OCR/exif/hidden text/face and no remote-original egress smoke | NOT RUN |
| AT-027 | Audio strict-local privacy | Remote audio denied unless approved local protection verified | NOT RUN |
| AT-028 | Security-worker container hardening | Nonroot/no model layers/ready/egress restriction smoke | NOT RUN |
| AT-029 | Firewall CI & measured regression | Fuzz and dataset leakage/overmask/latency evidence, PR negative test | NOT RUN |
| AT-030 | Security worker published image | GHCR digest, signed provenance/SBOM, fresh-host host+worker test | NOT RUN |

| AT-031 | Strongly typed enum + wire schema parity | C#/JSON/YAML/Pydantic/TS fixtures; reject numeric/unknown | NOT RUN |
| AT-032 | Options defaults/validation | DI config/JSON equivalence; invalid mode and policy matrix | NOT RUN |
| AT-033 | Interactive identity | Active user scoping/expiry with real auth middleware | NOT RUN |
| AT-034 | Scoped interactive delegation | Revocation, tool escalation and reconnect negative suite | NOT RUN |
| AT-035 | Operational workload identity | No HttpContext/user-cookie provenance in execution; tool scopes | NOT RUN |
| AT-036 | Operational scheduling | Fake clock, restart, pause, misfire, overlap, retry, kill | NOT RUN |
| AT-037 | Operational durable work | DB lease/fence + NATS replay + side-effect idempotence | NOT RUN |
| AT-038 | Operational control RBAC | Trigger/pause/review approval and tenant isolation | NOT RUN |
| AT-039 | Detector independent toggles | No initialization/download/capability when OFF; model ready when ON | NOT RUN |
| AT-040 | Coverage-based decisions | Required coverage vs available matrix; no silent fallback | NOT RUN |
| AT-041 | Lean Docker profile | Rules-only CPU footprint, no bundled extra model, readiness | NOT RUN |
| AT-042 | CI + release consumer integrity | Enum, agent modes and detector gates across NuGet/npm/GHCR | NOT RUN |


### Stable release rule

- [ ] All **P0** gates passed; P1/P2 exclusions explicitly described in release notes.
- [ ] No known unauthorized tool executions, cross-tenant restoration, raw PII in messaging/logs, or unreviewed model license violations.
- [ ] No unchecked **[SEC]** item relating to included stable functionality.
- [ ] Stable text privacy gate AT-021, AT-023–025, AT-028–030 and AT-039–040 passed (AT-022 when NER enabled) with genuine local security worker; AT-026/027 either passed or modality is unavailable, with no strict-local claim.
- [ ] Maintainer signs off release manifest and documented experimental capabilities.

## 15. Evidence log (append only; format to follow)

**Do not erase failed test evidence.** Use one row per verification attempt; `SKIPPED` is not `PASS`.

| Date (UTC) | Task / AT ID | Commit / PR | Command / environment | Result | Link or artifact |
|---|---|---|---|---|---|
| — | — | — | — | NOT RUN | — |

Example (illustration only, NOT evidence):

```text
2026-11-XX | IMP-024 / AT-007 | PR #N / <SHA> |
 docker compose up ...; dotnet test --filter JetStreamCrashRecovery |
 PASS (25 tests) | CI run URL + sanitized logs
```

## 16. Deferrals, blockers and change control

For each blocker log `{ID, owner, observed problem, prerequisite, effect on stable release, workaround, reassessment date}`. A feature can move to experimental only when absent from production default capability map, omitted from stable promises, tested in isolation, and accepted by maintainer. Do not silently omit critical platform security, durable task, client or release verification.

## 17. References

- `spec.md` — normative technical requirements and acceptance definitions.
- `firewall.md` — security-worker architecture, detection, vault, modality boundaries, test requirements and release gates.
- `implementation.md` — exact staged implementation work packages and release DAG.
- https://github.com/Vuminkosi/Swift-Aid — code extraction source.
- https://github.com/Vuminkosi/Swift-Aid/tree/master/workers — Python classification source.
- https://github.com/Vuminkosi/Swift-Aid/tree/master/infra/ellai-tts — Python MMS-TTS source.
- https://learn.microsoft.com/en-us/agent-framework/overview/
- https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing
- https://docs.npmjs.com/trusted-publishers/
