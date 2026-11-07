# OpenConnect.Navigator — Framework-Aligned Requirements

**Version:** 2.0-draft  
**Date:** 2026-11-07  
**Status:** Normative implementation target; not evidence of shipped functionality  
**Companion:** `navigator-spec.md`  
**Product:** OpenConnect — open-source, .NET-first, privacy-first reusable AI framework built around Microsoft Agent Framework (MAF).

## 0. Normative contract and source of truth

MUST/SHALL are release-blocking; SHOULD requires an ADR for deviation; MAY is optional. The existing OpenConnect `spec.md`, `implementation.md`, `checklist.md`, `firewall.md` and `configuration.md` (if present) remain authoritative for common agent, authorization, privacy, events, and package contracts. This document extends those contracts, and MUST NOT fork their semantics. Coding agents must first inspect actual repository APIs and record any mismatch in an ADR before coding. All proposed API names below require compile-time confirmation. Source-derived references and proposed changes are distinguished explicitly.

**Primary goal:** make navigation, discoverable UI guidance, onboarding, demonstrations, and optional spoken narration *reusable capabilities* of developers' AI products. OpenConnect itself ships a website/documentation portal and an OpenConnect-powered documentation assistant—not a standalone Navigator application. Demo hosts are tests and examples only.

## 1. Product fit and boundaries

- **R-GOAL-001:** Navigator SHALL be an independently installable OpenConnect extension. `AddOpenConnect()` continues to provide the default lead agent, policy, sessions, event infrastructure and tool broker. Navigator registration adds only semantic UI guidance and controlled execution contracts.
- **R-GOAL-002:** Core interactive agents, tool discovery, MCP and MiniComposer MUST continue when Navigator, Playwright and both speech workers are absent.
- **R-GOAL-003:** A developer SHOULD accomplish a read-only app tour by installing Navigator packages, registering semantic UI targets and adding a provider/bridge; no custom agent runtime or domain infrastructure is required.
- **R-GOAL-004:** Navigator SHALL reuse `IOpenConnectComponent` capability maps, descriptions and suggestions for discoverability rather than inventing a competing component registry.
- **R-GOAL-005:** Navigator SHALL expose narrow tools to OpenConnect lead/subagents through the existing allowlisted tool broker; it must not run its own general-purpose autonomous agent engine.
- **R-GOAL-006:** Navigator SHALL use the shared `agent-runtime.md` instruction hierarchy, task/session IDs, privacy policy and OpenTelemetry correlation.
- **R-GOAL-007:** Live task/event streaming SHALL use existing authenticated OpenConnect HTTP(S)/WS(S) client transport and committed events over NATS JetStream when durable; no separate SignalR transport or browser-facing NATS dependency is required. An adapter to SignalR MAY be added only where an existing host explicitly uses it.
- **R-GOAL-008:** The documentation assistant MAY demonstrate Navigator on its own documentation UI through the same published packages used by third-party products, never a privileged internal one-off implementation.
- **R-GOAL-009:** Optional QdrantVactor or other semantic-memory subscribers MAY index policy-approved, committed Navigator events through the common durable event subscription. Navigator MUST NOT depend on Qdrant, embed vectors in EventRecord or enumerate all sessions.
- **R-GOAL-010:** No TTS worker is part of the security firewall, mandatory gateway, agent execution engine or MCP capability. Speech makes presentation richer but never determines correctness or task authorization.

## 2. Deployment matrix: capability is not a dependency

| Deployment | Core lead/agents | MCP/tools | Semantic tour / highlight | Operational Playwright | Spoken narration |
|---|---|---|---|---|---|
| OpenConnect alone | Yes | Yes, when configured | No Navigator UI | No | No |
| + Navigator .NET/TS bridge | Yes | Yes | Yes, text-first | No | No |
| + Navigator Playwright adapter | Yes | Yes | Yes | Optional isolated runs | No |
| + Kokoro worker | Yes | Yes | Yes | Optional | English/installed Kokoro voices |
| + MMS-TTS worker | Yes | Yes | Yes | Optional | Licensed installed MMS languages |
| + both TTS workers | Yes | Yes | Yes | Optional | Explicit provider selection/fallback |

- **R-DEP-001:** Navigator registration MUST NOT launch Docker, models, Chromium, NATS, or other infrastructure automatically.
- **R-DEP-002:** Disabled adapters MUST create no service connection, model download, model allocation, background subscription or hosted worker instance.
- **R-DEP-003:** Worker failure or absence SHALL return truthful `unavailable/degraded` capability status and text-only UX without failing interactive/MCP tasks.
- **R-DEP-004:** Core event journal remains session-scoped; any optional projection subscriber consumes committed notifications via the transport abstraction, not journal-wide polling.
- **R-DEP-005:** Python is required for model workers, not for a minimal .NET/TypeScript Navigator installation. The operational scheduler is an OpenConnect-hosted .NET background worker; Playwright MCP is a separate isolated browser runtime and is not a Python TTS worker.

## 3. Agent execution modes and identities

- **R-ID-001:** Interactive Navigator sessions are launched by interactive agents using the **current authenticated user's** scoped principal, tenant, active session and permissions. The server reauthorizes before each action; browser data or a session ID alone confers no authority.
- **R-ID-002:** Operational Navigator work is activated and monitored by OpenConnect's existing background operational-agent worker using a *service/workload principal*, durable lease, scheduler, recovery and policy snapshot. Operational agents never inherit interactive cookies/tokens or impersonate live users.
- **R-ID-003:** Operational browser demos can use separately provisioned sandbox/demo credentials, scoped to an approved app/tenant and read-only by default; no login bypass, MFA bypass or imported live browser profile.
- **R-ID-004:** Browser/client bridge action execution is bound to a current authenticated connection and a short-lived, signed or server-state-backed command grant scoped to tenant, principal, client instance, navigator session, target, command hash, policy revision and expiry.
- **R-ID-005:** User-driven mutation requires explicit approval where configured and independent authorization by the underlying host resource/API. Consent to capture and approval to mutate are separate.
- **R-ID-006:** Authentication changes, logout, revoked tenant grants, or disconnected bridge invalidate command delivery and require state reconciliation; never replay an uncertain mutation.
- **R-ID-007:** Operational jobs have bounded leases, cancellation, retries for safe reads only, and audited completion/error states, using common OpenConnect operational-agent contracts.

## 4. Registration and developer experience

- **R-API-001:** .NET registration SHALL be strongly typed and opt-in, e.g. `.AddOpenConnectNavigator(options => ...)`, calling existing OpenConnect registration as prerequisite or failing with actionable validation.
- **R-API-002:** Existing `.AddOpenConnectClient()` configures task HTTP(S) and WS(S); Navigator client adapters reuse that instance and connection, rather than requiring a second authentication/transport stack.
- **R-API-003:** The initial generic JavaScript package SHALL be `@openconnect/navigator`, with an optional `@openconnect/navigator-react` provider that composes the existing OpenConnect provider. Blazor integration SHALL be provided by an OpenConnect component adapter where supported, not a new incompatible UI protocol.
- **R-API-004:** `OpenConnectProvider`, `MiniComposer`, and registered `IOpenConnectComponent` metadata continue to function as existing core client UX. `OpenConnectNavigatorProvider` or native bridge adds overlays/targets to that composition and MUST NOT replace MiniComposer.
- **R-API-005:** Semantic targets SHALL have stable ID/version, description, route, capability map, sensitivity, allowed actions and optional user-facing suggestions. User/agent discoverability is permission-filtered and bounded.
- **R-API-006:** Public enums SHALL define closed choices (mode, browser provider, speech provider, action, risk, capture, fallback); custom target IDs, voices, locales and provider URLs remain validated strings.
- **R-API-007:** Options SHALL be validated on startup and against worker capability handshake. Disabled feature paths cannot silently enable another provider or egress route.
- **R-API-008:** Users MAY opt in to Navigator from existing lead agents through tool-scoped capabilities; an agent without those grants SHALL not discover operational execution or browser automation tools.

## 5. Semantic UI, tours and actions

- **R-UI-001:** Registered semantic targets and accessibility-role observations take priority over generated selectors. No full-DOM scraping or direct secret form values by default.
- **R-UI-002:** Observations are bounded, current-route-scoped, filtered by user/tenant permissions, labeled untrusted and screened by the configured OpenConnect firewall before reaching an external provider.
- **R-UI-003:** Static developer-authored tours and dynamically proposed agent tours compile to the same typed, versioned, policy-validated plan. Plans are bounded by step count, time and capability budget.
- **R-UI-004:** Read-only steps include explain, locate, highlight, scroll, focus and permitted internal navigation; write steps require verified registered actions, fresh authorization, optional user approval and postcondition receipts.
- **R-UI-005:** Mutations MUST NOT be performed from unreliable best-guess selectors, hidden controls, cross-origin frames, page-authored scripts or unrestricted JS evaluation.
- **R-UI-006:** Overlays must provide next/back/skip/pause, focus safety, Escape, reduced motion, screen-reader announcements and responsive positioning without breaking host CSS.
- **R-UI-007:** Target movement/unmount/route changes require re-resolution and safe pause. No probabilistic replacement click.
- **R-UI-008:** All UI mutations produce intent, action receipt, outcome and uncertainty markers; a sent command is not proof of a successful effect.
- **R-UI-009:** Existing `IOpenConnectComponent` suggestion actions are prompts/tasks, never autonomous privileged commands.
- **R-UI-010:** No capture by default. Semantic snapshots require minimization; screenshots require scoped, revocable consent, local masking where feasible, server validation, encryption and TTL. Unsupported masking causes denial.
- **R-UI-011:** The JavaScript package is SSR-safe: no `window`/`document` access at module import. Bind to DOM only when the browser bridge starts.

## 6. Playwright MCP optional adapter

- **R-MCP-001:** Playwright MCP is an optional *isolated-browser* capability, not the required implementation of live UI navigation. Interactive UI relies on the connected client semantic bridge.
- **R-MCP-002:** Tool façade explicitly maps approved Navigator actions to a small allowlist of MCP tools. Model never sees `browser_evaluate`, unrestricted filesystem/network, arbitrary Playwright code, credential storage or unreviewed upstream additions.
- **R-MCP-003:** Per-job browser context/process, temporary storage, egress restrictions (including SSRF/DNS rebinding/redirects), fixed origin allowlists, resource budgets and separate workload identity are mandatory for operational runs.
- **R-MCP-004:** Playwright MCP results are untrusted tool output; firewall, schema validation and authorization still apply. MCP availability cannot upgrade an agent's tool rights.
- **R-MCP-005:** No Playwright service, browser download or Chromium launch when disabled. Missing browser worker does not prevent live text-based tours or MCP tools unrelated to browser automation.

## 7. Speech: independent optional Kokoro and MMS-TTS Python workers

- **R-TTS-001:** Kokoro and MMS-TTS MUST be separate deployable Docker images, separate Python 3.12+ uv-locked packages, separate ports/services, separate model volumes, separate health/capability descriptors and independently enableable Compose profiles.
- **R-TTS-002:** These are reusable OpenConnect *speech capabilities*, exposed through the shared worker/capability registry. Navigator is one possible consumer; MiniComposer, interactive agents and other OpenConnect client components MAY use them. Their images MUST NOT be branded as Navigator-only dependencies.
- **R-TTS-003:** Kokoro, when speech is enabled and no alternative is selected, defaults to English `en-US`, voice `af_heart` **only if installed and verified**. The default narration mode without any TTS worker is text-only.
- **R-TTS-004:** MMS-TTS is separately opt-in. Every language/model is explicitly selected; do not download the full MMS catalog, silently change language or assume broad language support from a generic model card.
- **R-TTS-005:** For enabled speech workers, **first container startup** MAY download only approved, pinned, allowlisted model assets into a named persistent Docker volume, after deployment-time configuration/license acceptance. If a complete verified cache exists, startup MUST reuse it with no network access. No on-demand downloads within a user synthesis request.
- **R-TTS-006:** Download must be checksum/revision-verified, guarded by exclusive per-model lock, written to temporary staging then atomically promoted. Corrupt/incomplete assets trigger controlled reprovision or fail (policy-dependent), never unverified inference.
- **R-TTS-007:** Offline startup using a verified cache MUST work without external model-host connectivity; when cache missing and network disabled, worker reports `model_unavailable`, core experience stays text-only.
- **R-TTS-008:** Worker models, artifacts, tokenizer/linguistic assets and voices have separate notices and license checks. Kokoro-82M model card lists Apache-2.0. MMS-TTS model cards list CC BY-NC 4.0; do not authorize commercial deployment on this basis alone. Different checkpoints may have different terms; verify pinned artifacts.
- **R-TTS-009:** Model downloader uses limited network egress to approved model hosts during provisioning only. Runtime inference is offline, uses no secret model download credentials in image layers, and mounts its model cache read-only after bootstrap where practical.
- **R-TTS-010:** Worker health separates liveness from readiness. `ready` is true only after model/tokenizer/voice checksum validation and actual load; unready worker must not advertise synthesis capability.
- **R-TTS-011:** Speech router exposes provider, voice, language, codec, sample rate, channels, format, chunk sequence, limits and model revision. Different workers may emit different native sample rates; never fabricate a universal sample rate.
- **R-TTS-012:** Sanitize narration through OpenConnect privacy policy. Interruption cancels playback and rejects stale chunks by speech epoch; UI progress follows actual playback acknowledgment rather than mere TTS completion.
- **R-TTS-013:** On missing, unhealthy, unlicensed or overloaded TTS, return visible text/subtitles and optional explicitly authorized alternate provider; no failed agent task, failed MCP call or aborted tour.
- **R-TTS-014:** Model caches persist across `docker compose down` / container recreation. Volume deletion requires explicit operator action; upgrades revalidate revision and provide rollback to prior compatible cache.
- **R-TTS-015:** Models are never committed to git or bundled in images by default. Multi-replica provisioning must use one initializer/lock and safe shared-volume semantics; no concurrent partial downloads.

## 8. Shared worker/events/privacy requirements

- **R-INT-001:** Events used by Navigator (`navigator.session.*`, `navigator.step.*`, `navigator.command.*`, `speech.*`) use shared versioned OpenConnect event contracts, per-session ordering and JetStream-backed transactional outbox when durable; browser receives only authorized filtered events over existing WS.
- **R-INT-002:** The common Python worker protocol and machine authentication shall be reused by Kokoro/MMS rather than a second private ad-hoc protocol; streaming binary audio may use an authenticated private HTTP stream while status/progress are sanitized durable events.
- **R-INT-003:** No raw sensitive text, screenshot bytes, audio PCM, model credentials or bearer tokens in JetStream event bodies, activity tags or Qdrant payloads.
- **R-INT-004:** Privacy firewall is default-on at protected data boundaries, with optional detectors controlled by trusted typed settings. Absent optional detectors must not be misrepresented as active. Unauthorized UI commands remain blocked even if privacy inspection is explicitly disabled.
- **R-INT-005:** Privileged actions are reauthorized at execution time; model output, UI text, MCP responses and worker payloads are never sources of policy authority.
- **R-INT-006:** Speech worker access requires scoped machine identity; gateway performs routing, quota, user/tenant authorization and text-policy checks. Workers have no database token-vault access and cannot authorize user actions.
- **R-INT-007:** External/provider egress is policy-controlled; local TTS does not make remote text transfer mandatory.
- **R-INT-008:** Operational event subscribers see only sanitized committed notices; QdrantVactor and observability remain separately installable.

## 9. Availability, failures, retention, observability

- **R-OPS-001:** A missing/disabled speech worker is not a readiness failure for the gateway, Navigator or MCP. Only explicitly configured required dependencies may gate host readiness.
- **R-OPS-002:** A missing/disabled Playwright worker is not a readiness failure for interactive Navigator.
- **R-OPS-003:** Persist navigation session state, versioned plans, safe action receipts and operational leases using existing OpenConnect persistence adapters. No duplicate domain database or new identity service.
- **R-OPS-004:** Capture is opt-in and ephemeral by default; encrypted retained artifacts, consent and retention deletion follow existing vault/secret policy.
- **R-OPS-005:** OpenTelemetry spans include navigation step, capability resolution, action authorization, worker selection, provisioning status and speech latency; safe model/voice revision metadata permitted. No raw input, DOM text or audio content.
- **R-OPS-006:** Distinguish billed provider cost from estimated CPU/GPU runtime costs. Worker counts and memory load are metrics, not guessed billing.
- **R-OPS-007:** Worker resource limits and circuit breakers must be configurable; over-capacity produces retryable bounded error and text-only UI.
- **R-OPS-008:** Compose documentation includes independent `navigator-browser`, `kokoro`, `mms-tts` profiles, persistent volumes, offline reuse, license gates, health probes and clear cold-start behavior.

## 10. CI, migration and releases

- **R-REL-001:** Reuse source from local `C:\DevOps\SwiftAidv2` where available, or the pinned corresponding GitHub commit if not; inspect `infra/ellai-tts/{kokoro_voice.py,app.py,voice_pool.py,Dockerfile.kokoro,Dockerfile,pyproject.toml,uv.lock,tests}` and related integration; record source SHA/license/file map and modifications. Source observation is not confirmation of copy rights for every model/dependency.
- **R-REL-002:** Swift-Aid currently combines Kokoro and MMS paths; the extraction SHALL deliberately split Python worker packages and shared generic modules. Remove Swift-Aid-specific application/case semantics and legacy hardcoded image dependencies.
- **R-REL-003:** CI MUST test core without Navigator, Navigator without Playwright/TTS, Kokoro alone, MMS alone (license/config gates), and both together; install-time dependency checks and contract validation.
- **R-REL-004:** PR CI SHALL use fake models and cached test fixtures without network/model downloads. Separate gated integration workflows test approved first-run download, restart/offline cache, checksums, interrupted download, bad license and real synthesis.
- **R-REL-005:** Independently package Navigator .NET NuGet and TypeScript npm packages, plus two GHCR images with immutable tags/digests and SBOM/license notices; verify clean external package consumers.
- **R-REL-006:** Do not claim operational Playwright, actual TTS quality, GPU, browser or published registry results without recorded real execution evidence.
- **R-REL-007:** Full release gates include threat-model review, connector authorization, license verification, no cross-tenant disclosure, no unauthorized mutations, and text-only fallback under every optional-worker failure.

## 11. Acceptance matrix (new IDs avoid collision with OpenConnect core AT tests)

| Test | Required evidence |
|---|---|
| NAV-AT-001 | Existing core lead + MCP still run without any Navigator/TTS/Playwright registration |
| NAV-AT-002 | Navigator static text tour with bridge, no model workers/browser |
| NAV-AT-003 | Interactive navigator action cannot exceed authenticated user's grants |
| NAV-AT-004 | Operational job uses isolated workload identity and background lease/recovery |
| NAV-AT-005 | Unregistered/ambiguous/stale target never causes a mutating click |
| NAV-AT-006 | Approval hash mismatch, expired grant, reconnect and revocation deny execution |
| NAV-AT-007 | React SSR-safe import; host MiniComposer + Navigator provider coexist |
| NAV-AT-008 | Only committed, safe Navigator events become visible on existing WS; duplicates deduped |
| NAV-AT-009 | Optional Playwright absent: live tour still works; arbitrary MCP browser tools refused |
| NAV-AT-010 | Capture denied or pixel-redacted under sensitive/ambiguous field scenarios |
| NAV-AT-011 | Kokoro-only first start provisions pinned `af_heart`, checks SHA, returns correct audio metadata |
| NAV-AT-012 | Kokoro restart with network denied uses persisted cache; no download attempted |
| NAV-AT-013 | MMS-only provisions exactly opted-in licensed language checkpoint; no full catalog fetch |
| NAV-AT-014 | MMS commercial/unapproved license scenario blocked; core text tours still work |
| NAV-AT-015 | Interrupted download cannot corrupt active validated cache; retry recovers |
| NAV-AT-016 | Concurrent initializer instances cannot expose partial model files |
| NAV-AT-017 | Missing/failed both TTS workers: interactive, MCP, Navigator complete text-only |
| NAV-AT-018 | Speech stop/barge-in rejects stale chunks and maintains tour checkpoint |
| NAV-AT-019 | Client/worker options enum validation and capability handshakes reject invalid combinations |
| NAV-AT-020 | End-to-end GHCR image install + persistent volume + NuGet/npm consumer smoke |
| NAV-AT-021 | Observability/log/NATS/capture artifacts contain no seeded sensitive canaries |
| NAV-AT-022 | Model provenance, artifact notices, code migration ledger and release security scan complete |
| NAV-AT-023 | Operational Playwright worker SSRF, profile reuse and tool-allowlist adversarial tests pass |
| NAV-AT-024 | Event consumer optional QdrantVactor absent causes no Navigator failure or requirement |

## 12. Delivery order and exit conditions

1. **P0 extension fit:** align repository APIs, core agent modes, unified transport/registry, contracts, threat model, source/license audit.
2. **P1 text-only:** semantic target metadata, bridge, provider, static tours/highlighter, no side effects, no speech or browser required. Releaseable preview after security tests.
3. **P2 interactive safety:** agent tool facade, approvals, durable commands, reconnect and read-only dynamic tours.
4. **P3 two speech workers:** split Swift-Aid sources, independent Python services, first-run model provisioners + persistent caches, explicit license/checksum gates, text fallback.
5. **P4 isolated browser:** optional Playwright MCP adapter and operational-agent background jobs, secure egress, audited captures.
6. **P5 full release:** opt-in capture, observability, resource testing, docs assistant integration, NuGet/npm/GHCR publishing and external consumer verification.

**Definition of done:** all enabled feature paths have tests, reproducible configuration, authorization/redaction boundaries and real evidence. Missing optional capability is shown as unavailable and cannot block unrelated OpenConnect functionality.

## 13. Source references

- OpenConnect uploaded prior specification and implementation files are the internal baseline; validate actual repository APIs before changing them.
- Swift-Aid: https://github.com/Vuminkosi/Swift-Aid and local `C:\DevOps\SwiftAidv2` (local path not accessible to this document author; coding agent working on that machine must inspect it).
- Source candidates: `infra/ellai-tts/kokoro_voice.py`, `app.py`, `voice_pool.py`, `Dockerfile.kokoro`, `Dockerfile`, `pyproject.toml`, `uv.lock`, tests.
- Playwright MCP: https://github.com/microsoft/playwright-mcp
- Kokoro model card: https://huggingface.co/hexgrad/Kokoro-82M
- MMS-TTS model card: https://huggingface.co/facebook/mms-tts
- Microsoft Agent Framework: https://learn.microsoft.com/en-us/agent-framework/overview/
