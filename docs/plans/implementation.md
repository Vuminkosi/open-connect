# OpenConnect — End-to-End Implementation Plan

**Status:** Executable engineering plan (target, not a status report)  
**Version:** 1.1-draft | **Date:** 2026-11-06  
**Source of truth:** `spec.md` and `firewall.md` requirement IDs. Track execution and evidence in `checklist.md`.

## 1. Instructions for Codex / Muse / DeepSeek / any coding agent

1. Read `spec.md`, then `firewall.md`, then this file, then `checklist.md`, then the changed area's Swift-Aid source and tests. Never treat plans as implemented code.
2. Work **one `IMP-*` unit per PR** (or cohesive small group), with contract test first. Do not create dozens of placeholders or claim success based on compilation alone.
3. Every completed task produces code, tests, docs, evidence (`command -> result`), updated checklist and any new ADR. Mark blocked work honestly.
4. Source tree is new OpenConnect; Swift-Aid(C:/DevOps/SwiftAidv2) is a read-only reference. First pin its actual commit SHA with `git rev-parse HEAD` or GitHub API and record it in `docs/migration/SOURCE-LOCK.md`. Avoid importing Swift-Aid `implementation.md` wholesale: its requirements are domain-specific.
5. Test first against fakes, then Docker services, then real models, then packaged consumers. No simulated successful real-model claims.
6. Changes to `contracts/`, package locks, solution configuration, EF migrations, `.github/workflows/`, `Directory.Packages.props`, or release settings require designated single PR ownership and explicit code review.
7. Separate trusted runtime policy from Markdown/prompts and model outputs. Secure failure is more important than a polished demo.
8. Never commit secrets, customer data, protected prompts, production tokens, model artifacts without licensing, or historical Swift-Aid patient/case records.
9. Never release/publish without explicit maintainer approval and protected GitHub Environment; CI is safe to run unattended, release workflows require configured approvals.

## 2. Definition of done for each task

A task is DONE only if: acceptance criteria from spec pass; `dotnet build/test` or corresponding Python/npm commands pass; new public APIs have XML docs/schema examples; failure/authorization tests included; no unintended dependencies introduced; migration/source attribution recorded where relevant; checklist evidence contains exact commands and outputs (or linked CI job); agent provides changed-path summary and known limitations. Tasks that touch external systems need integration evidence. If blocked by model license/registry/account, mark BLOCKED and continue unrelated work.

## 3. Target repository tree

```text
openconnect/
  AGENTS.md                      # coding-agent hard rules and task protocol
  spec.md  implementation.md  checklist.md  firewall.md  LICENSE  README.md
  global.json  Directory.Build.props  Directory.Packages.props
  OpenConnect.slnx
  .github/
    CODEOWNERS  dependabot.yml
    workflows/{ci.yml,contract-compat.yml,release.yml,publish-workers.yml,docs.yml,security.yml}
  src/
    OpenConnect.Abstractions/
    OpenConnect.Core/
    OpenConnect.Agents/
    OpenConnect.Privacy/
    OpenConnect.Sessions/
    OpenConnect.Tools/
    OpenConnect.Workers.Contracts/
    OpenConnect.Transport.Nats/
    OpenConnect.Persistence.Postgres/
    OpenConnect.Hosting.AspNetCore/
    OpenConnect.Client/
    OpenConnect.Components.Contracts/
    OpenConnect.Components.Blazor/
    OpenConnect.Observability/
    OpenConnect.Voice/            # optional after foundation
  packages/
    client-ts/                    # @openconnect/client
  workers/
    common/                       # shared Python package (envelope, leases, auth, health)
    classification/
    security/                     # local-only privacy detection worker
    mms-tts/
    nllb/
    inference/
    transcription/
    realtime-voice/
  contracts/
    openapi/openconnect.v1.yaml
    jsonschema/{agent-definition.v1.json,capability.v1.json,worker-envelope.v1.json,event.v1.json,component.v1.json,security-scan.v1.json,security-scan-response.v1.json}
    fixtures/{valid,invalid}/
  infra/
    compose.yaml
    compose.dev.yaml
    nats/{nats.conf,provision-streams.py,acl.md}
    postgres/  otel/
    docker/                       # reusable Docker build patterns
  site/                           # docs/marketing website and AI docs assistant
  docs/
    architecture/adr/
    migration/{SOURCE-LOCK.md,swiftaid-inventory.csv,LICENSE-AUDIT.md}
    getting-started/ security/ workers/ clients/ api/ release/
  examples/
    minimal-host/                 # developer SDK smoke, not standalone product
    minimal-blazor/               # developer integration smoke
  tests/
    Unit/ Integration/ Contracts/ Security/ PropertyBased/ E2E/ PackageConsumers/
  scripts/
    verify.sh  verify.ps1  validate-contracts.py  package-smoke.sh  release-verify.sh
```

Packages listed above are logical modules; only create a physical project when it contains meaningful code. Choose .NET target framework supported by pinned MAF/MudBlazor and tested build agents (prefer current supported LTS after verifying), and Python 3.12 initially to maximize source compatibility.

## 4. Dependency and release graph

```text
Abstractions
  -> Core -> Agents -> Hosting.AspNetCore
  -> Tools -> Agents
  -> Privacy -> Core/Hosting adapters
  -> Sessions -> Persistence.Postgres
  -> Workers.Contracts -> Transport.Nats -> Hosting.AspNetCore
  -> Components.Contracts -> Components.Blazor -> Client
Client <- OpenAPI contracts
@openconnect/client <- OpenAPI contracts
workers/common <- JSON schema + worker protocol
workers/* <- workers/common
site <- public NuGet SDK + @openconnect/client where applicable
```

Prevent circular project references; align package versions for a single framework release. `Components.Contracts` should not reference Blazor; Core should not reference MudBlazor. Worker protocol and client protocol share schema version rules, not assumptions about runtime language.

## 5. Workstream 0: audit, licensing and baseline (IMP-001 through IMP-006)

**IMP-001 Repository setup:** initialize clean OpenConnect git repository, branching rules, `.editorconfig`, deterministic NuGet restore, central packages, locked dependencies, Python uv workspaces or per-worker locks, npm lock, conventional commits, reusable local verification script. `AGENTS.md` mandates evidence and security rules. **Accept:** clean checkout builds minimal solution + test harness.

**IMP-002 Swift-Aid source lock:** using GitHub connector or local clone, pin `master` SHA, enumerate files in `src/SwiftAid.Application/Ellai`, `src/SwiftAid.Infrastructure/Messaging`, `src/SwiftAid.Infrastructure/Persistence/Ellai`, `src/SwiftAid.Gateway/Ellai`, `workers/src/swiftaid_workers`, `infra/ellai-tts`, test files. Record path, observed behavior, license, dependencies, risk and unit tests in CSV. **Accept:** every proposed copy links source SHA/path and engineer decision.

**IMP-003 Legal audit:** verify Swift-Aid repo LICENSE, copyright, copied file headers, dependencies, MMS/NLLB/classifier model terms, Silero ONNX notice and redistribution restrictions. If license unclear, **rewrite instead of copying** until rights established; preserve attribution. Never bundle model weights automatically. **Accept:** `LICENSE-AUDIT.md` with artifact decisions and third-party notices.

**IMP-004 Contract governance:** design versioned OpenAPI, event JSON schema, component schemas, worker envelope and fixture corpus. Introduce tooling for C# JSON serialization, TS types and Pydantic validations generated/verified against canonical JSON schema. Include schema compatibility diff test. **Accept:** valid fixtures accepted identically by all three runtimes; invalid rejected.

**IMP-005 Threat model:** STRIDE/LINDDUN-style boundary matrix for host, browser, WS, NATS, Postgres, tool adapter, PII vault and Python workers. Explicit cross-tenant, prompt injection, SSRF, replay, model data leakage and worker compromise. **Accept:** tests linked to each high-risk boundary.

**IMP-006 Build baseline:** tests + formatting/security scanning workflow initial branch. Pin actions to reviewed SHAs in production or use centrally approved versions; never introduce release credentials into PR jobs. **Accept:** failed test blocks protected branch.

## 6. Workstream 1: contracts and core DI (IMP-010–IMP-019)

**IMP-010 Abstractions:** define IDs as strongly typed value objects (tenant/principal/session/task/agent/execution/capability), DTO validation, `IClock`, `IIdGenerator`, `IPrivacyPolicy`, `ICapabilityRegistry`, `IAgentDefinitionRegistry`, `IToolCatalog`, `ITaskStore`, `IEventJournal`, `IOutboxStore`, `IWorkerRouter`, `IModelBoundary`, `IEventSink` and errors. Never embed framework-specific agents in transport DTOs. **Tests:** serialization golden fixtures and argument validation.

**IMP-011 Composition:** `AddOpenConnect(Action<OpenConnectOptions>?)` idempotently registers safe lead agent and privacy firewall enabled by default, options validation on startup. No background Docker launching. `MapOpenConnect()` maps versioned endpoints; fail helpfully when production persistence/transport missing. **Tests:** repeated DI registration, missing model, privacy on/off.

**IMP-012 Runtime instructions:** load `agent-runtime.md` from configured content root and resolve agent-specific instruction files with path traversal prevention, UTF-8 validation, size limit, hash revision and safe reload; markdown instructions have no authority to alter security config. **Tests:** malicious Markdown, path escape and invalid encodings.

**IMP-013 YAML parser:** schema-versioned options; resolve agents, delegates, cap requirements and tool references. Validate cycles, duplicate IDs, unknown capability, unsupported model semantics, timeouts/depth/budget. Do not allow YAML-authored secrets. **Tests:** negative fixture set.

**IMP-014 MAF adapter:** inspect installed Microsoft Agent Framework actual APIs and compile a small lead-agent + tool-calling test; implement adapter to MAF agents/workflows; do not fork agent runtime. Translate real MAF lifecycle events to execution trace records. **Tests:** actual delegation and parent-child events, no fake nodes.

**IMP-015 Capability registry:** registration, TTL/health, bounded allowlisted projection, model modality mapping, privacyClass and worker matching. Capability map versioning. **Tests:** invalid claims and missing workers.

**IMP-016 Tool broker:** descriptors, input/output schemas, dynamic discovery filtered by agent allowlist ∩ authenticated scope ∩ tenant policy ∩ task. Server reauth before invocation, bounded tool calls, approval binding to canonical arguments hash. **Tests:** cross-tenant/tool escalation, typed invalid args, expired approval.

**IMP-017 Host privacy enforcement (REVISED):** establish default-on immutable policy, .NET interception at model/tool/worker/output/NATS boundaries, strict fail-closed behavior and encrypted token-vault interfaces. All Python detection and local NER belong in `workers/security`, not embedded in .NET. Link implementation to `firewall.md` OC-FW-001–048 and follow-on IMP-080–089. Reversible placeholders never become authorization tokens. **Tests:** tenant isolation, unauthorized egress blocked when security worker is missing, collision, tool restoration and leakage fixtures.

**IMP-018 Provider adapter:** use MAF's `IChatClient` integration where applicable, register model metadata and expose only supported capabilities; hosted/local provider adapters are explicit. **Tests:** two providers with matching contract; unsupported tool-call fail.

**IMP-019 Runtime smoke:** minimal .NET host with `AddOpenConnect`, one permitted safe read tool, one subagent and redaction. **Accept:** real task returns safe result and trace; no domain-specific SwiftAid dependencies.

## 6A. Strongly typed configuration and dual-mode agent runtime (IMP-090–IMP-099)

**Read `configuration.md` before coding.** This is a normative extension and supersedes earlier string-only examples or an assumption that all security models must be loaded. Explicitly resolve compatibility between options and `openconnect.yaml`/JSON schemas before implementing agents. A typed enum is not a trust decision: only host-controlled policy grants authority.

**IMP-090 — Enum and wire taxonomy:** implement `AgentExecutionMode`, `AgentActivationMode`, `AgentIdentityMode`, operational run states, privacy/policy/detector/device/load enums, side effects, transport and model location in core types. Stable string mapping, reject numeric/unknown, JSON Schema and Pydantic parity, versioned fixtures. **Gate AT-031.**

**IMP-091 — Options composition and validation:** implement `OpenConnectOptions`, nested typed `PrivacyOptions`, `SecurityDetectorOptions`, `AgentRegistryOptions`, `OperationalAgentOptions`, scheduling options; support DI lambda, strongly typed registration helpers, configuration binding and `IValidateOptions` on startup. Validate mode/identity match, schedule, required detector coverage, review queue and NATS durable dependencies. Default lead interactive, privacy enabled, rules-only model profile; no Docker launch on DI. **Gate AT-032.**

**IMP-092 — Interactive execution context:** make current authenticated principal/tenant authorization snapshot a server-owned `AgentExecutionContext`, never an injectable client value. Carry scoped task and session IDs through MAF without raw bearer/cookies; delegate with equal-or-narrower tool grants; reauthorize at every protected call and scope change. Cancellation/reconnect must not revive prior privileges. **Gates AT-033/034.**

**IMP-093 — Operational agent supervisor:** add `OpenConnectOperationalAgentSupervisor : BackgroundService`; registry of explicitly enabled operational agents with schedules or event triggers; short-lived workload identity, stable revision, time zone, misfire, concurrency, retries, budgets, heartbeat, lease monitoring, paused/failed states and alerts. Do not access `HttpContext` or silently inherit the triggering user's rights. **Gates AT-035/036.**

**IMP-094 — Durable operational runs:** PostgreSQL run/schedule/review/lease tables, CAS/fencing, transactional outbox, NATS work events and idempotent run/side-effect semantics. Reconcile after restart and stale leases; ACK only after durable outcome. **Gate AT-037.**

**IMP-095 — Control plane and monitoring:** typed operational status and authorized trigger/pause/resume/cancel/review APIs, operator RBAC, immutable auditable approval argument hashes, redacted OTel metrics and separation of initiator provenance from workload identity. Mixed mode transitions require explicit authorized enqueue or review. **Gate AT-038.**

**IMP-096 — Optional detector loaders:** Python `workers/security` Pydantic enum/settings parity; independent rules/GLiNER/DeBERTa/Qwen3Guard/PaddleOCR/YuNet enabling with checkpoint and resource budgets. Off means zero import-time load, requests, downloads or advertised capability. Implement disabled/loading/ready/degraded/failed lifecycle. **Gate AT-039.**

**IMP-097 — Coverage-aware policy:** determine mandatory coverage per content class, destination and trust zone, use actual worker manifest to compute achieved coverage; policy actions `Allow/Mask/Redact/Block/Review`; required-unavailable block or real review or verified local-only. No implicit fallback. Host is final policy engine. **Gate AT-040.**

**IMP-098 — Lean Docker profiles:** CPU rules-only Compose and opt-in per-detector model mounts, quotas, nonroot, read-only artifacts, model hashes, startup readiness, no request-time downloads, tested cold-start and SIGTERM. Avoid shipping all models by default. **Gate AT-041.**

**IMP-099 — Distribution/CI:** extend required CI for enum schema compatibility, invalid configuration, interactive user scope, operational workload isolation, supervisor crash/lease/replay, detector toggles/resource bounds; NuGet XML docs and npm TS enum string parity, site quickstarts, signed security image/release manifest. **Gate AT-042.**

**Execution DAG:** IMP-090 -> 091 -> [092, 093, 096]; IMP-092 requires IMP-014/016/020/026; IMP-093 -> 094 -> 095 require IMP-020–024; IMP-096 requires IMP-081, and -> 097 -> 098 requires IMP-083/084; IMP-099 depends on 090–098 and baseline release tasks. DO NOT mark the earlier mandatory NER claim in IMP-082/AT-022 as required for a rules-only profile: it is mandatory only for configurations that advertise semantic PII coverage. Publish actual tested capability subsets.


## 7. Workstream 2: durable sessions, NATS and API (IMP-020–IMP-029)

**IMP-020 PostgreSQL:** new EF migrations for sessions, accepted turns, tasks, sanitized event journal, outbox, agent revisions, approvals and token mapping; expected-version CAS and proper indexes. Use testcontainers PostgreSQL; do not import Swift-Aid production schema. **Tests:** concurrent accept, duplicate request and failed migration recovery.

**IMP-021 Idempotent admission:** `clientTaskId` and canonical content hash scoped to authenticated tenant+principal; commit task + acceptance event + outbox in one transaction. Reply 202 with task/session IDs, duplicate identical request returns same resource, differing payload 409. **Tests:** retry races.

**IMP-022 NATS transport:** copy ideas from Swift-Aid `JetStreamPublisher.cs`, not its subject naming. Provision/verify streams out-of-band with least-privilege admin account; publisher `Nats-Msg-Id`, bounded payloads and ACK receipts. Workers cannot create streams. **Tests:** broker down, mismatched config, dedup receipt.

**IMP-023 Outbox dispatcher:** poll/claim outbox with lease/skip-locked and fencing, publish and mark delivered only after broker ACK; recover after crash. Domain event outbox data must not contain sensitive raw text. **Tests:** NATS disconnect/restart, duplicate publish and delay.

**IMP-024 Worker completion protocol:** NATS notice -> authenticated host `claim` -> scoped payload fetch -> model execution -> `complete/fail` -> durable transaction -> JetStream ACK. Use lease ID and attempt counters; expired lease cannot commit, retries are idempotent. **Tests:** completion response lost, worker kill, invalid digest, poison quarantine.

**IMP-025 Session projection/replay:** per-session sequence allocated under transaction; sanitized durable replay and bounded event retrieval; cursor expiry and resnapshot. **Tests:** out-of-order broker events; repeated event IDs.

**IMP-026 HTTP endpoints:** implement OpenAPI routes and typed errors/ProblemDetails; auth policies and quotas; authentication inherited from ASP.NET host, not embedded identity service. Server never trusts body tenant. **Tests:** unauthenticated, wrong tenant, size limits and malformed messages.

**IMP-027 WS endpoint:** authenticated `/events`, session-scoped subscriptions, short-lived ticket for cross-site where required, origin check, bounded send queues, resume cursor and heartbeat, safe cancellation. **Tests:** reconnect, slow consumer and expired auth.

**IMP-028 Observability:** OTel activity/spans and redacted event metrics, correlation across HTTP/NATS/worker; no actual prompt text in log fields. **Tests:** sensitive canary absent from logs/spans.

**IMP-029 Crash recovery demo:** launch Compose Postgres + NATS + host; create session, accept 10 turns, restart host, reconnect and replay without committed duplicates. **Accept:** AT-005, 006, 007, 016 pass.

## 8. Workstream 3: Python worker unification (IMP-030–IMP-039)

**IMP-030 Shared Python worker kit:** Python 3.12 `uv` locked dependency management; `openconnect_worker_common` Pydantic event/claim/commit models; auth, health/readiness, redacted logging, model manifest, NATS pull consumer, lease renewal, cancellation, timeout and durable completion semantics. Build CPU CI harness using dummy executor, not large model downloads. **Tests:** pytest/ruff/mypy, schema parity and fault injection.

**IMP-031 Classification extraction:** audit `workers/src/swiftaid_workers/classification` source; remove case/workspace disease semantics; abstract class labels, schema, language gating, abstention and model revision. Port BART classification only if checkpoint/license cleared, otherwise pluggable engine. **Tests:** retained unit tests + real model smoke gated on model cache.

**IMP-032 MMS-TTS extraction:** move/adapt `infra/ellai-tts` Python application into `workers/mms-tts`, preserve voice pool, LRU model limits, auth, pinned checksums, sample rate, cancellation semantics and tests. Remove SwiftAid header/prefix without breaking metadata version compatibility (new v1 protocol). **Tests:** existing synthetic tests + actual checkpoint smoke.

**IMP-033 NLLB:** implement Python adapter for pinned NLLB model, explicit src/tgt language support, bounded token length, known language mappings, resource monitoring and translation quality test corpus. Do not claim already in `workers/src`. **Tests:** valid pair, invalid pair, load failure, real inference opt-in.

**IMP-034 Self-hosted inference:** Python adapter for configured serving engine (e.g., compatible local endpoint or Python inference backend), define chat/stream/tool/embeddings separate capability descriptors; do not emulate missing features. Provide CPU fixture mode and GPU optional Compose profile. **Tests:** inference, cancellation, capability mismatch, actual engine smoke.

**IMP-035 Transcription:** Python STT worker providing recorded audio and streaming when supported, explicit codec/rate/language metadata, bounded upload/storage and privacy zone classification. **Tests:** synthetic fixtures and real speech smoke.

**IMP-036 Realtime dialogue:** Python voice dialog worker with VAD, speech epochs, barge-in and turn state, STT/TTS adapters, queue cancellation and metrics; gateway remains durable source of accepted turns. **Tests:** stale chunk rejection, network interruption, 5-turn live smoke.

**IMP-037 Docker standardization:** one Dockerfile per worker, non-root, multi-stage/uv-locked, pinned base digests, no model weights in published images unless licensed; supply cache mounts and pinned model manifests. Each image has liveness vs real readiness distinct. **Tests:** container smoke + startup-no-model readiness false + SIGTERM.

**IMP-038 Capability handshake:** authenticate worker registration, publish actual ready model/language/task capability descriptors with TTL; no fake advertised support. **Tests:** worker dies/stale registration/new worker matching.

**IMP-039 Compose infrastructure:** base Postgres/NATS/host, profile per worker (`security`, `classification`, `mms-tts`, `nllb`, `inference`, `transcription`, `realtime-voice`), optional CUDA profiles; sample secret `.env.example` contains placeholders only. Docs: ports, volumes, memory, GPU, model acquisition/licensing, rollback, cold-start and requirements. **Accept:** base works without workers; each opt-in profile independently smoke-tested.

## 8A. Workstream 3A: dedicated security worker and firewall (IMP-080–IMP-089)

**Normative detail:** `firewall.md` controls the exact Python security-worker architecture, `.NET` policy/evaluation boundary, JSON schema, multi-modal confidentiality rules, local detection, data vault and test suite. This workstream is a dependency of any stable privacy-first claim, not an optional untested enhancement. An unavailable worker MUST prevent unmasked external egress in strict mode. The runtime MUST NOT automatically start Docker.

**IMP-080 Security contract and threat-model refinement:** create `security-scan.v1.json`, `security-scan-response.v1.json`, content classification taxonomy, synthetic positive/negative fixture suite, license/model provenance evaluation and security tests per `firewall.md`. **Accept:** C#/Python contract parity, threat-model review, approved model acquisition plan.

**IMP-081 Python security-worker bootstrap:** `workers/security` with Python/uv, bounded private authenticated scan API, `GET /health/live`, true detector-dependent `GET /health/ready`, capability manifest and resource caps. Worker does not hold vault access or infer authorization. **Accept:** mTLS/machine identity, bogus identity denied, model unavailable returns unready, no raw logs.

**IMP-082 Local rules + opt-in NER:** implement checksum/rule/known-values detection, an optional pinned local NER checkpoint, Unicode span validation, deterministic overlap resolution, secrets priority and language-gated capability claims. **Accept:** golden fixtures, licensed pinned model, multilingual/financial blind regression report and readiness truthfulness.

**IMP-083 Host adapter, tokenization and encrypted vault:** host-side .NET scan client validates worker spans and applies immutable tenant policy; issue cryptographically random placeholders with per-tenant+principal+session+turn TTL-scoped encrypted values and host-only key material. **Accept:** key rotation/expiry, cross-tenant replay denial, worker never granted restoration.

**IMP-084 Egress & streaming integration:** enforce at every MAF model invocation, subagent, retrieval, tool argument/result, Python worker dispatch, output, WebSocket, NATS and telemetry path; require explicit field-scoped restoration; test chunk splits and reconnect. **Accept:** no raw canary reaches an unauthorized sink, no unauthorized tool restore, no partial-token streaming leak.

**IMP-085 Local multimodal image/document protection:** add optional local OCR, segmentation and metadata sanitization. Unsafe or unavailable modality blocks strict-mode external egress. Do not send original images to hosted detectors. **Accept:** image/PDF hidden text/EXIF adversarial fixtures and actual local image model tests before stable label.

**IMP-086 Audio privacy constraints:** enforce local STT/audio processing in strict mode or block unsupported external audio tasks; gate TTS and realtime voice paths. **Accept:** real audio boundary tests; no false privacy promise based on post-transcription masking.

**IMP-087 Worker Docker profile:** dedicated `security` Compose profile, non-root locked image, pinned local model manifest/checksum, no request-time download, minimal network egress, readiness and resource limits. **Accept:** clean-host start, absent model unready, pinned model ready, worker restart blocks/recoverably resumes egress.

**IMP-088 Security CI and evaluation:** implement required `security-worker-unit`, `security-worker-contracts`, `firewall-integration`, `firewall-adversarial`, `firewall-image` jobs plus scheduled model-backed tests; synthetic canaries and cross-tenant zero-violation gates. **Accept:** deliberate security regression fails protected PR CI, real evidence in checklist.

**IMP-089 Release and operations:** publish signed `openconnect-worker-security` GHCR image with SBOM/digest; smoke against actual host + worker, write operator docs/model notices/failure/incident procedures, include manifest in coordinated release. **Accept:** AT-021–030 according to feature labels; image/audio can remain unavailable, but text firewall and fail-closed gate must pass before stable v1.

**Workstream dependency correction:** Contracts/security worker bootstrap (IMP-080–082) can proceed alongside core (IMP-010–016), but host IMP-017 / IMP-083–084 and strict-mode live integration MUST pass before AT-001, AT-008, stable website claims, or publishing any NuGet packages branded privacy-first.

## 9. Workstream 4: .NET client, Blazor UI and npm (IMP-040–IMP-049)

**IMP-040 C# task client:** typed `IOpenConnectClient`, `CreateTaskAsync`, `CancelTaskAsync`, `GetTaskAsync`, `GetSessionAsync`, `GetCapabilitiesAsync`, `SubscribeEventsAsync`, cancellation and reasonable retry only for safe/idempotent operations. Typed failures + telemetry.

**IMP-041 `AddOpenConnectClient`:** DI registers named HttpClient and WS transport with base HTTPS/WSS URLs, auth bearer/cookie handler integration and per-render-mode constraints. No NATS client in browser. **Tests:** URL validation, auth renewal, client disposal.

**IMP-042 Blazor provider:** `OpenConnectProvider` coordinates cascading client state, active session, component catalogue, suggestions and event listeners; ensure markup actually wraps child content or uses scoped services. Provide working layout for interactive server and WebAssembly render modes.

**IMP-043 MiniComposer:** MudBlazor FAB bottom-right; popover/dialog/panel conversation, input submit, stream deltas, loading/error/cancel, suggestion chips, default responsive placement, focus and ARIA, double-click idempotence. Do not assume MudBlazor providers absent in host. **Tests:** bUnit/Playwright, keyboard, mobile viewport and disposal.

**IMP-044 Component descriptor:** `IOpenConnectComponentDescriptor` + Blazor renderer registration `AddOpenConnectComponent<T>()`; JSON schemas, capability map, suggested actions, safety classification, immutable ID/version. Keep metadata in `Components.Contracts`; rendering in Blazor only. **Tests:** ID collision, schema mismatch, unauthorized visibility.

**IMP-045 Dynamic proposal admission:** verify server-issued component descriptor ID/version, authorized capability and bounded validated props; map ID to registered Blazor renderer. No arbitrary code/HTML/JS/script injection. Suggestions use task API, privileged writes require confirmation. **Tests:** 100+ malicious proposal fixtures.

**IMP-046 UI execution tree:** consume actual agent/tool/worker events, parent-child, statuses, duration, privacy-safe metadata, replay from journal. No synthetic agent execution traces. **Tests:** cancellation and nested traces.

**IMP-047 TypeScript client:** derive types from OpenAPI/events, ESM build with `.d.ts`, browser and Node transport adapters, reconnect/resume/AbortSignal, API parity with C# client. `npm ci`, strict type check, Vitest/Node tests, browser Playwright. **Tests:** same gateway contracts and two-turn conversation.

**IMP-048 Package-consumer examples:** minimal external Blazor and TS Node apps consuming local NuGet and npm tarballs, never project references; verify published public surface. **Tests:** install, compile, run.

**IMP-049 Browser E2E:** authenticated demo, MiniComposer task send, stream, component proposal, reconnection, auth expiry, XSS probes, screenshot accessibility checks. **Accept:** AT-014/015/016/017.

## 10. Workstream 5: website, documentation and assistant (IMP-050–IMP-055)

**IMP-050 Website:** static-first documentation/marketing site, searchable versioned docs, examples, packages, workers, privacy policy/threat model, troubleshooting, API reference, release notes and changelog. Site should be independent of inference outages.

**IMP-051 Assistant:** docs-only lead and `docs.search`/`docs.read` subagent/tools with strict read-only access; uses published OpenConnect host/client; uses public docs index. Source-cited responses, no hidden privileged repo access. Should be an authentic dogfood implementation, not hand-authored mock outputs.

**IMP-052 Getting started:** 5-minute minimal-host and Blazor tutorials, Docker Compose Quickstart and worker opt-in recipes; no mandatory worker if hosted model supplied. Copy/paste compile/run tests.

**IMP-053 Reference:** generated OpenAPI, all JSON schemas, `agent-runtime.md` example, permission matrix, capability descriptor docs, event replay and component examples; docs source linked in CI.

**IMP-054 Migration:** explicit Swift-Aid provenance and boundaries; show code intentionally rejected, copied-with-attribution or rewritten.

**IMP-055 Publish site:** PR previews without production secrets, protected deploy environment, link checker and assistant smoke; stable docs version tied to framework release.

## 11. CI pipeline design (IMP-060–IMP-066)

Use GitHub Actions; create workflows as runnable files in repo. **Do not claim YAML snippets are already verified**. Pin workflow actions to audited commit SHAs when finalizing. Required PR checks and branch protections: `dotnet`, `python-workers`, `typescript`, `contracts`, `integration`, `security`, `package-consumers`, and `docs` where relevant. Cache only dependency inputs keyed to locks; publishing jobs avoid shared untrusted caches.

**IMP-060 `ci.yml` trigger:** `pull_request`, `push` to `main`, `workflow_dispatch`. `permissions: contents: read`; concurrency cancel superseded PR runs. Jobs:

```yaml
name: CI
on:
  pull_request:
  push:
    branches: [main]
  workflow_dispatch:
permissions:
  contents: read
jobs:
  dotnet:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4   # PIN to reviewed immutable SHA before merge
      - uses: actions/setup-dotnet@v4 # PIN
        with:
          global-json-file: global.json
      - run: dotnet restore OpenConnect.slnx --locked-mode
      - run: dotnet format OpenConnect.slnx --verify-no-changes --no-restore
      - run: dotnet build OpenConnect.slnx -c Release --no-restore -warnaserror
      - run: dotnet test OpenConnect.slnx -c Release --no-build --logger trx
  typescript:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4 # PIN
      - uses: actions/setup-node@v4 # PIN
        with: {node-version: '24', cache: npm, cache-dependency-path: packages/client-ts/package-lock.json}
      - run: npm ci
        working-directory: packages/client-ts
      - run: npm run lint && npm run typecheck && npm test && npm run build && npm pack --dry-run
        working-directory: packages/client-ts
```

**IMP-061 Python matrix:** jobs for `workers/common`, `security`, `classification`, `mms-tts`, `nllb`, `inference`, `transcription`, `realtime-voice`; use `astral-sh/setup-uv` pinned, `uv sync --locked`, `uv run ruff check .`, `uv run mypy src`, `uv run pytest -q`; huge models omitted from PR CI; opt-in real-model scheduled/manual GPU checks. Adapt command to final per-worker pyproject structure.

**IMP-062 Contract compatibility:** validate JSON Schema via `check-jsonschema`/Python jsonschema; generated TS/C# schema files match checked-in originals; OpenAPI lint (Spectral or equivalent); backwards compatibility check against last release; negative fixtures, event snapshots, privacy log canaries. Breaking changes require version bump and formal approval.

**IMP-063 Integration:** Linux Docker with Postgres and NATS testcontainers/Compose; run DB migrations, NATS stream bootstrap/ACL validation, crash/replay tests, Python dummy worker cross-language, minimal external consumer. Poll health instead of sleeping fixed times; publish isolated logs and test results with redaction.

**IMP-064 Security:** Dependabot, CodeQL for C#/JS/Python as applicable, `dotnet list package --vulnerable`, `npm audit --omit=dev` (with reviewed policies), `pip-audit` or `osv-scanner` for locked Python dependencies, secret scan (Gitleaks), container Trivy/Grype scan, SBOM SPDX/CycloneDX, license report, SAST. Never blindly fail open on scanner outage; document conditional blocking policy.

**IMP-065 Docs:** markdown lint, link validation, code snippet compile/run, website build, basic browser E2E and accessibility checks. Site preview uses no privileged production assistant credentials.

**IMP-066 Nightly:** expensive model-backed verification, soak (1000 events), fault injection (NATS/host restarts, worker kill), CPU/GPU workload matrix where hardware exists, model hash/provenance checks, package install canary. Report skipped capability explicitly.

## 12. NuGet, npm and Docker release workflows (IMP-070–IMP-079)

### 12.1 Release policy and versioning

- Version `vMAJOR.MINOR.PATCH` is one coordinated framework release. Packages share version even when unchanged; do not publish an empty package. Schema major changes require explicit protocol major increment and compatibility test.
- Git tag + protected `release` environment + approving maintainer + full green checks; no publication from `pull_request`, no secrets in untrusted checkout, no direct manual developer pushes to package registries.
- Use repository-hosted GitHub Actions and OIDC trusted publishers for NuGet/npm where registry account supports it. Check account/workflow name/scope setup before first publish.
- `CHANGELOG.md` by change category; generate release manifest with Git SHA, version, package list, image digests, checksums, SBOM, attestation references and provenance.
- Prepare pre-release packages on `vX.Y.Z-rc.N`, publish with npm `next` dist-tag and NuGet prerelease suffix; stable tagged `vX.Y.Z` publishes npm `latest`.

### 12.2 `release.yml` architecture (do not blindly copy without config)

**IMP-070 Preflight:** `on: push: tags: ['v*']` or `workflow_dispatch` with explicit version; verify annotated tag, protected ref, `git describe`, package metadata vs tag version, changelog, repo clean, tag commit CI green, uniqueness via registries, required approvals, license audit and signing policies. Refuse duplicate release.

**IMP-071 Build once:** one release job with `contents: read` builds NuGet `.nupkg`+`.snupkg`, npm `.tgz` per package and SBOM as immutable workflow artifacts with SHA256. Do not recompile package source separately in publish jobs; use artifact provenance/digest verification. Pack commands:

```bash
dotnet restore OpenConnect.slnx --locked-mode
dotnet test OpenConnect.slnx -c Release
dotnet pack OpenConnect.slnx -c Release --no-build -p:PackageVersion="$VERSION" -o artifacts/nuget
cd packages/client-ts
npm ci && npm run build
npm pack --pack-destination ../../artifacts/npm
```

Pin deterministic NuGet metadata: `PackageId`, `Authors`, `Description`, `PackageLicenseExpression` (when applicable), `RepositoryUrl`, `PackageReadmeFile`, `PackageTags`, `IncludeSymbols=true`, `SymbolPackageFormat=snupkg`, `PublishRepositoryUrl`, `ContinuousIntegrationBuild`. Every `.nupkg` name/version checked against manifest.

**IMP-072 NuGet publish:** configure nuget.org Trusted Publishing policy matching `owner/repo`, exact release workflow filename (`release.yml`) and optional GitHub environment `release`. Request `id-token: write` **only on publisher job**; use `NuGet/login@v1` and exchange OIDC for short-lived `NUGET_API_KEY`, then:

```yaml
- name: NuGet OIDC login
  id: nuget
  uses: NuGet/login@v1  # pin to reviewed SHA
  with:
    user: ${{ secrets.NUGET_USER }}
- name: Publish NuGet
  shell: bash
  env:
    NUGET_API_KEY: ${{ steps.nuget.outputs.NUGET_API_KEY }}
  run: |
    set -euo pipefail
    for pkg in artifacts/nuget/*.nupkg; do
      dotnet nuget push "$pkg" --api-key "$NUGET_API_KEY" --source https://api.nuget.org/v3/index.json
    done
    for sym in artifacts/nuget/*.snupkg; do
      [ -e "$sym" ] || break
      dotnet nuget push "$sym" --api-key "$NUGET_API_KEY" --source https://api.nuget.org/v3/index.json
    done
```

Avoid `--skip-duplicate` as silent success in first release; on retry, explicitly compare published version/hash and record partial state. Acquire OIDC credential near push (short-lived). Avoid exposing secret in script tracing.

**IMP-073 npm publish:** configure npm Trusted Publisher for each `@openconnect/*` package with exact GitHub repo/workflow/environment. Use Node 24 and npm **>=11.5.1**, GitHub-hosted runner, `id-token: write`, `contents: read`. Publish from verified extracted pack contents or verified source tree with corresponding digest; **npm publish of a tarball must be tested with your npm/OIDC tooling** (prefer `npm publish ./artifacts/npm/name-version.tgz --access public --tag latest` when supported). Confirm current npm registry rules; do not set `NODE_AUTH_TOKEN` if using OIDC.

```yaml
- uses: actions/setup-node@v4 # pin to reviewed SHA
  with:
    node-version: '24'
    registry-url: 'https://registry.npmjs.org'
    package-manager-cache: false
- run: npm --version
- name: Publish npm (OIDC)
  env:
    TAG: ${{ startsWith(github.ref_name, 'v') && contains(github.ref_name, '-') && 'next' || 'latest' }}
  run: npm publish ./artifacts/npm/openconnect-client-*.tgz --access public --tag "$TAG"
```

Make glob resolve to exactly one tarball (shell explicit count); if not, fail. Trusted publisher provenance is automatic for supported public GitHub repos; verify provenance on registry. Multi-package releases publish in dependency order, no parallel mutable dist-tags.

**IMP-074 Docker GHCR:** `publish-workers.yml` triggered by approved tag/dispatch after worker tests; buildx multi-platform where dependencies support it, one image per worker with separate context, immutable tag `vX.Y.Z` and SHA, push to GHCR with `GITHUB_TOKEN` (`packages: write`, `contents: read`), no exposed private model assets. Build remote supply-chain attestations/SBOM; sign digest with keyless cosign when permitted. Use `docker/build-push-action` pinned; `docker/metadata-action` pinned. Validate images run as nonroot, health/readiness, verify model optional asset behavior. No overwrite of release tags.

**IMP-075 Smoke after public distribution:** clean .NET consumer `dotnet add package OpenConnect.Hosting.AspNetCore --version X.Y.Z`, clean Blazor consumer `dotnet add package OpenConnect.Components.Blazor`, clean TS consumer `npm install @openconnect/client@X.Y.Z`, pull images by digest; compile/run against disposable Postgres/NATS with live selected provider. Registry propagation may require bounded retry and clear failure; don't mark release complete if package unavailable.

**IMP-076 GitHub Release:** only after verification, release notes, manifest, checksums, SBOM, signed attestation/links and quickstart; tag considered incomplete if any channel failed. Do not hide partial release.

**IMP-077 Rollback:** NuGet/npm versions typically immutable; release corrective patch, deprecate affected npm packages if necessary and record rollback advisories. Roll back deployments by pinned previous image digest, not deleting streams/DB; migrations forward-only unless explicit recovery plan.

**IMP-078 Branch and registry protection:** GitHub `main` PR-only, required status checks, CODEOWNERS for security/contracts/workflows, release environment approvals, trusted publisher workflow scope, secret scanning; release bots restricted permissions and release concurrency one-at-a-time.

**IMP-079 Supply chain:** SPDX SBOM, dependency license audit, third-party model artifacts provenance, image scan severity policy, attest artifact SHA, package metadata and checksum manifest; actions pinning and release workflow review gates.

### 12.3 Suggested GitHub Actions job DAG

```text
preflight -> [dotnet, python, typescript, contracts, security, integration]
          -> pack-artifacts -> [publish-nuget, publish-npm, publish-ghcr]
          -> verify-registry-consumers -> publish-github-release -> deploy-versioned-docs
```

`publish-*` jobs must read immutable built artifact outputs from `pack-artifacts`, not mutable branches, and use protected release environment approval. Consider staged sequential channels if atomic coordination isn't possible; record partial status automatically. Reserve `id-token: write` for OIDC jobs and `packages: write` only for GHCR publisher.

## 13. Commands / developer workflow

```bash
# Repo
./scripts/verify.sh
# .NET
dotnet restore OpenConnect.slnx --locked-mode
dotnet build OpenConnect.slnx -c Release --no-restore
dotnet test OpenConnect.slnx -c Release --no-build
# Python (example per-worker path)
cd workers/classification
uv sync --locked
uv run ruff check .
uv run mypy src
uv run pytest -q
# npm
cd ../../packages/client-ts
npm ci
npm run lint && npm run typecheck && npm test && npm run build
# Infrastructure (base services no workers)
docker compose -f infra/compose.yaml up -d nats postgres
# Worker profile example
docker compose -f infra/compose.yaml --profile classification up -d classification
```

Every task PR description template:

```markdown
## Task
IMP-XXX / OC-AREA-XXX / AT-XXX
## Swift-Aid source references
SHA, files, license decision, changed semantics
## Changes
Concrete files/packages/contracts
## Tests and evidence
Command -> result -> CI link, real model? yes/no
## Security/privacy review
Boundary, authorization, PII/secret/log effects
## Compatibility
Schemas, NuGet/npm/image, migrations
## Known limitations / follow-up
Explicitly documented
```

## 14. Work order and stage gates

| Stage | Minimum tasks | Gate |
|---|---|---|
| S0 Audit | IMP-001–006 | Attribution + source SHA + CI + schemas established |
| S1 Core | IMP-010–019 and IMP-080–084 | Lead, host privacy interlock, Python security scan, token vault, tools, real MAF delegation |
| S2 Durable | IMP-020–029 | Ten turns; crash/resume; NATS redelivery safe |
| S3 Blazor/client | IMP-040–049 | MiniComposer and component contracts end-to-end |
| S4 Workers | IMP-030–039 plus IMP-085–089 | Security worker first for strict-mode egress; classification/MMS extraction; other modalities individually gated |
| S5 Docs | IMP-050–055 | Docs assistant truly uses framework packages |
| S6 Release | IMP-060–079 | CI + package-install smoke + trusted OIDC publishing |

These stages can partially overlap but contracts/security/durability are blocking prerequisites for external release. Worker features are independently releasable as `experimental` until their real model acceptance evidence exists.

## 15. Known design risks and mitigation

| Risk | Mitigation |
|---|---|
| Swift-Aid domain logic contaminates framework | Extraction ledger and generic contract tests before copy |
| MAF API changes | Pin tested versions; adapter isolation + compilation smoke |
| At-least-once NATS produces duplicate user output | DB dedup + per-session seq + committed ACK rules |
| Privacy disabled interpreted as no security | Separate always-on authN/authZ and negative tests |
| Masked prompts still leak surrounding facts | Threat model, fail-closed strict mode and documentation |
| Huge GPU images/model licensing | Separate optional tags, downloads through approved pinned model volumes |
| WS cursor inconsistency | DB-generated per-session sequence, replay expiry handling |
| Untrusted component activation | Local allowlist, schemas, bounded props, approval for writes |
| Registry trusted publisher unavailable | BLOCK release; configure official account policy instead of leaking permanent API key |
| Partial multi-registry release | Signed release manifest, explicit recovery/re-publish plan |

## 16. Final first stable release criteria

All P0 items (lead, defaults, privacy with healthy local security worker and host-owned fail-closed enforcement, tool security, MAF integration, durable sessions, NATS, HTTP/WS, Blazor MiniComposer, component registry and .NET client) pass their AT tests; TS SDK either passes or is explicitly deferred/version-labeled; at least one real Python worker demonstrates claim/commit and model inference; docs assistant actually uses OpenConnect; package consumers install released NuGet/npm versions; CI and OIDC release channels verified. No privacy/tenant isolation violations in adversarial tests. All unsupported worker modalities explicitly marked experimental/unavailable.

## 17. External references

- `firewall.md` — normative security worker, policy, vault, streaming, CI and release instructions.

- https://github.com/Vuminkosi/Swift-Aid
- https://github.com/Vuminkosi/Swift-Aid/tree/master/workers
- https://github.com/Vuminkosi/Swift-Aid/tree/master/infra/ellai-tts
- https://learn.microsoft.com/en-us/agent-framework/overview/
- https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing
- https://docs.npmjs.com/trusted-publishers/
