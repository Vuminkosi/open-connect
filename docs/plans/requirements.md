# OpenConnect — Unified Product and Engineering Requirements

**Document:** `requirements.md` · **Baseline:** Consolidated 2026-11-07 · **Status:** Normative implementation target, not evidence of completed work  
**Companions:** [`spec.md`](./spec.md) and [`checklist.md`](./checklist.md).  
**Source:** Every specialist document uploaded for this consolidation is retained in a labeled annex within the three-file master set; no functional workstream is intentionally removed.

## 0. Purpose, precedence and scope preservation

OpenConnect is an **open-source, .NET-first, privacy-first, configurable and reusable agentic AI framework based on Microsoft Agent Framework**. Developers initialize lead agents, interactive agents, operational agents, subagents, tool/MCP discoveries, `agent-runtime.md`, and capability maps, then integrate secure task/voice/UI capabilities into their own products. The official OpenConnect product-facing application is its **static-first documentation website, with an optional OpenConnect-powered documentation assistant**. Other UI examples, sample applications, admin surfaces and test hosts are framework deliverables or optional integrator components—not an additional official end-user product.

This master set is intended for Codex, Muse, DeepSeek and human maintainers. It does **not** claim any proposed APIs compile, any model has been run, any release workflow has published, or any acceptance test has passed.

**Precedence order for actual implementation:**
1. **This document §§0–18**: product goals, mandatory scope, authoritative conflict decisions, acceptance boundaries.
2. **`spec.md` canonical §§0–21**: package interfaces, inter-service protocols, data models, deployment and engineering implementation contracts.
3. **`checklist.md` canonical sections**: all work packages, test gates, evidence and execution order.
4. **Source-preservation annexes** inside these files: detailed original requirements, tests, examples and reasoning. Retain as requirements and design evidence except where a canonical decision explicitly supersedes an incompatible sentence, API name, or hard dependency. An obsolete clause is not permission to drop the underlying feature; carry it into the canonical design.

All source requirement identifiers (`OC-*`, `R-*`, `ACR-*`, `OBS-*`, `REQ-*`, `EF-AT-*`, `NAV-AT-*`, `CMP-AT-*`, etc.) are retained verbatim in the relevant annex; implementation work IDs are preserved or explicitly mapped. Requirements omitted from the top-level summary **remain in scope** through the annexes and traceability checklist. MUST/SHALL = mandatory for advertised stable capability, SHOULD = default with recorded rationale for deviation, MAY = opt-in extension. Experimental feature labeling may defer release gates, not erase scope.

## 1. Non-negotiable architectural requirements

- **OC-MSTR-001 — Framework, not application:** provide composable NuGet and npm SDKs, opt-in Docker workers, ASP.NET Core hosting and secure defaults. The documentation site is the only official product website; the docs assistant must dogfood published/real OpenConnect packages.
- **OC-MSTR-002 — Microsoft Agent Framework:** reuse supported MAF agents, workflows, tools, and model integration. Never implement a competing general orchestration engine. Verify pinned MAF APIs and event hooks against actual code before implementation.
- **OC-MSTR-003 — Startup DX:** `.AddOpenConnect()` registers a default **interactive lead agent** and default-on host firewall policy without launching Docker, running schema migrations or loading optional model checkpoints.
- **OC-MSTR-004 — Config first:** strongly typed .NET `Options` and enums for closed choices, typed registration APIs, versioned JSON/YAML serialization, strict validation on startup and runtime policy revisions; agent instructions do not grant permission.
- **OC-MSTR-005 — Two execution modes:** Interactive agents assume the current authenticated user’s derived scoped authority; operational agents run under independently authorized constrained workload identities and a durable .NET `OpenConnectOperationalAgentSupervisor : BackgroundService`. Never silently transfer interactive credentials to operational agents.
- **OC-MSTR-006 — Tool security:** server-authorized tools/MCP capabilities, descriptions, typed schemas, side-effect classes, approvals, bounded execution, provenance and per-agent capability maps; discovery is not authority.
- **OC-MSTR-007 — Privacy boundaries:** policy, egress authorization, reversible opaque tokenization, AES-GCM envelope encryption, protected vault and authorized restoration remain in .NET; optional Python security worker executes independently enabled rules/AI detectors. Required missing coverage fails closed.
- **OC-MSTR-008 — Durability:** accepted tasks/session/events/transactional outbox persisted atomically where a real relational provider allows it. In-memory mode must not claim durability. JetStream is the internal durable publication/subscription mechanism for configured distributed deployments; HTTP(S)/WS(S) are public task/event transports.
- **OC-MSTR-009 — Provider-neutral storage:** `.AddOpenConnectEntityFramework<TContext>()` uses developer’s own EF Core provider, supports existing DbContext and dedicated context, no forced PostgreSQL, and no SQL Server/SQLite/Npgsql/InMemory provider package transitively pulled into OpenConnect EF adapter. Manual migrations default.
- **OC-MSTR-010 — Optionality:** QdrantVactor, Infisical KMS, Navigator, Playwright MCP, Kokoro, MMS-TTS, NLLB, inference, classification, STT, real-time voice, Aspire, OTLP exporter, admin analytics, richer UI adapters all install only when selected. Missing unrelated extensions must not disable core interactive/MCP/text capabilities.
- **OC-MSTR-011 — Thin composer:** default UI is the small, discreet, draggable anchored MiniComposer (separate FAB launcher and compact bubble), not a full-screen chat application. Same interaction contract across Blazor and browser composer; headless Node remains DOM-free.
- **OC-MSTR-012 — Real events:** client status, subagent execution tree, progress, operational runs, usage, voice playback and UI actions reflect actual committed/runtime events, not model-generated fictional activity.
- **OC-MSTR-013 — Privacy-safe observability:** OpenTelemetry is composable and optional exporters/Aspire never gate the firewall. Durable model metering and usage ledger are separate from sampled traces; user/admin isolation and minimal content telemetry apply.
- **OC-MSTR-014 — Multi-repo/source hygiene:** reuse safe generic ideas/code from the designated reference only after revision pin, model/dependency/license review, extracted test migration and removal of domain-specific business/health data. Necessary copyright/third-party notices must remain; public runtime should not contain borrowed app branding.
- **OC-MSTR-015 — Release correctness:** GitHub Actions gated CI, clean package consumer tests, versioned contracts, NuGet/npm trusted publishing when available, optional GHCR worker images, SBOM/provenance, security tests and documented partial-release recovery.

## 2. Required developer integration contracts

### 2.1 Host and persistence

Target fluent API (illustrative until implemented and compiled):

```csharp
builder.Services.AddDbContext<ApplicationDbContext>(db =>
    db.UseSqlServer(builder.Configuration.GetConnectionString("Default")));

builder.Services.AddOpenConnect(options =>
{
    options.Agents.DefaultExecutionMode = AgentExecutionMode.Interactive;
    options.Privacy.Enabled = true;
    options.Privacy.Mode = FirewallMode.StrictLocal;
});

builder.Services.AddOpenConnectEntityFramework<ApplicationDbContext>(options =>
{
    options.Schema = "openconnect";
    options.Migrations = OpenConnectMigrationMode.Manual;
});

builder.Services.AddOpenConnectNats(/* independently configured transport */);
```

The existing context imports `modelBuilder.ApplyOpenConnectModel(...)` explicitly; `DbSet<T>` properties are NOT required. SQL Server/PostgreSQL may use `openconnect` schema, SQLite `oc_` table prefixes, and EF Core InMemory is explicitly non-durable/test-only. A dedicated `OpenConnectDbContext` may share the same database/connection string, but cross-context ACID coordination is **not** automatic. Registration does not create schema objects. Dedicated-context startup migration MAY be explicitly enabled with tested provider/cluster controls; shared application context migrations remain application-owned and manual. This storage decision **supersedes PostgreSQL-required clauses** in earlier source specifications and checklists, but preserves every session/task/event/outbox/lease/approval/privacy-token/operational/usage entity requirement.

### 2.2 Interactive and operational agent registration

Interactive agent: user-initiated via HTTP/WS/SDK; scope derived from authenticated server context, reauthorize before consequential tools; subagent delegation narrows permissions. Operational agent: scheduled, event-triggered, continuous or explicitly enqueued; monitored by .NET background supervisor, workload credential, lease/fence, retry and review controls; no ambient `HttpContext`. Mixed-mode transitions require explicit authorized enqueue/review, never silent delegation.

### 2.3 Client integration

`.AddOpenConnectClient()` accepts task API HTTPS and event WSS URI. Blazor uses `<OpenConnectProvider>` and `<MiniComposer />`; browser uses `@openconnect/client` (headless) plus optional `@openconnect/composer`, and React/browser bindings when installed. Do not add global styling, browser-to-NATS, or a parallel agent/session system. `IOpenConnectComponent` describes permitted UI, discoverable contracts, capability maps, descriptions and suggestions with local allowlisted renderer and schema validation.

## 3. Product requirements by capability

### 3.1 Gateway, agent, MCP and event contracts

- Reusable task/session API with idempotent task acceptance, authorization, cancellation, streaming deltas, bounded cursor replay, reconnect, typed errors and output projections.
- Authenticated HTTP(S)/WS(S); NATS JetStream durable committed event subjects/consumers, transactional outbox, bounded payloads/leases/ACK-after-commit and dead-letter recovery. No sensitive raw prompt/audio/PII in broker events. Session journal remains scoped to known SessionId; introduce a separate committed agent event subscription interface for optional global subscribers.
- Declarative `agent-runtime.md` and agent/subagent definitions; validated tool discovery and permission-limited MCP adapters; event-driven actual execution tree; per-agent model capability selection.
- Optional QdrantVactor `.AddQdrantVactor(configure)` subscribes to committed, policy-approved agent events, resolves scoped sanitized projections, generates embeddings asynchronously, upserts idempotently, filters retrieval by tenant/principal and handles deletion/revocation. No required Qdrant, global journal enumeration or vector property on EventRecord.

### 3.2 Privacy firewall and vault

- Default-on .NET enforcement; configurable trusted host-only privacy-off state must not disable authentication, tool authorization or tenant isolation.
- Separate optional Python security-worker image, running local rules by lightweight baseline. GLiNER PII, DeBERTa injection, Qwen3Guard safety, PaddleOCR image text and YuNet faces are **independent opt-in detectors**, load only if enabled, declare actual coverage and resource demands. Optional model downloads are pinned and gated, never per request.
- The security worker detects; the gateway owns allow/mask/redact/block/review verdicts, secret and tenant policies, vault encryption, token scopes and streaming authorized restoration.
- Default encrypted token mapping store may use the provider-neutral EF Core adapter with AES-256-GCM envelope encryption, protected persistent local key for development, optional Infisical KMS for key wrapping, TTL/rotation and no cross-scope restoration.
- Strict-local mode describes the trusted processing zone, **not** universal detector quality: if policy requires semantic PII, image, face or audio coverage absent from active detectors, prevent external egress. Never ship raw originals to remote OCR/STT in that mode. HTML/screenshot masking must modify actual pixels/layers and verify metadata.
- Every model, tool, RAG, event, worker and output boundary is protected by a policy admission point; streamed placeholders are restored only to approved fields under current authorization.

### 3.3 Entity Framework and persistence

- Required entity set covers sessions, tasks, turns, committed events, outbox, leases, approvals, encrypted privacy tokens, operational definitions/runs/schedules/checkpoints/reviews, component proposals, usage/ledger and optional module storage. Module tables registered explicitly, not by blanket assembly scan.
- Shared host context imports mappings, with application-owned migrations. Dedicated context and migration history are optional. Compatibility across SQL Server, PostgreSQL, SQLite and EF InMemory (development only) with provider-aware claims and concurrency test suites. Outbox dispatch never holds a DB transaction across network publication.
- Transactional boundary required for accepted task+event+outbox and any caller business writes claimed to be atomic; sharing a connection string is insufficient. On failure do not acknowledge accepted tasks before commit.

### 3.4 MiniComposer and cross-platform clients

- Visually anchored bottom-right launcher and compact ~23.5–24rem bubble; preserve source reference anatomy: drag handle, settings, stop, close, one-to-five-line editor, dynamic mic/live/send, bounded latest sent/response/partial transcript, animated three-bar status and independent `Voice on` / `Mic on`.
- Launcher is standalone fixed FAB, no reserved page column or uninvited overlay. Bubble is movable with handle/touch/keyboard, clamped on viewport changes, optional attached mode for Navigator, safe default focus and no host CSS collisions. Legacy `20vh` launcher position remains optional, **not** default.
- Close/minimize must not silently cancel a committed task; Stop is explicit cancellation; keyboard Enter send, Shift+Enter newline, IME safe. Status from actual event stream, replay idempotent and input current-value safe.
- Unavailable speech hides/disables only voice controls; text, tools/MCP and components work. Browser mic requires user gesture and independent playback toggle; stale speech epochs discarded. Accessibility WCAG 2.2 AA target, RTL, reduced motion, safe areas, browser SSR, Blazor hydration and visual-regression screenshot evidence.

### 3.5 Navigator and optional browser automation

- Optional `.AddOpenConnectNavigator(configure)` and browser/Blazor bridge reuse UI discovery, component metadata, task IDs, gateway auth and events; semantic target registry, bounded observations, tours/highlighting, suggestions, consent and approvals.
- Read-only observation/help should operate without Playwright and TTS. Browser action rights cannot exceed active user or operational workload principal. Mutation needs registered target, typed schema, exact-bound grant, postcondition receipt and review when required.
- Optional isolated Playwright MCP operational adapter runs only under constrained workload identity, curated safe browser tools, per-job browser profile and network/SSRF restrictions. No arbitrary code evaluation, browsing secrets or cross-tenant contexts.
- Navigator can optionally narrate through separate Kokoro and MMS-TTS workers, but those are generic OpenConnect speech capabilities, never prerequisites for interactive agents, MCP or tours.

### 3.6 Python workers, model provisioning, voice and media

- All executable model workers are independent Python 3.12+ projects/images with shared contracts, authenticated readiness, resource limits and targeted deployment profiles; optional workers are classification, NLLB, self-hosted inference, transcription, real-time dialogue, Kokoro TTS, MMS-TTS and the security detector worker.
- Kokoro and MMS-TTS each have **separate Docker images, Compose profiles, independent named model volumes**, pinned manifests/checksums, controlled first-start model downloading into persistent cache, atomic staging+promotion, verified offline cache reuse, no user-request-time downloads, and provider-specific language/voice metadata. Kokoro English `af_heart` default only if present and verified. Confirm MMS checkpoint license suitability before distribution and use.
- Voice runtime distinguishes recorded transcription, live turn/interrupt/VAD, reasoning, synthesis, playback-delivered acknowledgments and fenced speech epochs; missing TTS/STT degrades to text without breaking agent task/MCP.
- Workers must advertise actual modality/languages/model/checkpoint/detected capability, with no phantom support, silent privacy-changing fallback or mandatory GPU profile.

### 3.7 Observability, accounting, admin and Aspire

- OpenTelemetry `ActivitySource`, `Meter`, safe structured logs and optional OTLP exporter/collector and .NET Aspire integrations. Hosted exporters and dashboards are not prerequisites to model execution.
- **Durable per-provider-attempt usage ledger** with pricing version/currency, token subcategories, provenance (reported/estimated/unknown), late usage corrections, decimal calculations and idempotent retries. Tracing sample decisions cannot erase costs.
- Firewall traces show enabled/disabled/skipped/running detector states, latency, coverage and security decisions without content; operational/interactive agent traces show different identities and real lifecycle.
- Budget reservations, alerts and tenant-scoped administrative APIs/read models/UI. The admin UI is an **optional embeddable/operator component**, not a second official public OpenConnect application. It must work with provider-neutral storage, not a PostgreSQL-only backend.
- Secure retention, authorized audit/export, no high-cardinality user/tenant IDs in metrics, no raw prompts or tokens, measured SLOs and clear cost estimate versus billing truth.

### 3.8 Website and documentation assistant

- Astro Starlight static-first developer documentation website with versioned Markdown/MDX, navigation/search, generated DocFX .NET API and OpenAPI reference, code samples, architecture diagrams, tutorials and developer quickstarts.
- Optional backend service uses public OpenConnect runtime, read-only documentation agent/retrieval with citations, version filtering, privacy gates, authentication/anonymous restrictions and session isolation. Static docs must remain available when assistant/backend down. Optional UI composer is the compact SDK component, not a separate chat application.
- CI-generated docs, tested copy/paste C# examples, preview deployments and approved release site deployment. No client-side provider API keys.

### 3.9 Supply chain, CI and release

- GitHub Actions PR/push pipelines for .NET, TS/npm, Python/uv, schema parity, EF provider matrix, auth/security/PII canary tests, real DB/NATS fault recovery, UI screenshot/a11y, docs, package consumers and image scans.
- Protected version tags and release environment; NuGet and npm trusted publisher with OIDC if configured; GHCR worker images by digest, software bill of materials, signed provenance, license/model notices and clean external install verification.
- Optional capability release gates individually verify actual models; no model weights or secrets baked into ordinary packages; partial release failure is visible and recoverable without mutating published immutable versions.

## 4. Explicit reconciliation decisions (do not silently select outdated alternatives)

| Conflict across uploaded documents | Canonical decision | What remains in scope |
|---|---|---|
| Core mandates PostgreSQL vs new EF proposal | **Provider-neutral optional EF Core adapter** supporting SQL Server, SQLite, PostgreSQL, EF InMemory dev; Manual migrations default | All durable entities, outbox, fencing, replay and crash testing; PostgreSQL remains supported |
| Docs/admin UI vs official website-only product | Official OpenConnect app = docs site + optional assistant; **Admin API/UI is a reusable optional operator surface** in integrator-hosted systems | Full cost, budget, audit and detector analytics scope retained |
| NER always on vs resource-efficient detectors | Rules enabled default; GLiNER/DeBERTa/Qwen3Guard/PaddleOCR/YuNet independently opt-in; policy-required coverage still fails closed | Full detector, OCR, safety, injection and benchmark requirements retained |
| Strict privacy and optional security-worker Docker | Host firewall always installed; worker deployment opt-in, but protected egress **blocked** until required local detector ready | No unsafe automatic fail-open; local-only processing may work according to policy |
| `AddOpenConnectPostgres` vs `AddOpenConnectEntityFramework<TContext>` | Prefer generic EF adapter for relational stores; old Postgres adapter may be maintained only as compatibility shim if actually implemented | Provider-specific optimizations remain internal/optional |
| `OpenConnect.Gateway` vs `OpenConnect.Hosting.AspNetCore` | Adopt host package `OpenConnect.Hosting.AspNetCore` as canonical, aliases only with reviewed migration | All gateway HTTP/WS API and security requirements remain |
| `OpenConnect.Firewall` vs `OpenConnect.Privacy` | Canonical enforcement package `OpenConnect.Privacy`; Python worker separate | Detector/policy/vault modules retain their functions |
| NATS required vs small installs | NATS JetStream chosen durable distributed event backbone **when registered**, not direct browser dependency; in-process dev transport allowed, explicitly non-durable | Durable subscription/outbox/operational reliability goals retained |
| Old 20vh launcher vs bottom-right | Default bottom-right; `LegacyViewport20Vh` preset supported | Compact screenshot fidelity and draggable/attached composer behavior retained |
| Kokoro/MMS combined reference service vs independent containers | Two separate opt-in Python services and caches, first-start controlled download | All voice features, language coverage checks and image tests retained |
| Observability default store Postgres vs EF-neutral | Metering and audits use provider-neutral persistence abstractions/EF mappings when selected; no PostgreSQL forced | Dedicated immutable usage ledger, budgets and admin API retained |
| Operational agents on active user token | Operational workload identity only; interactive inherits current user | Both execution modes, schedules, approvals and delegated tools retained |
| Infisical as vault itself vs key management | PostgreSQL/EF encrypted token mappings by default, optional Infisical as KEK wrap/unwrap adapter | Key rotation, vault isolation, restoration policy retained |
| Qdrant vs IEventJournal known-session replay | Separate committed event subscription (NATS) and optional projection/embedding subscriber | Optional semantic retrieval, deletes and reindexing retained |
| MiniComposer as heavy chat panel vs anchor bubble | Small FAB+card as default; long-form conversation opens only user-requested host-approved view | History, component rendering, real progress and settings retained |
| Hosted workers always present vs modular stack | Never launch/download without explicit registration; truthful capabilities and text-only fallbacks | Every worker and multimodal requirement remains in complete roadmap |
| Source instruction says never mention reference while requiring licenses | No borrowed app branding in public product UX; **mandatory legal attributions/third-party notices remain** | Source provenance and license audit must not be erased |

## 5. Requirement and acceptance ownership map

| Domain | Preserved normative source identifier set | Technical implementation/test set |
|---|---|---|
| Foundation/core | `OC-ARC`, `OC-CFG`, `OC-AGT`, `OC-CAP`, `OC-PRV`, `OC-EVT`, `OC-CLI`, `OC-API`, `OC-REL` | `IMP-*`, `AT-001–042` |
| Typed options + dual mode | `OC-CFG-010–029`, `OC-AGT-020–039` | `IMP-090–099`, `AT-031–042` |
| Firewall/vault | `OC-FW-001–064` | `IMP-080–089`, `AT-021–030`; optional models tested separately |
| EF adapter | `OC-EF-001–096` | `EF-IMP-001–016`, `EF-AT-001–030` |
| Navigator | `R-GOAL`, `R-DEP`, `R-ID`, `R-API`, `R-UI`, `R-MCP`, `R-TTS`, `R-INT`, `R-OPS`, `R-REL` | `NAV-IMP-001–020`, `NAV-AT-001–024` |
| MiniComposer | `ACR-UX`, `ACR-VIS`, `ACR-TXT`, `ACR-AUD`, `ACR-SDK`, `ACR-A11Y`, `ACR-LIFE`, `ACR-OPS` | `CMP-IMP-001–014`, `CMP-AT-001–024` |
| Observability | `OBS-001–025`, `REQ-TEL`, `REQ-GW`, `REQ-MET`, `REQ-FW`, `REQ-AG`, `REQ-MM`, `REQ-ADM`, `REQ-ASP`, `REQ-SEC`, `REQ-CFG`, `NFR` | `OBS-IMP-*` created in canonical checklist; specialist detailed verification preserved |
| Website | Doc platform instructions §§1–19 | `SITE-IMP-*`, `SITE-AT-*` from canonical checklist |
| Optional vector memory / KMS | Integration proposals and core event/privacy contract | `VEC-IMP-*`, `KMS-IMP-*` in canonical checklist |

## 6. Release completeness and documentation rules

A feature is **defined in scope** even when its code is not yet implemented. Stable capabilities require true end-to-end tests, cross-tenant negative tests, real-model verification when applicable and external package consumers. Scope may be staged; it must not be quietly deleted to satisfy an artificial deadline. Document `Planned`, `Experimental`, `Ready`, `Degraded`, `Disabled` accurately in APIs and docs. Evidence is required to change checklist status. Protect proprietary/reference source attribution and all third-party license notices even where product UI has been rebranded.

---

# Part II — Preserved detailed source requirements (normative annexes)

These annexes retain the full detailed uploaded requirements and options material rather than paraphrasing away functionality. **The canonical reconciliation table in §4 governs conflicting old examples**, especially PostgreSQL-only clauses, model defaults, deployment profiles and product identity. No line below independently authorizes a weakening of security controls.


---

## Annex 1: Strongly typed configuration and agent modes

**Preserved source:** `configuration.md` · **SHA-256:** `2a295418a44a666954754000686b7622c98560030d31e21c86a84124064f9514` · **Content retained verbatim below.**

# OpenConnect — Strongly Typed Configuration and Agent Execution Modes

**Status:** Normative proposed engineering contract · **Version:** 1.1-draft · **Date:** 2026-11-06  
**Companions:** `spec.md`, `implementation.md`, `firewall.md`, `checklist.md`.  
**Traceability:** OC-CFG-010–029, OC-AGT-020–039, OC-FW-049–064; IMP-090–099; AT-031–042.

> All sample classes, APIs, and names are target contracts, **not compiled/existing implementations**. Check actual Microsoft Agent Framework APIs and supported target frameworks before implementation. Avoid treating enum types as a security boundary: configuration is trusted only when supplied by the server operator; requests, agents, Markdown and NATS events cannot alter it.

## 1. Non-negotiable principles

1. **One strongly typed options model.** Prefer public C# enums and typed options over arbitrary strings for closed vocabularies: execution mode, privacy mode, failure behavior, detector kinds, on/off, model device, load policy, transport, tool side-effects, scope selection and policy decisions. Allow strings for user-defined agent IDs, tool IDs, capability IDs and model names, but validate against registries at startup.
2. **No enum-number configuration.** Serialize enums as stable kebab-case strings to JSON/YAML using explicit converters; reject unknown values and numeric enum payloads, and keep stable protocol identifiers independent of C# CLR spelling. Never use implicit `Enum.Parse` on untrusted inputs.
3. **No magic switches.** Defaults are secure and documented; validate all combinations at startup with `IValidateOptions<T>` and `ValidateOnStart`. Agents cannot request more permissions than host policy.
4. **No mandatory heavyweight detectors.** Security firewall on by default; rules-only is the default lightweight detector profile. GLiNER, DeBERTa, Qwen3Guard, PaddleOCR and YuNet are individually opt-in and must not load/download/allocate if disabled. Required protection depends on **explicit policy coverage**; no claim that rules-only covers freeform names, injection, safety, images or faces.
5. **Two execution modes, distinct security principals.** `Interactive` agents execute under the **authenticated active user's derived immutable auth context** and can only access that user's rights. `Operational` agents execute from scheduled/durable background work under a **dedicated constrained workload identity**, never impersonate the last interactive user, and are monitored and reconciled by a .NET hosted background worker. Operational agents cannot inherit user session cookies or UI action authority. These are different modes of agent instance/execution, not different agent frameworks.
6. **Model/worker isolation.** `.AddOpenConnect()` registers lead agent and host firewall enforcement; self-hosted Python security and capability workers are separate opt-in Docker deployments. Enabling a required detector without an available validated checkpoint makes that detector unready and blocks only flows requiring its coverage.
7. **NATS durability ≠ authorization.** JetStream carries safe work notices; the .NET host remains final policy, authorization, session, and durable commit authority. Scans that gate egress run inline before external transfer, not asynchronously via JetStream.

## 2. Public C# enum catalog (proposed)

Use the following identifiers in `OpenConnect.Abstractions` or `OpenConnect.Core.Configuration`; avoid hidden stringly typed switch branches. Keep service-specific ASP.NET/NATS integrations outside abstractions.

```csharp
public enum OpenConnectTransportMode { JetStream, InMemoryDevelopment }
public enum AgentExecutionMode { Interactive, Operational }
public enum AgentActivationMode { OnDemand, Scheduled, Continuous, EventTriggered }
public enum AgentRunState { Registered, Queued, Starting, Running, Waiting, Paused, Completed, Failed, Cancelled, Stale }
public enum AgentIdentityMode { ActiveUser, WorkloadIdentity }
public enum ToolImpact { ReadOnly, Write, ExternalSideEffect }
public enum ModelExecutionLocation { LocalOnly, SelfHosted, RemoteAllowed }
public enum FirewallMode { StrictLocal, LocalOnly, PolicyControlled, Disabled }
public enum FirewallFailureAction { Deny, LocalOnlyIfVerified }
public enum SecurityDetectorKind { Rules, GlinerPii, DebertaInjection, Qwen3Guard, PaddleOcr, YuNet }
public enum DetectorLoadPolicy { Startup, LazyOnFirstUse }
public enum DetectorDevice { Auto, Cpu, Cuda }
public enum DetectorState { Disabled, Loading, Ready, Degraded, Failed }
public enum SecurityCoverage { StructuredPii, SemanticPii, PromptInjection, ContentSafety, ImageText, FaceDetection }
public enum SecurityPolicyAction { Allow, Mask, Redact, Block, Review }
public enum SecurityCoverageUnavailableAction { Block, Review, LocalOnlyIfVerified }
public enum SchedulingMisfirePolicy { Skip, RunOnce }
public enum OperationalConcurrencyPolicy { Forbid, Queue, ReplaceAfterFence }
public enum OperationalRecoveryPolicy { RetryWithBackoff, PauseForReview, Disable }
```

**Enum compatibility:** External contracts use explicit wire names such as `interactive`, `operational`, `qwen3guard` and `strict-local`; maintain cross-language fixtures. New optional enum members are additive only with version negotiation and default-deny unknown behavior. `LocalOnlyIfVerified` is permitted only when the capability registry can prove the chosen model/tool remains in a specifically approved local trust zone. `Review` requires a real durable review queue and human approval; absence of one resolves to `Block`, never auto-allow.

## 3. Typed registration — minimum DX

```csharp
// ASP.NET Core composition: signatures are proposed and must be implemented.
builder.Services.AddOpenConnect(options =>
{
    options.Transport.Mode = OpenConnectTransportMode.JetStream;
    options.Runtime.InstructionsPath = "agent-runtime.md";
    options.Agents.DefaultExecutionMode = AgentExecutionMode.Interactive;
    options.Agents.RegisterLeadAgent = true;

    options.Privacy.Enabled = true;
    options.Privacy.Mode = FirewallMode.StrictLocal;
    options.Privacy.OnFailure = FirewallFailureAction.Deny;
    options.Privacy.Detectors.Rules.Enabled = true;
    options.Privacy.Detectors.GlinerPii.Enabled = false;
    options.Privacy.Detectors.DebertaInjection.Enabled = false;
    options.Privacy.Detectors.Qwen3Guard.Enabled = false;
    options.Privacy.Detectors.PaddleOcr.Enabled = false;
    options.Privacy.Detectors.YuNet.Enabled = false;

    options.Privacy.Policies.StructuredPii = SecurityPolicyAction.Mask;
    options.Privacy.Policies.UnsupportedRequiredCoverage = SecurityCoverageUnavailableAction.Block;
});

builder.Services.AddOpenConnectPostgres(builder.Configuration.GetConnectionString("OpenConnect")!);
builder.Services.AddOpenConnectNats(builder.Configuration["Nats:Url"]!);
builder.Services.AddOpenConnectSecurityWorker(options =>
{
    options.Endpoint = new Uri("https://security-worker.internal/");
    options.Timeout = TimeSpan.FromSeconds(5);
    options.RequireMutualTls = true;
});

builder.Services.AddOpenConnectAgent("docs-assistant", agent =>
{
    agent.ExecutionMode = AgentExecutionMode.Interactive;
    agent.IdentityMode = AgentIdentityMode.ActiveUser;
    agent.InstructionsPath = "agents/docs-assistant.md";
    agent.AllowedTools.Add("docs.read");
});

builder.Services.AddOpenConnectAgent("nightly-indexer", agent =>
{
    agent.ExecutionMode = AgentExecutionMode.Operational;
    agent.IdentityMode = AgentIdentityMode.WorkloadIdentity;
    agent.Activation = AgentActivationMode.Scheduled;
    agent.Schedule.Cron = "0 2 * * *"; // UTC unless validated time zone set
    agent.Schedule.TimeZoneId = "Etc/UTC";
    agent.Schedule.MisfirePolicy = SchedulingMisfirePolicy.RunOnce;
    agent.Operational.Concurrency = OperationalConcurrencyPolicy.Forbid;
    agent.Operational.MaxParallelRuns = 1;
    agent.Operational.MaxAttempts = 3;
    agent.Operational.Timeout = TimeSpan.FromMinutes(10);
    agent.Operational.WorkloadIdentityRef = "openconnect/docs-indexer";
    agent.AllowedTools.Add("docs.index");
});
```

### 3.1 Option class shapes and validation

```csharp
public sealed class OpenConnectOptions
{
    public TransportOptions Transport { get; } = new();
    public RuntimeOptions Runtime { get; } = new();
    public AgentRegistryOptions Agents { get; } = new();
    public PrivacyOptions Privacy { get; } = new();
}
public sealed class PrivacyOptions
{
    public bool Enabled { get; set; } = true;
    public FirewallMode Mode { get; set; } = FirewallMode.StrictLocal;
    public FirewallFailureAction OnFailure { get; set; } = FirewallFailureAction.Deny;
    public SecurityDetectorOptions Detectors { get; } = new();
    public SecurityPolicyOptions Policies { get; } = new();
}
public sealed class SecurityDetectorOptions
{
    public DetectorOptions Rules { get; } = new() { Enabled = true };
    public DetectorOptions GlinerPii { get; } = new();
    public DetectorOptions DebertaInjection { get; } = new();
    public DetectorOptions Qwen3Guard { get; } = new();
    public DetectorOptions PaddleOcr { get; } = new();
    public DetectorOptions YuNet { get; } = new();
}
public sealed class DetectorOptions
{
    public bool Enabled { get; set; }
    public string? ModelId { get; set; }
    public string? Revision { get; set; }
    public string? ModelDirectory { get; set; }
    public DetectorDevice Device { get; set; } = DetectorDevice.Cpu;
    public DetectorLoadPolicy LoadPolicy { get; set; } = DetectorLoadPolicy.Startup;
    public int MaxConcurrentRequests { get; set; } = 1;
    public int? MemoryBudgetMb { get; set; }
    public double? Threshold { get; set; }
}
```

`OpenConnectOptions` hosts **desired policy**; the Python security worker holds its own typed Pydantic config for actual model availability. Enforce negotiation between the host's enabled/required coverage and the worker's **signed/authenticated ready capability manifest**. Merely setting an option to true does not prove detector readiness or safety.

**Required validation cases:** unknown enum strings; `FirewallMode.Disabled` with `Privacy.Enabled=true` (conflicting state); strict-local with permissive failure behavior; `AgentExecutionMode.Operational` plus `ActiveUser`; `Interactive` plus `Continuous` or cron; absent workload identity; schedule without valid cron/time zone; unsupported NATS in production; selected `Qwen3Guard` without a model manifest; `PaddleOcr` image policy without image-text handling; contradictory detector disable/required coverage; invalid thresholds/negative memory; unavailable review backend; unapproved model downloads or remote detector endpoints. Missing optional detectors do **not** block startup if no required policy depends on them. Do not enforce model-reported confidence as an authorization decision.

## 4. JSON configuration with typed enums

```json
{
  "OpenConnect": {
    "Transport": { "Mode": "jet-stream" },
    "Agents": { "DefaultExecutionMode": "interactive", "RegisterLeadAgent": true },
    "Privacy": {
      "Enabled": true,
      "Mode": "strict-local",
      "OnFailure": "deny",
      "Detectors": {
        "Rules": { "Enabled": true },
        "GlinerPii": { "Enabled": false },
        "DebertaInjection": { "Enabled": false },
        "Qwen3Guard": { "Enabled": false },
        "PaddleOcr": { "Enabled": false },
        "YuNet": { "Enabled": false }
      },
      "Policies": {
        "StructuredPii": "mask",
        "UnsupportedRequiredCoverage": "block"
      }
    }
  }
}
```

The host uses `Microsoft.Extensions.Options` binding, documented converters and validators. Enum serialization/conversion must produce the same wire values across C#, JSON schema, YAML and Pydantic; create a single golden fixture and verify parity. Do not expose `Privacy.Enabled`, detector toggles, or scheduling credentials via public agent/tool request DTOs.

## 5. Execution mode contracts

### 5.1 Interactive agents

**OC-AGT-020–027** The task enters via authenticated HTTP/WS, from an active session with a validated principal. The host creates a server-only `AgentExecutionContext` snapshot containing tenant, subject, authorization scope revision, session/task IDs, correlation, expiry, request cancellation and policy revision. It MUST NOT copy a raw bearer token/cookie into model prompts, traces, NATS or workers. A delegated subagent uses an equal-or-narrower scope; elevation is impossible through Markdown, tool discovery or model output. Enforce reauthorization on EVERY read/write tool invocation; signed approvals are bound to arguments, principal, session and expiry. If the principal is disabled, access expires or the session changes scope, revoke future tool calls and cancel/park any affected task according to policy. UI components and suggestions inherit the same entitlement filtering and require human action for privileged operations.

**Interactive task sequence:** authenticated request -> derive active user scope -> validate typed agent and capabilities -> select MAF agent/subagent -> inline security pre-egress -> invoke model/tool with reauthorization -> durably commit answer -> output inspection/restoration only for authorized principal -> WS replay. A disconnected browser does not confer ongoing authority to subsequent actions; recheck grants.

### 5.2 Operational agents

**OC-AGT-028–036** Operational agents are registered explicitly and monitored by a .NET hosted `BackgroundService` (`OpenConnectOperationalAgentSupervisor`) using a durable scheduler/dispatch table and JetStream work notices. A user can trigger a permitted operation, but after acceptance execution MUST use the registered **workload identity** and its pre-approved scopes; the initiator is retained only as safe, audit-only provenance, not impersonated authority. Require per-agent enable/pause, owner, workload identity, allowlisted tools/capabilities, timezone-aware schedule or event trigger, concurrency policy, maximum runtime, attempts, backoff, cancellation, quotas, and data access policy. Do not use ambient `HttpContext`, `IHttpContextAccessor`, session cookies or direct user bearer tokens in operational execution. Use scoped DI for each run and a workload credential provider (prefer short-lived identity where possible).

The supervisor should: acquire a DB lease with fencing token -> evaluate due schedules/event subscriptions -> create an idempotent run record -> publish NATS notice from the transactional outbox -> heartbeat/renew lease -> construct MAF agent from registered definition + workload context -> enforce firewall and tool broker -> commit outcomes -> ACK only after durable commit -> apply retry/backoff/quarantine on errors -> expose redacted metrics/status, pause/resume/replay and alerts. Multiple host replicas must elect only one lease owner for a logical run. A resumed run must not duplicate non-idempotent writes; require side-effect idempotency or explicit approval.

An operational agent **must not** perform automatic client UI activation or assume the latest active user. If review/approval is needed, create a durable authorization-bound review item; a later authenticated reviewer may approve exact operation parameters. The approval must not change the workload's authorized capability budget.

### 5.3 Delegation and shared rules

An interactive lead agent can delegate only to agents whose definition and tool/capability scopes are equal-or-narrower for its task. Interactive -> operational is **not implicit delegation**: it must be an explicit, authorized durable *enqueue operation* with a configured operational identity, idempotency key, and approval if required. Operational -> interactive is never silent: it creates a review/task notification and requires a fresh authenticated user task. An operational agent may delegate to another appropriately configured **operational** subagent without adopting interactive credentials. Any mixed-mode transition is visible in execution telemetry.

**Principal validation matrix:**

| Context / operation | Interactive | Operational |
|---|---|---|
| Caller identity | Authenticated current user | Host-managed workload identity |
| Auth information | Derived claims/scopes, no raw token propagation | Scoped workload grants, no user cookie |
| Activation | HTTP/WS/user action, explicit API | Scheduled, event-triggered, continuous or authorized enqueue |
| Hosting | Request/task processor with durable session | `BackgroundService` monitor + persistent run ledger |
| Delegation | Scoped same-mode subagents only | Workload-scoped same-mode subagents only |
| Tool authorization | Active principal and task | Workload principal and run ID |
| UI actions | Authorized proposals requiring user interaction | No direct UI activation; review/notification only |
| Authorization expiry | Revalidate before each consequential tool call | Revalidate workload grants before each consequential call |
| Cancellation | User/owner/API with policy | Operator/supervisor/run control |
| Audit | User/principal/session/task | Workload/run/trigger initiator (provenance only) |

## 6. Operational storage, events, and monitoring

Define `oc_operational_definitions`, `oc_operational_schedules`, `oc_operational_runs`, `oc_operational_leases`, `oc_operational_checkpoints`, `oc_operational_review_items`. Tenant/workload boundaries indexed and enforced; schedule occurrence/run idempotency must be unique per agent+tenant+scheduled occurrence or event ID. Store definition/policy revision on each run. New typed lifecycle events: `operational.agent.activated`, `operational.run.queued`, `.started`, `.heartbeat`, `.paused`, `.retry-scheduled`, `.review-required`, `.completed`, `.failed`, `.cancelled`, `.stale`; schema v1 typed and emitted sanitized through host event journal/outbox. Host readiness and health report supervisor status, lease lag, failed runs and alert thresholds without logging prompts/results.

Public control APIs: authenticated, scoped `GET /openconnect/v1/operational/agents`, `GET /.../runs`, `POST /.../{agentId}/pause`, `POST /.../{agentId}/resume`, `POST /.../{agentId}/trigger`, `POST /.../runs/{runId}/cancel`, `POST /.../runs/{runId}/review` with exact-bound approvals and typed errors; use admin/operator role for pause/enable as policy dictates. Do not assume all users can see operational status.

## 7. Detector profiles, resource admission and policy completeness

### 7.1 Baseline defaults

| Detector | Default | Expected capability | Hardware behavior |
|---|---|---|---|
| Rules | **Enabled** | Validated patterns, secret formats and known values | CPU, minimal allocations |
| GLiNER PII | Disabled | Semantic entity detection where calibrated/tested | Optional local checkpoint |
| DeBERTa injection | Disabled | Injection risk evidence (not proof) | Optional local checkpoint |
| Qwen3Guard | Disabled | Safety classifier (input/output, checkpoint-dependent) | Optional local checkpoint, potentially large |
| PaddleOCR | Disabled | Text localization/OCR in images | Optional OCR assets |
| YuNet | Disabled | Face bounding regions | Optional local ONNX model |

No hardware figure, detection accuracy or language coverage is guaranteed without actual benchmark evidence. Disabled means **no module model initialization, download, allocated GPU context, or capability advertisement**. For `LazyOnFirstUse`, readiness is pending until first load and only permitted if the host policy explicitly allows waiting; never route protected data when required detector is unready.

### 7.2 Coverage-aware security decisions

Default lightweight profile (rules-only) may protect **structured PII** but does not claim semantic name detection or general image/face protection. Strict-local mode defines *where* detection executes, not a blanket promise of full detector coverage. Policy can require coverage levels per destination/content class, e.g. external model + unstructured free text requires `SemanticPii` and a healthy GLiNER (or verified equivalent) whereas a synthetic structured fixture may allow rules-only. When a required detector is missing, fail closed/review (if a real review backend exists), or proven local-only if allowed. A disabled optional detector is **not** a global denial for unrelated requests. Image with text and faces requires both image-text and face coverage under relevant policy; if missing, block external upload. Audio remains blocked from remote STT under strict-local unless an independently verified local processing/privacy path exists.

Policy result must include coverage evaluated, required, missing, detector states, rules applied, `SecurityPolicyAction`, policy revision and **redacted** reasoning codes. Model confidence is input evidence, not a privileged security decision. No client or agent can self-select `Allow`, toggle detector states or claim a higher coverage level.

### 7.3 Resource profiles (examples, not promises)

- `Minimal`: rules-only; no NER, OCR, safety, injection or face coverage.
- `Balanced`: rules + selected GLiNER / YuNet, subject to actual model memory and policy coverage.
- `Advanced`: operator-selectable GLiNER + DeBERTa + Qwen3Guard + PaddleOCR + YuNet; requires measured memory, CPU/GPU budgets and model licensing.

Profiles are convenience builders that materialize **explicit detector options**, not runtime-guessed safety levels. `MemoryBudgetMb`, `MaxConcurrentRequests`, model checksum, load policy and model device must be validated against measured local capability; failed resource admission = unready, not silent substitute.

## 8. Work packages and acceptance mapping

| ID | Work | Exit evidence |
|---|---|---|
| IMP-090 | Public typed enum/options catalog; stable wire converters and schema synchronization | AT-031 |
| IMP-091 | Strongly typed options builders/DI validation and JSON/YAML binding, secure defaults | AT-032 |
| IMP-092 | Interactive `AgentExecutionContext` derived from authenticated user, scope-bound delegation and tool auth | AT-033, AT-034 |
| IMP-093 | Operational agent definitions, scheduler, supervisor hosted service, workload identities | AT-035, AT-036 |
| IMP-094 | Operational Postgres schema, leases/fences, idempotency, outbox, NATS lifecycle | AT-037 |
| IMP-095 | Operational observability, typed pause/resume/review/trigger APIs and negative access tests | AT-038 |
| IMP-096 | Per-detector typed options and Python Pydantic parity, independently disabled/lazy/startup loaders | AT-039 |
| IMP-097 | Coverage-aware policy engine, truthful capability registry, missing-detector matrix | AT-040 |
| IMP-098 | Resource-constrained Docker profiles, no unwanted downloads and model startup tests | AT-041 |
| IMP-099 | CI, NuGet/npm/GHCR/docs integration for enum stability and both agent modes | AT-042 |

### Acceptance scenarios — all initially NOT RUN

- **AT-031** Public C# enum catalog and JSON/YAML/Pydantic golden fixture parity, numeric/unknown enum rejection.
- **AT-032** `.AddOpenConnect()` typed defaults, `IValidateOptions` on startup, invalid configuration matrix rejects early; fluent and configuration binding equivalent.
- **AT-033** Interactive task uses active user's actual auth scope; one user never receives another user's data after scope refresh.
- **AT-034** Delegation never expands tools/principal; revoked active authorization blocks next tool action and cannot revive via WS reconnect.
- **AT-035** Operational agent initializes and runs under its workload identity with no `HttpContext` or user cookies; reviewer/initiator cannot impersonate execution.
- **AT-036** Operational scheduler misfire/pause/resume/overlap/backoff/cancellation and multi-instance lease/restart recovery.
- **AT-037** Operational run's acceptance/outbox/NATS ACK lifecycle idempotent, fencing prevents duplicate side effects after crash/replay.
- **AT-038** Operational controls RBAC and review-item exact-argument approval; inaccessible tenant and malicious trigger denied.
- **AT-039** All six detector switches independently exercised: disabled means no model download/init/advertisement, enabled reports truthfully.
- **AT-040** Coverage-required/missing matrix blocks relevant external egress but unrelated rules-only tasks still function; safety/injection models treated as evidence.
- **AT-041** Low-memory/CPU-only Docker profile and selected detector resource admission + SIGTERM; disabled model artifacts absent.
- **AT-042** PR CI and clean NuGet/npm consumers verify typed APIs, both agent modes, detector configuration and stable wire contracts; required release gates recorded.

## 9. Coding-agent execution instructions

Read `spec.md` -> `configuration.md` -> `firewall.md` -> `implementation.md` -> `checklist.md`. Implement `IMP-090`/`091` **before** writing agent-mode or detector adapters. `IMP-092` can proceed after host authentication and tool broker; `IMP-093`/`094` require durable persistence/outbox and NATS. `IMP-096` depends on the Python security worker bootstrap; `IMP-097` requires host policy and detector capability negotiation. Change only one numbered work package or tightly related group per PR; add focused tests first, then code, then checklist evidence. Do not mark any `AT-*` complete without run logs. Document third-party source/model licenses and true tested supported languages. 




---

## Annex 2: Entity Framework requirements

**Preserved source:** `entityframework-requirements(1).md` · **SHA-256:** `a442a916a927d6d204b0f32b9344fb7217e7423b35fcfc5596eeb393d5a6195a` · **Content retained verbatim below.**

# OpenConnect — Pluggable Entity Framework Core Persistence Requirements

**File:** `entityframework-requirements.md`  
**Version:** 1.0-draft  
**Date:** 2026-11-07  
**Status:** Proposed normative requirements; not proof of implementation  
**Companion:** `entityframework-spec.md`  
**Audience:** Codex, Muse, DeepSeek, maintainers and application integrators

## 0. Product intent and normative language

OpenConnect is a .NET-first, privacy-first reusable AI framework, not an application. Its persistence implementation MUST integrate with developers' **existing EF Core database configuration** instead of imposing PostgreSQL, creating a second database, or requiring them to define `DbSet<T>` properties. The framework retains its provider-independent `ISessionStore`, `ITaskStore`, `IEventJournal`, `IOutboxStore`, approvals, leases, privacy-vault and operational-run contracts. The EF Core adapter is optional and selected only by explicit DI registration.

- **MUST / SHALL:** required to claim feature support and pass release gates.
- **SHOULD:** preferred default; deviations require a documented architectural decision and tests.
- **MAY:** optional extension.
- **Normative precedence:** OpenConnect security, agent-identity, operational scheduling and event-contract requirements remain in force. This document governs EF Core adapter registration, schema, migrations and transactional guarantees where earlier documentation prescribed PostgreSQL specifically.
- **Traceability:** `OC-EF-001`–`OC-EF-096`; work packages `EF-IMP-001`–`EF-IMP-016`; tests `EF-AT-001`–`EF-AT-030` in companion spec. Mark status only after actual tests.
- **No invented functionality:** All example APIs in these documents are **target contracts** and must be compiled against the chosen supported EF Core version before describing them as shipped.

## 1. Architectural invariants

**OC-EF-001** `OpenConnect.Abstractions`, default agent registration, HTTP/WS clients, MCP and Navigator MUST NOT depend on EF Core or any particular provider; only the optional `OpenConnect.Persistence.EFCore` package introduces EF Core dependencies.

**OC-EF-002** `AddOpenConnect()` MUST NOT create tables, open connections, execute migrations, register a database provider, install NuGet provider packages or add a second database.

**OC-EF-003** The intended primary developer API is:

```csharp
builder.Services
    .AddOpenConnectEntityFramework<ApplicationDbContext>(options =>
    {
        options.Schema = "openconnect";
        options.Migrations = OpenConnectMigrationMode.Manual;
    });
```

`ApplicationDbContext` is already registered by the application using its chosen provider. This API SHALL register EF-backed OpenConnect stores but not override or recreate the developer's provider configuration.

**OC-EF-004** Entities MUST be discoverable through `modelBuilder.ApplyOpenConnectModel()` in `OnModelCreating`. Consumers MUST NOT be forced to declare `DbSet<SessionEntity>`, `DbSet<TaskEntity>`, etc. Existing host business entities and mappings remain untouched.

**OC-EF-005** The same **connection string and database** are the usual configuration; a separate `OpenConnectDbContext` and/or separate physical database MAY be configured explicitly. This choice MUST not be inferred from an arbitrary connection string string comparison.

**OC-EF-006** Prefer isolated tables within SQL Server/PostgreSQL schema `openconnect`; SQLite and InMemory use prefixed logical tables/entity names because schema support differs. Entity mapping names MUST be consistent across EF model, generated migrations and runtime repositories.

**OC-EF-007** No backend may advertise durable outbox guarantees without real transactional atomicity, concurrency control, recovery and tested isolation semantics.

**OC-EF-008** `InMemory` is explicitly **development/test-only** with no durable, multi-process, transactional-outbox, distributed-lease or crash-recovery guarantees. An app requiring operational-agent leases/durable outbox MUST fail production startup if InMemory is selected.

**OC-EF-009** EF Core **provider portability** means supported contracts across the four tested provider families, not identical SQL, migrations or lock APIs. Provider-specific strategies may be selected via a validated dialect/capability abstraction without putting provider packages into the core EF adapter.

**OC-EF-010** All EF mappings, migrations, schema changes and migration execution are opt-in. Defaults MUST be safe for an existing live database.

## 2. Supported providers and external packages

| Provider | Developer-owned provider package | EF relation | Production durability | Schema strategy | Concurrency strategy |
|---|---|---|---|---|---|
| InMemory | `Microsoft.EntityFrameworkCore.InMemory` | Non-relational fake | **No** | Logical entity mapping | Single-process development behavior only |
| SQL Server | `Microsoft.EntityFrameworkCore.SqlServer` | Relational | Yes, tested | `openconnect` schema | App-managed concurrency token + provider-appropriate transactional claim |
| SQLite | `Microsoft.EntityFrameworkCore.Sqlite` | Relational | Yes within single-node/writer limits | `oc_` table prefix, no schema | App-managed concurrency token, serialized writers/retry busy |
| PostgreSQL | `Npgsql.EntityFrameworkCore.PostgreSQL` | Relational | Yes, tested | `openconnect` schema | App-managed concurrency token + provider-appropriate transactional claim |

**OC-EF-011** `OpenConnect.Persistence.EFCore` MUST NOT reference a concrete `UseSqlServer`, `UseSqlite`, `UseNpgsql` or `UseInMemoryDatabase` extension, include provider package references, or automatically resolve/install missing packages.

**OC-EF-012** Developers choose the provider in their normal `AddDbContext<TContext>`/`AddDbContextFactory<TContext>` configuration. The package only consumes the configured context.

**OC-EF-013** Package and compatibility docs MUST declare the supported EF Core major version and a four-provider build/test matrix; integration with other EF Core providers is experimental until dialect/capability tests pass.

**OC-EF-014** SQL Server/PostgreSQL require actual running database integration tests, not simulated EF InMemory tests. SQLite file-based and in-memory relational SQLite are separate tests.

**OC-EF-015** Provider-specific optimizations MUST be isolated behind dialect interfaces or user-installed provider-specific adapter packages if direct provider dependencies are necessary. Public EF stores must continue using common abstractions.

## 3. Integration and configuration

**OC-EF-016** `AddOpenConnectEntityFramework<TContext>(Action<OpenConnectEfOptions>? configure)` MUST require `TContext : DbContext` and validate it is registered and has the OpenConnect entity model installed. Missing model integration fails with an actionable error, not an automatic mutation of a precompiled model.

**OC-EF-017** `ApplyOpenConnectModel(ModelBuilder, ...)` SHALL register all required entities with public model-registration extension and internal implementations, supported keys, indexes, max lengths, concurrency and required provider-specific schema/table conventions. No public `DbSet<>` property interface is required.

**OC-EF-018** `OpenConnectDbContext` SHALL be offered as an alternative to modifying an existing context. It can use any application-supplied EF provider/connection string, including the same physical database. Registering it MUST NOT silently bootstrap a database.

**OC-EF-019** Typed options MUST be exposed for `OpenConnectMigrationMode`, `OpenConnectEfIntegrationMode`, `OpenConnectOutboxClaimStrategy`, `OpenConnectPersistenceConsistency`, and `OpenConnectSchemaValidationMode`, with validated string wire representation where bound from configuration.

**OC-EF-020** Default migration mode MUST be `Manual`; no call to `Migrate`, `EnsureCreated`, `EnsureDeleted`, DDL or provider-specific schema creation may occur from `AddOpenConnectEntityFramework` alone.

**OC-EF-021** `None` mode means neither schema creation nor migration apply and only explicit model/runtime validation if enabled. `Manual` means the application/deployer runs migration commands/scripts; `ApplyOnStartup` is a separately explicit opt-in for a dedicated OpenConnect context only and must coordinate startup concurrency (or be denied when unsafe). In a shared application context, this option MUST be rejected rather than migrating unrelated app tables.

**OC-EF-022** Safe default migration workflow for existing context: add `ApplyOpenConnectModel` to host `OnModelCreating`; generate migration in host's migration project; review/apply migration using application's deployment process. Migrations must create only intended new tables and never drop/change unrelated business tables.

**OC-EF-023** For dedicated context, users can manage a provider-compatible, independently versioned migration assembly and migration history table. One monolithic compiled set of SQL Server migrations MUST NOT be assumed portable to SQLite/PostgreSQL; provide provider-specific migration guidance and examples.

**OC-EF-024** Existing-context migration history remains application-owned; dedicated OpenConnect context history should use e.g. `__OpenConnectMigrationsHistory` and `openconnect` schema where supported, configured via provider-specific options. Never edit migration history automatically from the EF adapter.

**OC-EF-025** Model cache key MUST include mapping-shaping options (schema, prefix, provider family) where a single context type can be used with multiple option sets in the same process. Design-time and runtime model must match; otherwise reject divergent configuration.

**OC-EF-026** All public options MUST be validated using .NET Options pattern, with startup validation; arbitrary client/agent/YAML instructions cannot change provider, schema, migration policy or table names.

**OC-EF-027** Default table naming SHOULD be predictable, e.g. `Sessions`, `Tasks`, `Turns`, `Events`, `Outbox`, `Leases`, `Approvals`, `PrivacyTokens`, `OperationalRuns`; SQLite maps with prefix `oc_`, such as `oc_sessions`. Explicitly document aliases/mapping decisions; no table-name collisions with existing host tables.

**OC-EF-028** Schema customization is allowed only through strongly validated, fixed identifier options (not raw SQL). Reject SQL injection, reserved names, excessively long identifiers and configuration drift against applied schema.

## 4. Entity and database model

**OC-EF-029** Support at least these entities, leaving optional modules to register their extensions: sessions, tasks, turns, events, outbox messages, outbox delivery status/leases, operational runs/leases, approvals, privacy tokens, and schema/version metadata where needed.

**OC-EF-030** OpenConnect entities are internal implementation details by default and accessed by OpenConnect stores with `DbContext.Set<TEntity>()`. Public domain contracts remain separate from persistence records. Avoid forcing callers to know persistence entity classes or map their own `DbSet` properties.

**OC-EF-031** All tenant/owner-scoped tables MUST include tenant and relevant principal/workload scope columns, be indexed for access paths, and be queried under a server-resolved authorization context. No user-supplied tenant ID confers authority. Global query filters MAY provide defense-in-depth but cannot replace explicit authorization checks.

**OC-EF-032** Events MUST retain per-session monotonic sequence, event UUID/idempotency identifier, sanitized payload, timestamp and correlation. They MUST NOT store unmasked prompts or arbitrary tool contents by default.

**OC-EF-033** Tasks and turns MUST support unique scoped client request IDs, optimistic version/fence, cancellation/terminal outcomes and unambiguous replay.

**OC-EF-034** Outbox rows MUST track delivery status, retry count, next attempt, lease owner/id, lease expiry, fencing version, payload digest, event id and safe failure code. Never persist broker credentials or unsanitized payloads.

**OC-EF-035** Privacy token mappings MUST be encrypted with keys controlled by approved .NET vault/key services (local protected key or optional Infisical KMS) and never expose plaintext through relational queries, logs or migration seeds. A database provider is NOT an encryption provider.

**OC-EF-036** Approved operational-agent definitions/runs, schedules and leases use dedicated constrained workload identity, not an interactive user token. Run leases and task outcomes obey the same provider-specific concurrency guarantees.

**OC-EF-037** Schema naming, collations, indexing, key lengths, blob types, date/time types and identifier length MUST be tested for each provider. Avoid relying on automatically generated rowversion/sequence support unavailable in SQLite.

## 5. Transactional outbox and durability

**OC-EF-038** When using one shared `ApplicationDbContext` instance, OpenConnect changes and application changes MAY participate in the **same EF Core transaction**, provided they are persisted within a defined transaction boundary and commit owner. The framework MUST expose a unit-of-work API and not call `SaveChangesAsync` or `CommitAsync` prematurely on behalf of a caller-owned transaction.

**OC-EF-039** Atomic persistence MUST cover accepted task / session mutation / appended committed event / enqueued outbox message in one relational transaction for durable mode. The exact trigger for publishing MUST be a committed outbox record—not an in-memory agent event callback.

**OC-EF-040** If application business data and OpenConnect outbox are in different DbContexts, even with identical connection strings, atomic commit is NOT guaranteed until their connection/transaction is deliberately shared with provider support. If separate physical databases are used, the feature MUST NOT claim a single-database ACID transaction; use a documented eventual consistency/compensating workflow instead.

**OC-EF-041** Support `IExecutionStrategy` for retriable transient database failures with correctly scoped transaction delegate and idempotency. Avoid nested implicit transactions that conflict with a caller-managed transaction; respect cancellation and prevent retries of already-completed non-idempotent external actions.

**OC-EF-042** Outbox dispatcher MUST not hold a DB transaction open during network publication. Claim/lease in short transaction -> publish to NATS -> confirm broker ACK -> short transaction mark delivered, with idempotent retry. If crash occurs after publish before delivered marking, duplicate broker events are expected and MUST be deduplicated downstream.

**OC-EF-043** Claims MUST be safe with multiple host replicas: use optimistic compare-and-swap/fenced lease or validated provider-specific claim strategy. No reliance on `SELECT ... FOR UPDATE SKIP LOCKED` across all providers.

**OC-EF-044** Lease acquisition, heartbeat and completion MUST compare fencing/version and reject stale owners. Operational agents and worker dispatch use the same platform primitives where appropriate.

**OC-EF-045** SQLite mode MUST document single-writer limitations, busy timeout/retry bounds and recommended limits for concurrent dispatchers. Support single-node/small deployment; distributed multi-writer guarantees are unavailable unless proven by tests and configuration.

**OC-EF-046** InMemory mode MUST NOT use real outbox dispatcher success semantics or pretend atomic commit. Offer a deterministic in-process test transport and explicit non-durable status; reject operational durable mode.

**OC-EF-047** Outbox processing failures must be retried with exponential backoff/jitter, bounded attempts, visibility timeout and quarantined terminal states, with safe diagnostics. Support maintenance repair/requeue through authorized operator API.

**OC-EF-048** Delivery ordering is guaranteed only for the specifically defined scope (e.g. per-session sequence) by the database journal; NATS cross-subject/consumer scheduling order is not a substitute for the journal's session sequence.

**OC-EF-049** Explicitly define transaction boundaries for privacy-token creation and consent/approval records; protected data may not be dispatched externally before required persisted privacy state is committed.

**OC-EF-050** Event and outbox deletes/retention must honor session/legal/privacy policies. Retries must not resurrect deleted sessions or republish revoked payload references; use versioned tombstones/revocation checks.

## 6. Service lifecycle, schema validation and migrations

**OC-EF-051** Adapter registration MUST be idempotent where repeated with identical settings, and MUST reject ambiguous registrations with different contexts, providers or schema configurations unless a named-store feature is implemented.

**OC-EF-052** Host startup MUST validate required model entity types and table mappings, relational capability when durability is required, and optionally the database schema version/readiness. Startup validation must be configurable to avoid connecting merely to build a container or generate tooling.

**OC-EF-053** No package-level automatic migrations, no `EnsureCreated` shortcut for shared databases, and no database deletion in any production code path.

**OC-EF-054** When `ApplyOnStartup` is explicitly supported in a **dedicated** context, coordinate migration concurrency across service replicas with a supported provider-compatible mechanism; otherwise reject startup and instruct operator to use a migration bundle/job. Never automatically migrate a shared application's context.

**OC-EF-055** A manual migration workflow MUST include a runnable CLI example (`dotnet ef migrations add` and reviewed `dotnet ef database update`/bundle) and must distinguish installing the adapter from executing schema changes.

**OC-EF-056** Migrations MUST preserve existing application tables, relationships, schema versions and history. Test before/after snapshots, foreign keys and unrelated business rows in all relational providers.

**OC-EF-057** Schema upgrades must be forwards-compatible for rolling deployments where practical, with safe fail/read-only mode on incompatible version and an explicit rollback/backup plan. No forced schema reset.

**OC-EF-058** Schema validation cannot simply assume one database schema contains only OpenConnect tables. Validate only owned objects; do not introspect business data unnecessarily.

## 7. Security, privacy and tenancy

**OC-EF-059** No raw sensitive values in logs, traces, query diagnostics or error messages. Enable EF Core sensitive-data logging only in controlled synthetic test hosts; never in production default.

**OC-EF-060** Connection strings/credentials must originate in application's secret configuration and MUST NOT be copied into `agent-runtime.md`, models, task payloads, NATS, docs examples or OpenTelemetry tags.

**OC-EF-061** SQL injection must be excluded by validated identifiers, parameterized queries and provider-specific SQL guards. Reject arbitrary schema/table name interpolation from client input.

**OC-EF-062** Privacy-token storage MUST use authenticated encryption before DB writes, scoped to tenant + principal/workload + session + turn and expiry. KMS/Infisical integrations remain optional and separate packages.

**OC-EF-063** All retry claims, event projections, sessions, approvals and deletions enforce tenant isolation and appropriate principal/workload permissions. Operational agents cannot inherit the latest interactive user's context.

**OC-EF-064** Bulk deletion, retention, migration tooling and administrative diagnostics require explicit authorization and no uncontrolled cross-tenant disclosure.

**OC-EF-065** EF Core concurrency conflicts must not leak entity values; map to stable typed errors and safe retries.

**OC-EF-066** A compromised NATS consumer, agent or browser must not be able to change EF store authorization scope or access the token vault by supplying a session ID.

## 8. API design and extension boundaries

**OC-EF-067** Public store interfaces remain generic and provider-independent; implementations of `IEventJournal`, `IOutboxStore`, `ITaskStore`, `ISessionStore`, `ILeaseStore`, `IApprovalStore` and `IPrivacyTokenVault` MUST be properly registered via a single adapter.

**OC-EF-068** An `IOpenConnectPersistenceUnitOfWork` (or equivalent) MUST expose atomic execution/transaction ownership semantics, not merely `SaveChangesAsync`. Distinguish `OwnedTransaction`, `AmbientSharedContext` and `ExternalTransaction` where required.

**OC-EF-069** Data transfer records and persistence entity types are separate. EF models do not become API contracts or require serialization to clients.

**OC-EF-070** Mapping extension accepts explicit options (`schema`, `table prefix`, `provider capability`) so shared-context and dedicated-context mappings behave identically for a given provider.

**OC-EF-071** No reflective, auto-discovered assembly scans that accidentally map unrelated host entities. OpenConnect mappings are registered by a closed explicit set of `IEntityTypeConfiguration<T>` types and optional modules registered explicitly.

**OC-EF-072** `OpenConnect.Persistence.EFCore` MUST NOT reference Blazor, browser, MAF provider adapters, NATS server SDK, Docker or Qdrant implementation packages. It may depend on common OpenConnect abstractions and EF Core relational APIs needed for supported relational behavior.

**OC-EF-073** A higher-level `.AddOpenConnectEntityFramework()` convenience API MAY register a dedicated context when developer explicitly supplies provider configuration; it MUST NOT guess a connection string/provider by constructing another context from an existing `DbContext`'s current connection.

**OC-EF-074** `IOpenConnectComponent` and Navigator client metadata remain unaffected by provider selection; only authenticated domain events and authorization context flow to client.

**OC-EF-075** Optional event subscribers such as QdrantVactor use committed outbox/JetStream notifications; they MUST NOT require scanning all `IEventJournal` sessions or injecting vectors into `EventRecord`.

**OC-EF-076** Support optional application-level usage through an explicitly registered shared transaction coordinator for business data + OpenConnect events; document ambient/interleaved `SaveChanges` behavior precisely.

## 9. Testing and acceptance

**OC-EF-077** Unit tests SHALL cover model registration without `DbSet` properties, typed options, schema/table name normalization, duplicate DI and missing model detection.

**OC-EF-078** Integration suite SHALL run SQLite in-memory relational + SQLite persistent file; SQL Server disposable test instance; PostgreSQL disposable instance; EF InMemory only for clearly labeled non-durable tests.

**OC-EF-079** Existing application DbContext with `Customers`/`Orders` must maintain rows and unrelated schema before and after adding OpenConnect mappings and migrations.

**OC-EF-080** Per-provider outbox concurrency/fault tests: claim race, lease expiration, fail during publish, duplicate NATS ACK, restart, stranded outbox and multiple hosts.

**OC-EF-081** Outbox write and task/session commit must prove transaction rollback and idempotency. Shared vs dedicated context transaction semantics must be separately demonstrated.

**OC-EF-082** SQLite specifically must prove safe high-contention behavior for documented supported profile, or be restricted to a smaller deployment profile.

**OC-EF-083** InMemory cannot pass transactional durability tests; startup must reject when durable production mode is required.

**OC-EF-084** Migrations tests must show: no registration-time DDL, correct `Manual` behavior, dedicated-only startup migration opt-in, host-business migration isolation, SQLite no-schema mapping and separate migration history.

**OC-EF-085** Provider-neutral contracts must be tested through the same test vectors against all relational adapters, not forked per-provider logic that diverges.

**OC-EF-086** Package consumer tests must reference only published/packed OpenConnect NuGet packages + one installed EF provider, and compile with user's DbContext without adding any OpenConnect `DbSet<>` properties.

## 10. Performance, operations and releases

**OC-EF-087** Configure batch size, dispatch concurrency, poll delay, lease duration, max retries, retention, table prefix/schema and query timeout with strongly typed options and safe bounds. No unbounded scans.

**OC-EF-088** Index session/event/outbox queries and keep payload sizes bounded. Large data uses authorized references to encrypted blobs, never unbounded event columns.

**OC-EF-089** OTel signals include DB duration, pending outbox age/count, publish lag, lease conflicts, retry/dlq, schema version and provider family, without connection-string/user payload labels.

**OC-EF-090** Core host works without the adapter if a different persistence implementation is registered. No startup error due simply to missing EF package when not selected.

**OC-EF-091** Document single-process vs multi-host suitability by provider; SQLite/InMemory not described as equivalent to SQL Server/PostgreSQL under distributed load.

**OC-EF-092** Provider package versions are controlled by the application and compatibility-tested, without forcing unneeded package installation or binary redistribution of database drivers.

**OC-EF-093** CI gates: model/contract tests, migration script inspect, SQLite/in-memory, SQL Server/PostgreSQL integration, fault injection, package-consumer smoke, security scan and retention tests.

**OC-EF-094** Release package is `OpenConnect.Persistence.EFCore` on NuGet; additional provider dialect packages are OPTIONAL only if implementation genuinely requires them and must never be transitively pulled into core.

**OC-EF-095** Release documentation provides exact `AddDbContext` and `AddOpenConnectEntityFramework` examples for all four providers, alongside manual migrations and transaction warnings.

**OC-EF-096** Release acceptance requires ALL critical EF-AT tests for included production providers; features not validated must be marked experimental and cannot be advertised with full durable outbox guarantees.

## 11. Required developer experiences

### 11.1 Existing SQL Server context (primary)

```csharp
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("Default")));

builder.Services.AddOpenConnect();
builder.Services.AddOpenConnectEntityFramework<ApplicationDbContext>(options =>
{
    options.Schema = "openconnect";
    options.Migrations = OpenConnectMigrationMode.Manual;
});

// One-time application source change:
// protected override void OnModelCreating(ModelBuilder modelBuilder)
// {
//     base.OnModelCreating(modelBuilder);
//     modelBuilder.ApplyOpenConnectModel();
// }
```

### 11.2 Existing PostgreSQL context

Same mapping + registration but the application's own provider configuration uses `UseNpgsql(...)`. No `OpenConnect.Persistence.Postgres` installation is required.

### 11.3 Existing SQLite context

Application uses `UseSqlite(...)`. `Schema = "openconnect"` is mapped to provider-safe table prefix `oc_` (or explicitly configured prefix); it MUST NOT silently pretend SQLite has a named relational schema.

### 11.4 InMemory development

Application uses `UseInMemoryDatabase(...)` and chooses `Migrations = None` with `Durability = DevelopmentOnly`. Developer documentation MUST show that this does not persist across restart and is not a transactional-outbox integration.

### 11.5 Dedicated context (optional)

Provide `OpenConnectDbContext` and a registration overload where the developer configures EF provider explicitly and the app may choose the same connection string or a separate database. No requirement to add OpenConnect entity configurations to `ApplicationDbContext` in this mode; cross-context business/outbox atomicity is **not automatic**.

## 12. Failure behavior and boundaries

| Condition | Required behavior |
|---|---|
| EF adapter not registered, other persistence registered | Core uses configured persistence; no provider packages pulled in |
| Existing DbContext not registered | Fail with `ef_context_missing` at startup |
| OpenConnect model not applied | Fail with `ef_model_missing`, include setup instruction |
| SQL Server/Postgres schema absent, Manual mode | Startup readiness/config warning; never auto-create |
| SQLite no named schema | Map prefixed tables; no schema DDL |
| DB unavailable | Accepted task not reported committed without database commit; bounded retry |
| Outbox crash after broker publish | Message redelivered, idempotent downstream processing |
| Shared context with external transaction | Respect caller commit owner, no premature publish or commit |
| Separate context with same connection string | Warn/no claim of atomic shared business transaction |
| Separate databases | Explicit eventual-consistency/compensation design; no ACID claim |
| InMemory in durable production mode | Reject startup |
| SQLite multiple writer contention | Bounded retries/lease handling, documented scale limits |
| Incompatible migrations | Fail safely; no destructive schema reset |
| Privacy vault unavailable | Block required protected transfer, never write plaintext |

## 13. Source and verification references

- EF Core model discovery: https://learn.microsoft.com/en-us/ef/core/modeling/entity-types
- EF Core transactions and cross-context sharing: https://learn.microsoft.com/en-us/ef/core/saving/transactions
- EF Core concurrency: https://learn.microsoft.com/en-us/ef/core/saving/concurrency
- EF Core migrations: https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/managing
- EF Core separate migration projects: https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/projects
- EF Core migration history: https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/history-table
- SQLite limitations: https://learn.microsoft.com/en-us/ef/core/providers/sqlite/limitations
- EF Core InMemory cautions: https://learn.microsoft.com/en-us/ef/core/testing/choosing-a-testing-strategy

**Implementation agent must validate chosen EF Core release and provider APIs rather than assuming all provider-specific operations compile unchanged.**




---

## Annex 3: Anchor MiniComposer requirements

**Preserved source:** `composer-requirements(1).md` · **SHA-256:** `426cd8aad111f61bbb3fc36e05b28c8bf7f4ec71fa02935a272428434b0988fa` · **Content retained verbatim below.**

# OpenConnect Anchor Composer — Product & Acceptance Requirements

**Document:** `composer-requirements.md`  
**Version:** 1.0-draft · **Date:** 2026-11-07  
**Status:** Normative implementation specification; no feature assumed implemented  
**Companion:** [`composer-spec.md`](./composer-spec.md)  
**Target packages:** `OpenConnect.Components.Blazor`, `OpenConnect.Client`, `@openconnect/client`, and optional `@openconnect/react`/browser components.

## 0. Objective and immutable design decision

**OpenConnect's default end-user interaction surface SHALL replicate the compact anchored composer from the supplied reference image and reviewed source, not create a full-screen chat application, sidebar, large modal, or permanent chat history panel.** The SDK must disappear into a developer's application until the user explicitly calls upon it, preserving application workspace. A floating bottom-right launcher opens a small conversation bubble immediately above it; the user can drag the bubble to another safe location. Text tasks, status, voice input/output, session stop, settings and context-aware UI guidance all operate through the same minimal surface.

The anchor composer is the **shared user-facing entry** into the default OpenConnect lead agent, permitted subagents, tools, MCP, `IOpenConnectComponent` discovery, Navigator, and optional speech workers. It is *not* an independent AI agent, model runtime, transport, security policy, or replacement for host UI. All actual task routing, durable events, privacy and authorization are owned by the OpenConnect host.

**MUST/MUST NOT** are release-blocking. **SHOULD** needs documented deviation; **MAY** optional. IDs `ACR-*` map to `composer-spec.md` implementation work items `CMP-IMP-*` and tests `CMP-AT-*`. No acceptance is complete without reproducible evidence.

## 1. Reviewed reference versus design interpretation

Repository reviewed: [`Vuminkosi/Swift-Aid`](https://github.com/Vuminkosi/Swift-Aid), pinned tree SHA `1c98e0a1efc94ac517805a6454216fde7b6e8743`. Relevant observed source:

- `src/SwiftAid.UI/Ellai/Components/EllaiCompactComposer.razor` and `.razor.css`: compact input, header, status, audio controls, drag handle, callbacks, sizing and message snippets.
- `src/SwiftAid.UI/Ellai/Components/EllaiLauncherMenu.razor` and `.razor.css`: independent, fixed `MudFabMenu` launcher and action items.
- `src/SwiftAid.UI/Ellai/Guidance/EllaiGuidanceHost.razor` and `.razor.css`: floating versus attached presentation, session state and latest messages, tour attachment and position binding.
- `src/SwiftAid.UI/Ellai/Components/EllaiStatusLine.razor` and `.razor.css`: three-bar animated status, safe structured text, expanded status, reduced motion.
- `src/SwiftAid.UI/Ellai/EllaiComposerPosition.cs`, `wwwroot/js/ellai-composer-drag.js`, `wwwroot/js/ellai-composer-input.js`, corresponding `tests/voice/*.test.mjs`: clamp, move by drag/keyboard, callback/disposal, Enter/Shift+Enter and IME safeguards.

The supplied reference screenshot is **634×326 pixels** and shows an *open bubble*: thin-bordered white rounded rectangle, small move handle at upper left, settings/stop/close at upper right, "Message Ellai" and "Ask Ellai..." input, red stop and grey live audio controls, a dotted separator, `Ready` with green animated bars, `Voice on`, `Mic on`, and a distinct green circular assistant FAB below/right. It is a cropped snapshot—not a reliable measurement of underlying CSS viewport scaling, not proof that every state is functional.

Source specifics to preserve: source composer declares `inline-size: min(24rem, calc(100vw - 2rem))`; source host floating width `min(23.5rem, calc(100vw - 2rem))`, with max-height bounded by viewport; launch button 2.75rem square and positioned `20vh` from bottom and at least 1rem from right; drag handle has keyboard arrow movement of 24 CSS pixels and Home reset; input MaxLength 8000, one initial row, grows to five lines; source status response area max 12rem height, latest user text max 4.5rem, partial transcript max 4.5rem. The OpenConnect default MAY intentionally change FAB vertical placement to the user's previously specified *bottom right*, but MUST retain the anchored bubble visual language, tiny footprint, and a configurable legacy `20vh` placement preset. This difference is an explicit design change, not source behavior.

## 2. Non-negotiable user experience

- **ACR-UX-001:** Closed/minimized state takes no more than one small FAB button footprint (target 44–56 CSS px touch area), no full-height overlay or reserved layout column. No automatic pop-up on first load by default.
- **ACR-UX-002:** Expanded state is a **popover-like compact bubble**, anchored above and aligned to the FAB with a small notch/tail. It floats over the host document and does not cause surrounding layout reflow, horizontal scroll, or resize.
- **ACR-UX-003:** Default anchor is bottom-right with safe-area padding; offer typed enum positions (`BottomRight`, `BottomLeft`, `TopRight`, `TopLeft`, `Custom`) and optional screenshot-faithful `LegacyViewport20Vh` preset. On narrow viewports, clamp width to viewport minus margins and keep input/buttons visible.
- **ACR-UX-004:** The bubble must visually resemble the reference image: pale/host-themed surface, subtle stroke/shadow, rounded corners, small drag glyph at left, small settings/stop/close at right, a labeled text field, context-sensitive right-end icons, subdued divider, tiny status indicator at bottom-left and compact voice/mic controls at bottom-right.
- **ACR-UX-005:** Drag is **handle-only**; input, buttons and other page elements remain interactive. Pointer/touch drag stays inside visual viewport and updates an optional persisted position; keyboard focus on handle accepts arrows (24 CSS px increments by default) and Home to reset. Position must re-clamp on resize, zoom, orientation and virtual keyboard changes. Drag disabled in attached/in-flow mode.
- **ACR-UX-006:** Opening always gives predictable focus to the input when not loading/listening; Escape returns to launcher if not dismissing a nested dialog; closing restores launcher focus and does not silently cancel a durable task unless close policy is explicitly `Cancel`.
- **ACR-UX-007:** User may close/minimize composer, navigate the host app or reconnect without losing the authorized durable task/session. The launcher can show a restrained unread/active indicator without intrusive badges or constant motion.
- **ACR-UX-008:** No forced OpenConnect brand name: host app supplies label, placeholder, launcher icon, accent tokens, header/help text and optional empty-state suggestions. Safe neutral default name is "Assistant"; do not retain hardcoded "Ellai" in public SDK.
- **ACR-UX-009:** Host CSS, MudBlazor themes and JavaScript apps must not be overridden by broad selectors; style via local CSS isolation (Blazor) or scoped CSS variables/Shadow DOM strategy (web) with an explicit documented theme bridge.
- **ACR-UX-010:** Attached mode (e.g. Navigator tour card) reuses the *same composer content* without FAB and without floating positioning; not a second behavior implementation.

## 3. Launcher actions and visual anatomy

- **ACR-VIS-001:** Header row uses `Move` on left, `Settings`, `Stop session` and `Close` on right. Stop is disabled or hidden when no active/cancellable session; close remains operable while disconnected.
- **ACR-VIS-002:** Text input has a small label above/within field (`Message {AssistantName}`), placeholder (`Ask {AssistantName}…`), immediate editing state, auto-grow 1–5 lines, 8000-character configurable limit (hard server limit), and right-hand action cluster that never overlays typed text.
- **ACR-VIS-003:** Empty input shows a live-voice action (if available) and recorded mic action where relevant; nonempty valid input replaces the live-voice action with Send. When capturing, recorded mic becomes Stop; live voice shows an active waveform/graphic equalizer control and Stop. Disabled actions must expose truthful description, never pretend mic support.
- **ACR-VIS-004:** Response text and last sent message are concise **inline snippets** between field and status; they scroll within bounded local regions rather than turning the composer into an unbounded transcript. Richer history is an *optional explicit action* (view conversation in another host-approved surface).
- **ACR-VIS-005:** Bottom status row shows animated three-bar indicator and plain-language status (`Ready`, `Connecting…`, `Thinking…`, `Listening`, `Speaking`, `Waiting for approval`, `Reconnecting…`, `Error`); status long text is clamped/expandable, media links sanitized. Animation respects reduced motion.
- **ACR-VIS-006:** Optional audio footer shows small `Voice on`/`Listen` toggle and, when voice playback enabled, mic on/off state, with visible active/disabled semantic indicators. When no speech capability exists, omit nonfunctional toggles; text-first composer stays fully usable.
- **ACR-VIS-007:** Settings opens a small, explicit view for voice preferences, privacy notice, worker status, accessibility and available actions; no full settings page required. Dangerous settings never changed directly by model-generated UI.
- **ACR-VIS-008:** Launcher is a distinct circular FAB with primary accent and assistant/robot icon by default. Optional menu modes are derived from *authorized capability maps* (Ask, Show Me, Guide Me, Help Me Work); no fixed healthcare/business-specific choices or unavailable mode entries.
- **ACR-VIS-009:** For minimal mode, a single FAB click opens Ask composer; alternate modes may appear in optional FAB menu when multiple enabled capabilities exist.
- **ACR-VIS-010:** Visual fidelity must be verified with golden screenshots at reference-like desktop crop, phone viewport, dark theme, 200% zoom and small landscape. Layout targets in `composer-spec.md` are intended CSS targets, not claims extracted as pixel-perfect from the screenshot.

## 4. Text interaction, task and event lifecycle

- **ACR-TXT-001:** Enter submits valid nonempty text, Shift+Enter inserts newline, IME composition does not submit, held Enter cannot send twice, and disabled/cancelled states reject submission. Use the most recent DOM value for user gesture to avoid stale asynchronous binding races; server remains authoritative.
- **ACR-TXT-002:** Submit creates a task with a client idempotency key, maps to an authorized session and lead agent, and records accepted/processing/final state through existing OpenConnect API. A duplicated click/keydown or reconnect must not duplicate committed turns.
- **ACR-TXT-003:** Cancel (`Stop`) is distinct from Close/Minimize: it cancels active task and/or session according to explicit typed policy. The UI renders `Cancelling…`, waits for authoritative receipt, handles unknown outcome and retains a meaningful recovery action.
- **ACR-TXT-004:** UI status is derived from *real* gateway task/agent/tool/voice events over existing HTTP(S)+WS(S), never timers pretending execution or synthetic subagent progress. NATS JetStream remains internal to gateway/outbox; browser never connects to NATS.
- **ACR-TXT-005:** Accept partial streamed text and final sanitized response; maintain compact latest-user/latest-answer views. Reconnect uses session cursor, deduplication and user-scope authorization; uncommitted deltas may be reset from a durable snapshot.
- **ACR-TXT-006:** Model/tool outputs require default-on firewall and output inspection before being displayed/restored; tool authorization/capability discovery controlled on server. No model-generated arbitrary Razor, HTML, JS or client action.
- **ACR-TXT-007:** Detect and describe disconnected, offline, authorization expired, policy blocked, approval pending, invalid input, capability unavailable and server error states without trapping user input.
- **ACR-TXT-008:** Interactive agents use current authenticated user's execution context; operational runs use workload identity and are not automatically exposed in an unrelated user's composer. All component suggestions are filtered by the active user's grants.
- **ACR-TXT-009:** `IOpenConnectComponent` proposals can be activated in an optional scoped supplementary host-approved renderer; compact bubble displays a minimal link/button/summary and remains small.
- **ACR-TXT-010:** No prompt text, speech bytes, sensitive DOM data, bearer tokens or placeholder-to-PII mappings logged in frontend analytics or NATS events.

## 5. Speech and microphone behavior

- **ACR-AUD-001:** Voice output, recorded speech transcription, live speech dialog and microphone capture are **independent capabilities**, advertised by actual healthy workers/browser permissions; none are prerequisites for text composition or MCP.
- **ACR-AUD-002:** The voice toggle never implicitly asks for microphone access; mic permission is requested only from an explicit user gesture, with clear stop and indicator. Closing/leaving session stops active capture as policy requires.
- **ACR-AUD-003:** Recorded mic: start -> capture -> stop -> transcribe -> optionally auto-submit only once if configured and transcript accepted. Live mic: start -> VAD/turn recognition -> automatic accepted turn(s) -> stay listening until stop. Both use distinct user-facing state.
- **ACR-AUD-004:** Voice output uses OpenConnect's optional Kokoro/MMS-TTS routing and real speech capability metadata; default Kokoro English `af_heart` only when installed and healthy. If no TTS exists, voice UI hides/degrades gracefully and text remains complete.
- **ACR-AUD-005:** Output speech playback and input mic state must be independently toggled; `Voice on` does not imply `Mic on`, nor vice versa. Stop speech on barge-in; fence late audio chunks by speech epoch.
- **ACR-AUD-006:** Speech streams require appropriate codec/sample-rate/channel metadata, permissions, interruption and cancellation. No unauthorized external STT for strict-local audio; unsupported privacy coverage is blocked.
- **ACR-AUD-007:** UI shall disclose unavailable permission/insecure context/device, inactive worker, resource overload or explicit language mismatch. Never render fake waveform indicating recording when no capture is active.
- **ACR-AUD-008:** Browser autoplay must be activated through a user gesture; no surprise audio. Screen readers get status text and transcript even without speech.
- **ACR-AUD-009:** Stopping a live microphone session does not automatically disable response speech playback. Voice output may continue if the user leaves it on.
- **ACR-AUD-010:** Audio and transcription are optional across .NET Blazor, TS browser, and Node headless; Node headless SDK supports task APIs but does not expose browser microphone/UI functionality.

## 6. SDK integration and feature ownership

- **ACR-SDK-001:** Blazor integration: `AddOpenConnectClient(configure)` for task API and WS address; `<OpenConnectProvider>` in root/layout; `<MiniComposer />` anywhere inside provider scope (or layout-level default anchor). Do not register duplicate MudBlazor popover/dialog providers if already present.
- **ACR-SDK-002:** TypeScript package `@openconnect/client` remains headless/browser-neutral and supports existing fetch/WS client, versioned contracts and abort/reconnect. A framework-neutral browser UI package `@openconnect/composer` renders the same design; optional React adapter `@openconnect/react` (or a separate subpath) composes with existing client. A Node process MUST NOT require DOM globals.
- **ACR-SDK-003:** `MiniComposer` is a single **public design/API name** across platforms (Blazor component and browser/React renderable) with shared type/state/event behavior and design tokens. Pixel-perfect replication must be validated via cross-platform screenshots, not duplicated by hand-wavy styling.
- **ACR-SDK-004:** Both SDKs use same canonical OpenAPI/JSON schema events, common agent session IDs, client task idempotency and runtime status projection. No second backend channel, agent coordinator or token vault.
- **ACR-SDK-005:** Developers configure strongly typed options/enums for anchor position, close behavior, initial mode, voice visibility, input policy, status presentation, appearance and optional capability menus. Custom labels and brand tokens are validated strings.
- **ACR-SDK-006:** Composer actions use existing client task API, voice/router SDK and Navigator capability hooks. Disabling Navigator, voice, Playwright or other worker never disables the text composer.
- **ACR-SDK-007:** The UI must not self-grant cross-origin tool access, run arbitrary commands or mutate business data; all suggestions, Navigator commands and component renderers undergo host authorization.
- **ACR-SDK-008:** Native browser composer must be safe with SPA routing, SSR, hydration, Shadow DOM optionality and host app CSS. Multiple render roots are supported without event-handler duplication or leaked global CSS.
- **ACR-SDK-009:** Blazor Server and WebAssembly require tested rendering lifecycle; auto render mode must not execute JS interop before hydration or retain stale DotNetObjectReference after disconnect.
- **ACR-SDK-010:** No `Ellai`, SwiftAid, healthcare or application-domain literal labels in packaged SDK runtime; retain correct license/copyright attribution where actually required by copied code in a separate legal ledger.

## 7. Accessibility, responsiveness, compatibility

- **ACR-A11Y-001:** WCAG 2.2 AA targets, visible focus, semantic buttons/inputs and status `role=status`, labels for all icons; no pointer-only drag, keyboard trap or screen-reader inaccessible active/disabled control.
- **ACR-A11Y-002:** Accessible voice states announce `Microphone on/off`, `Voice playback on/off`, not just icon color. Recording permissions and stop are available by keyboard and touch.
- **ACR-A11Y-003:** When browser reports `prefers-reduced-motion`, suppress bar-wave and glowing animations while preserving state labels.
- **ACR-A11Y-004:** Maintain usable 44×44 CSS pixel target hitboxes on touch screens where possible; visual icon may remain smaller than hitbox. No minimum target sizes that make the bubble overflow narrow screens.
- **ACR-A11Y-005:** RTL, browser zoom, device safe-area insets, soft keyboard and mobile visualViewport must not strand the composer offscreen.
- **ACR-A11Y-006:** Honor high contrast and host font scaling; enforce minimum legible status typography and avoid relying on red/green alone for error/recording state.
- **ACR-A11Y-007:** No automatic focus/voice permission prompts on page load; user invocation is the activation boundary.

## 8. Lifecycle and persistence

- **ACR-LIFE-001:** Persist only safe UI preferences per authorized user/app: launcher corner, normalized drag offset, voice-enabled preference when consented, last open/closed state if explicitly configured, and dismissed introduction; never store raw messages or tokens in localStorage.
- **ACR-LIFE-002:** Close minimizes UI and disposes browser media/capture correctly; task may continue on server. Stop actively cancels selected task/session per configured policy. Logout clears scoped local state/permissions, disposes event listeners and avoids cross-user reuse.
- **ACR-LIFE-003:** Drag/input bridge attach must be idempotent; browser detachment/Blazor unmount while async module loading must not resurrect listeners. No leaked `ResizeObserver`, `MutationObserver`, audio context, WS subscription or timer.
- **ACR-LIFE-004:** Double mount/React StrictMode and Blazor rerender must not initialize duplicate composers or subscribe twice.
- **ACR-LIFE-005:** Composer may be placed in attached layout during Navigator/onboarding and return to floating anchor with coherent state, no duplicate sessions or keyboard listeners.

## 9. Operational and CI requirements

- **ACR-OPS-001:** Bundle size measured and published for the web UI; no dependency on Python models, Chromium or TTS just to render composer. Lazy-load interaction JS and any optional audio modules.
- **ACR-OPS-002:** `OpenConnect.Components.Blazor` uses MudBlazor components where appropriate; browser UI uses standards-based DOM/CSS with equivalent interaction semantics, not a NuGet-to-JS dependency.
- **ACR-OPS-003:** Tests include bUnit, JS unit tests, Playwright screenshot regression, accessibility scan, package-consumer installs and auth/WS integration against a real gateway.
- **ACR-OPS-004:** CI must enforce visual screenshot baselines from supplied reference-style design across mobile/tablet/desktop, light/dark, no voice/voice active/status long/keyboard/drag.
- **ACR-OPS-005:** NuGet/npm release workflows publish composer packages with source links/symbols/types and dependency compatibility. Release only after clean consuming Blazor and TypeScript/React apps can mount, open, send, reconnect, close and dispose without layout regression.
- **ACR-OPS-006:** No model/worker download when launching text-only composer; capability absence is not shown as a fatal global error.

## 10. Acceptance test matrix

| ID | Scenario | Passing condition |
|---|---|---|
| CMP-AT-001 | Closed minimal UI | Only FAB visible, no reserved layout width or unsummoned overlay |
| CMP-AT-002 | Open anchor geometry | Compact bubble and tail align with FAB; no viewport overflow |
| CMP-AT-003 | Reference screenshot parity | Header/input/divider/status/voice/mic controls visually faithful at target crop |
| CMP-AT-004 | Close versus Stop | Close hides but does not silently cancel; Stop dispatches scoped cancellation |
| CMP-AT-005 | Drag pointer + keyboard | Handle only, 24px arrows, Home reset, resize clamp, no offscreen state |
| CMP-AT-006 | Enter + IME | Single current-value submit; Shift+Enter newline; IME/held Enter ignored |
| CMP-AT-007 | Text-only environment | No TTS/STT/browser worker; send/response/status still work |
| CMP-AT-008 | Control conditionality | Empty field live icon, filled field Send, active recording Stop and waveform |
| CMP-AT-009 | Output status/progress | Real `Ready/Thinking/Reconnecting/...` from authorized events, no fake task lifecycle |
| CMP-AT-010 | Mic/voice independent state | Voice/mic toggled separately; stopping mic leaves playback setting intact |
| CMP-AT-011 | Real voice worker | Enabled installed capability, correct audio metadata, speech epoch and stop |
| CMP-AT-012 | Offline/unavailable microphone | No unauthorized permission prompt; readable disabled/unavailable state |
| CMP-AT-013 | Compact response | Latest messages scroll in bounded regions; bubble does not become large transcript |
| CMP-AT-014 | Reconnect and idempotency | No duplicated accepted turn, durable session restored within scope |
| CMP-AT-015 | Auth change | Logout/user switch clears scoped bridge/capability/session UI; no leak |
| CMP-AT-016 | Blazor external consumer | Layout Provider + MiniComposer mounts in tested render modes; no duplicate Mud providers |
| CMP-AT-017 | TS/React external consumer | Browser UI mounts; Node SSR import without DOM access succeeds |
| CMP-AT-018 | A11y | Labels, keyboard focus, touch hitboxes, reduced motion, contrast tests pass |
| CMP-AT-019 | Mobile/RTL/zoom | Within viewport and safe areas, keyboard does not cover composer |
| CMP-AT-020 | Component/Navigator integration | Authorized suggestion triggers standard task; attached tour uses same content |
| CMP-AT-021 | Privacy/observability | No raw canary prompt/PII/audio or user credentials in unsafe sinks |
| CMP-AT-022 | Event state and replay | Accepted/delta/final/stop status reconciles across WS reconnect |
| CMP-AT-023 | Package build and release | NuGet/npm OIDC workflow and clean-consumer smoke pass |
| CMP-AT-024 | Cleanup/StrictMode | Unmount/re-attach doesn't leak duplicate handlers/audio/WS observers |

## 11. Release phases and definition of done

**P0:** reproduce shell, FAB, input, drag, status, close/stop and text-only gateway interaction in Blazor and TypeScript/browser. **P1:** voice capture/playback and conditional actions with honest capability detection. **P2:** authorized component suggestions/Navigator attachment, settings/menu, enhanced accessibility, dark/mobile/RTL hardening. **P3:** package publishing/visual regression in CI and docs examples; release of P0 only as clearly labeled limited preview if voice and Navigator remain absent. Each release must preserve all core security tests, and no voice feature may be advertised without real worker evidence.

## 12. Reference and migration hygiene

Sources were inspected on public GitHub at pinned tree SHA `1c98e0a1efc94ac517805a6454216fde7b6e8743`, and the screenshot was supplied by the user. This is evidence of source structure, **not** proof a clean OpenConnect runtime already exists or its tests pass. For code extraction, inspect repository license, copyright headers and third-party licenses; record source SHA and treatment in migration ledger. Do not copy domain-specific application state, credentials, patient data or healthcare-only modes into OpenConnect. Product-facing package documentation should use generic OpenConnect/host terminology; legal notices must remain as required.




---

## Annex 4: Navigator requirements

**Preserved source:** `navigator-requirements(3).md` · **SHA-256:** `954e304b78daaa4f88ee5b6c7790a5ea6216b985955274de315ad884a92cc52a` · **Content retained verbatim below.**

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




---

## Annex 5: Observability requirements

**Preserved source:** `observability-requirements(1).md` · **SHA-256:** `ff90f439e2459b49e680941078f0d9328537cdd9715dbb212f59c4ae678f5db6` · **Content retained verbatim below.**

# OpenConnect — End-to-End Observability Requirements

> **File:** `observability-requirements.md`  
> **Status:** Proposed baseline for implementation  
> **Version:** 1.0.0  
> **Date:** 2026-11-06  
> **Applies to:** OpenConnect gateway, firewall, model adapters, multimodal pipelines, interactive and operational agents, durable conversations, vault, and background workers  
> **Companion:** [`observability-spec.md`](./observability-spec.md)

## 1. Executive summary

OpenConnect SHALL provide an integrated, privacy-preserving observability system built on **OpenTelemetry** (standard instrumentation and OTLP interoperability) with **optional .NET Aspire** integrations for local orchestration and diagnostics. OpenConnect SHALL retain ownership of cost accounting, budgets, governance, policy analytics, and tenant-aware administrative reports in its **durable metering and audit stores**. No Aspire AppHost, external collector, Grafana installation, or third-party hosted observability vendor may be required to run the core gateway.

The system SHALL provide correlated visibility into every material stage of a multimodal AI operation: ingress, authentication, policy selection, input firewall, PII tokenization, model routing, provider inference, tool or agent execution, response firewall, restoration, response streaming, persistence, and egress. It SHALL allow administrators to understand security decisions and added latency without exposing raw protected content. It SHALL account for external provider expenses and, where configured, estimate local infrastructure expenses for running models, speech workers, and detectors.

These requirements use **MUST/SHALL** for mandatory capabilities, **SHOULD** for strongly recommended capabilities, and **MAY** for optional enhancements. **P0** = foundational release, **P1** = production-hardening release, **P2** = future enhancement. Targets marked *initial SLO proposal* are adjustable operational defaults, not validated performance claims.

## 2. Goals and non-goals

### 2.1 Goals

1. End-to-end cross-process traces across HTTP, streaming, asynchronous messaging, agents, workers, tool calls, and model providers.
2. Minimal instrumentation and infrastructure overhead for low-resource installations.
3. Cost and usage accounting that remains correct with tracing disabled or sampled and survives exporter outages.
4. Actionable firewall telemetry, including configurable GLiNER/rules, DeBERTa prompt-injection detection, Qwen3Guard safety checks, tokenization and restoration, and custom plugins.
5. A tenant-scoped, authenticated Admin Console covering reliability, security, budgets, spend, workflows, and configuration health.
6. OTLP interoperability, optional Aspire AppHost and standalone dashboard, and portable backends.
7. Strongly typed .NET configuration with explicit enums, validation, secure defaults, and deployment profiles.
8. Auditability, security boundaries, retention, cardinality controls, and privacy by construction.
9. Deterministic verification through integration tests, synthetic workloads, benchmark datasets, and operational runbooks.

### 2.2 Non-goals

- Replacing existing log, trace, or metric storage vendors.
- Treating sampled spans or Prometheus counters as the source of truth for customer invoices.
- Recording full prompts, completions, PII, secrets, or vault plaintext by default.
- Claiming classifier precision/recall from unreviewed live traffic alone.
- Mandating deployment of every detector, model worker, or dashboard.
- Promising precise provider invoice equivalence where provider pricing, rounding, taxes, or hidden usage are unavailable.
- Automatically charging tenants for local compute estimates without an explicit billing policy.

## 3. Actors and authorization boundaries

| Actor | Authorized visibility | Restrictions |
|---|---|---|
| Platform operator | Cross-service health, aggregate cost, provider and detector performance | No raw message content by default; privileged access audited |
| Security administrator | Firewall policies, detection outcomes, reviewed cases, detector effectiveness | Case review must use explicit permission and safe/redacted view |
| Billing administrator | Price schedules, usage records, budgets, reconciliation, cost exports | Cannot retrieve secrets or vault mappings |
| Tenant administrator | Own tenant's agents, usage, requests, spend, and budgets | No data from other tenants |
| Application operator | Authorized applications, usage, alerts, and traces | Scoped to application and tenant |
| Interactive end user | Only own request status or cost if explicitly enabled by product | No access to admin diagnostic spans |
| Operational-agent scheduler/service | Machine identity for background jobs and usage attribution | Never impersonates an active human user |
| Auditor | Immutable security and administrative audit events | Read-only; access independently logged |

Authorization SHALL be enforced server-side at query time for every drill-down, export, trace link, and API endpoint. Tenant identifiers SHALL originate from validated authorization context, not user-supplied telemetry labels.

## 4. Capability and priority matrix

| ID | Capability | Priority | Required outcome |
|---|---|---|---|
| OBS-001 | Native `ActivitySource`, `Meter`, structured logs | P0 | All core execution paths instrumented |
| OBS-002 | OpenTelemetry and OTLP export configuration | P0 | Opt-in external export, no hard dependency |
| OBS-003 | Optional Aspire AppHost integration | P1 | Start development stack with correlated resources |
| OBS-004 | Optional standalone Aspire Dashboard | P1 | OTLP diagnostics without AppHost |
| OBS-005 | Durable per-operation model metering | P0 | Every billable attempt accounted once |
| OBS-006 | Versioned provider pricing and estimates | P0 | Reproducible costs and provenance |
| OBS-007 | Tenant/app/model/agent cost breakdowns | P0 | Secure aggregation and drill-down |
| OBS-008 | Budgets, quotas, and spending alerts | P1 | Enforcement and notifications where configured |
| OBS-009 | Firewall spans, metrics, and outcomes | P0 | Decision, stage, and detector timings |
| OBS-010 | Optional detector deployment awareness | P0 | Disabled/skipped/not-installed versus executed |
| OBS-011 | PII vault/tokenization instrumentation | P0 | Latency and errors without secrets |
| OBS-012 | Interactive-agent identity correlation | P0 | Auth-context attribution without identity leakage |
| OBS-013 | Operational-agent and worker visibility | P0 | Durable run lifecycle and retries |
| OBS-014 | Multimodal telemetry | P1 | Text, speech, image, video unit accounting |
| OBS-015 | Low-resource deployment profile | P0 | No mandatory collector or external database |
| OBS-016 | Privacy, RBAC, retention and audit | P0 | Tenant isolation and redaction default |
| OBS-017 | Alerting, SLOs, and runbooks | P1 | Actionable incident response |
| OBS-018 | Telemetry pipeline self-monitoring | P1 | Drops, failures, buffers, export health |
| OBS-019 | Detector quality benchmarking | P1 | Ground-truth precision/recall/calibration |
| OBS-020 | Firewall configuration comparison | P2 | Evidence-based security/performance trade-offs |
| OBS-021 | Admin APIs and dashboards | P1 | Unified operational view |
| OBS-022 | Reconciliation and audit-safe exports | P1 | Explain estimates, corrections and provider differences |
| OBS-023 | Forward-compatible GenAI semantic conventions | P0 | Versioned mapping; no double-emission |
| OBS-024 | Cancellation, streaming and partial failure accounting | P0 | No dropped or doubled usage |
| OBS-025 | Deployment/upgrade compatibility | P1 | Rolling upgrades with versioned schemas |

## 5. Functional requirements

### 5.1 Telemetry foundation

- **REQ-TEL-001 [P0]** OpenConnect SHALL expose stable named .NET `ActivitySource` and `Meter` instances for gateway, firewall, AI inference, metering, agents, vault, and workers.
- **REQ-TEL-002 [P0]** SDK registration SHALL be additive and idempotent with existing OpenTelemetry configuration, including Aspire Service Defaults; duplicate span processors, exporters, and HTTP instrumentation SHALL be avoided.
- **REQ-TEL-003 [P0]** Tracing, metrics, structured logging, diagnostic export, and durable accounting SHALL have independent enablement and settings. Disabling tracing SHALL NOT disable ledger writes.
- **REQ-TEL-004 [P0]** Each trace SHALL carry service name, deployment environment, service version, and instance identifier when available. Request correlation SHALL use W3C trace context; baggage SHALL be minimal and allowlisted.
- **REQ-TEL-005 [P0]** The system SHALL support OTLP/gRPC and OTLP/HTTP via configured exporter packages, including endpoint, protocol, authentication headers, TLS, timeouts, buffering, and batch settings.
- **REQ-TEL-006 [P0]** Instrumentation MUST NOT block successful inference or downgrade firewall policy on export failures. Local financial accounting failure SHALL be handled separately according to a configured accounting-failure policy.
- **REQ-TEL-007 [P0]** Admin-configurable head sampling SHALL be supported; events and measurements required for metering and audit SHALL never depend on sample decisions.
- **REQ-TEL-008 [P1]** Optional collector-side tail sampling, filtering, routing and retention SHALL be supported without gateway-specific collector coupling.
- **REQ-TEL-009 [P0]** OpenConnect-specific instrumentation names SHALL be versioned and documented. Published semantic conventions SHALL be adopted through a pinned compatibility profile, because GenAI conventions may evolve.
- **REQ-TEL-010 [P1]** A telemetry health endpoint SHALL report operational export readiness and recent failures without leaking endpoints, credentials, or tenant identifiers.

### 5.2 Gateway, routing and conversation visibility

- **REQ-GW-001 [P0]** Each ingress operation SHALL measure request count, outcome, payload-size bands, total latency, first-response latency, streaming duration, and in-flight requests.
- **REQ-GW-002 [P0]** Traces SHALL distinguish authentication/authorization, route selection, rate limiting, input inspection, inference, output inspection, storage, and response delivery.
- **REQ-GW-003 [P0]** Routing telemetry SHALL capture policy identifier/version, provider and model chosen, retry/failover count, circuit-breaker decisions, and failure category. Secrets and full routing prompt content MUST NOT appear.
- **REQ-GW-004 [P0]** Durable conversation operations SHALL track persistence latency, append failures, concurrency conflicts, and storage errors. Actual conversation payloads SHALL remain outside telemetry.
- **REQ-GW-005 [P1]** Operations SHALL expose request correlation for a conversation turn without adding high-cardinality conversation IDs as metric dimensions.
- **REQ-GW-006 [P0]** Streaming traces SHALL record time-to-first-token/chunk and distinguish successful completion, client cancellation, timeout, provider interruption, and late usage reports.

### 5.3 Model inference usage and cost metering

- **REQ-MET-001 [P0]** Every logically billable provider or local-model inference attempt SHALL create one uniquely identified usage operation. Retries SHALL have separate attempt IDs and a common parent logical-request ID.
- **REQ-MET-002 [P0]** Metering SHALL record tenant, application, provider, model, operation/modality, region if known, agent execution if applicable, timestamps, status, usage provenance, price version, monetary currency and amounts.
- **REQ-MET-003 [P0]** Usage SHALL distinguish input/output totals, cached-read/cache-write subsets, reasoning-output subsets, modality-specific units, and extra billable provider/tool charges as supported.
- **REQ-MET-004 [P0]** Provider-reported usage SHALL take precedence. Tokenizer/local estimates SHALL be explicitly flagged; unknown usage SHALL remain unknown rather than silently becoming zero.
- **REQ-MET-005 [P0]** The pricing catalog SHALL be versioned with effective intervals, currencies, units, region/tier qualifiers, and source provenance. Original price schedule or immutable snapshot SHALL remain accessible for recalculation.
- **REQ-MET-006 [P0]** Costs SHALL be calculated using decimal arithmetic and correctly normalize cache and reasoning subcategories to avoid double counting.
- **REQ-MET-007 [P0]** At-least-once delivery and idempotent ledger ingestion SHALL ensure retries or worker restarts do not duplicate billable usage.
- **REQ-MET-008 [P0]** Usage from streamed, aborted, failed, or timed-out requests SHALL be recorded if billable, including late provider usage and provisional-to-final corrections.
- **REQ-MET-009 [P0]** Cost ledgers SHALL survive OTLP outages and trace sampling; ledger writes SHALL be independent of dashboards and exporters.
- **REQ-MET-010 [P1]** Reports SHALL support provider, model, tenant, application, agent, work item, route, modality, and date-range aggregations, with authorization-safe filtering and pagination.
- **REQ-MET-011 [P1]** A reconciliation job SHALL compare estimates against final provider reports/invoices where integrations exist and apply immutable adjustment records rather than overwrite financial history.
- **REQ-MET-012 [P1]** Budgets SHALL support hard and soft limits, rolling/calendar periods, quota units, alert thresholds, and policy for unknown/estimated usage.
- **REQ-MET-013 [P1]** When configured for hard enforcement, reservations or conservative pre-call estimates SHALL prevent concurrent requests from overrunning a budget without coordination.
- **REQ-MET-014 [P1]** External API expense SHALL be distinguished from optional locally estimated CPU/GPU/infrastructure cost. These SHALL never be combined into an invoiced amount without explicit billing policy.
- **REQ-MET-015 [P0]** Currency and unit conversions SHALL be explicit and versioned; multi-currency totals SHALL not be summed without a chosen conversion rate and timestamp.
- **REQ-MET-016 [P1]** Export of usage records SHALL be supportable via paginated APIs and audit-safe CSV/Parquet or equivalent, with documented schemas.

### 5.4 Firewall and detector observability

- **REQ-FW-001 [P0]** Each request SHALL expose an aggregate firewall decision and separate input, output, and other applicable inspection stages.
- **REQ-FW-002 [P0]** Each configured detector SHALL expose its detector type, logical name, configuration/model version, execution state, duration, queue wait, and outcome.
- **REQ-FW-003 [P0]** Detector state SHALL distinguish `Disabled`, `NotInstalled`, `NotApplicable`, `SkippedByPolicy`, `SkippedByBudget`, `Executed`, `TimedOut`, `Failed`, and `Cancelled`.
- **REQ-FW-004 [P0]** Policy outcomes SHALL distinguish `Allow`, `Block`, `Review`, `Redact`, `Tokenize`, `Sanitize`, `Quarantine`, and `Error`; multiple detector findings SHALL not be counted as multiple blocked requests.
- **REQ-FW-005 [P0]** GLiNER/rules SHALL report PII entity-class counts and redact/tokenize action counts without raw entity values, offsets, restored values or token mappings.
- **REQ-FW-006 [P0]** DeBERTa shall report injection classification outcomes, bounded confidence bucket, and policy action, not raw prompt text.
- **REQ-FW-007 [P0]** Qwen3Guard SHALL report safety categories, outcome, model revision, and stage without disclosing content.
- **REQ-FW-008 [P0]** Vault tokenization and restoration SHALL report provider type, duration, operation count, availability and error class, but never tokens, cleartext, secrets, mapping keys, or vault paths.
- **REQ-FW-009 [P0]** Measurement SHALL separate detector execution time from queue time, wall-clock aggregate stage time, and overall additional request latency. Concurrent detector durations SHALL NOT be naively summed into request overhead.
- **REQ-FW-010 [P0]** Runtime SHALL report detector cold starts, resource pressure, timeouts, concurrency limits, circuit-breaker states, fallback actions and fail-open/fail-closed decisions.
- **REQ-FW-011 [P1]** Quality reports (precision, recall, F1, false-positive rate, false-negative rate, calibration) SHALL be computed only from labeled or reviewed evaluation datasets and SHALL display evaluation set/version and sample size.
- **REQ-FW-012 [P1]** Firewall analytics SHALL allow slicing by policy version, detector/version, traffic class, stage, outcome, deployment, and time range without high-cardinality metrics.
- **REQ-FW-013 [P1]** Shadow evaluation/canary comparison MAY run only with explicit policy authorization and bounded resource budgets; shadow output MUST NOT implicitly affect live policy decisions.
- **REQ-FW-014 [P2]** An optimizer MAY compare candidate detector configurations on latency, resource usage, estimated infrastructure cost, and labeled security performance.

### 5.5 Agents, tools and background workers

- **REQ-AG-001 [P0]** Interactive agents SHALL inherit the active authenticated user's authorized context for execution and attribution, without exposing raw credentials in telemetry.
- **REQ-AG-002 [P0]** Operational agents SHALL execute under a designated service/workload identity and record scheduling, activation, lease acquisition, heartbeats, execution, retries, checkpoints, and completion.
- **REQ-AG-003 [P0]** Agent traces SHALL distinguish plan/step, model calls, tool invocation, human-approval wait, delegated work, memory/retrieval, and external API calls.
- **REQ-AG-004 [P0]** Agent execution and child model/tool cost SHALL be attributable without summing an aggregate run charge twice.
- **REQ-AG-005 [P0]** Async trace propagation SHALL support `traceparent` and `tracestate` in approved message metadata; durable execution ID SHALL allow correlation when trace continuity is intentionally broken.
- **REQ-AG-006 [P1]** Admin views SHALL show current agent health, queue depth, task lag, stuck runs, and failure/retry distributions by agent type.
- **REQ-AG-007 [P1]** Tool telemetry SHALL identify tool kind, status, duration, and explicit charges while respecting per-tool secret and payload suppression.

### 5.6 Multimodal, local models and detector infrastructure

- **REQ-MM-001 [P1]** Audio workloads SHALL report provider billable units (e.g., seconds/minutes/tokens as defined by price schedule), audio duration, stream latency, and cancellation.
- **REQ-MM-002 [P1]** Image workloads SHALL report image count, configured resolution/quality tiers, modality-specific tokens where returned, and model usage source.
- **REQ-MM-003 [P1]** Video workloads SHALL report duration, frame count where applicable, and provider-specific billable units.
- **REQ-MM-004 [P1]** Local inference worker telemetry SHALL expose process/container CPU, memory, queue depth, concurrency and optional accelerator utilization where measurable; unavailable GPU counters SHALL show `unavailable` rather than `0`.
- **REQ-MM-005 [P1]** Infrastructure cost estimates SHALL disclose allocation method, sampling granularity, machine price profile, and confidence/quality limitations.

### 5.7 Administrative experience and alerting

- **REQ-ADM-001 [P1]** Admin UI SHALL provide overview, model cost, firewall, detectors, routing, agent execution, runtime health, vault health, audit, budgets, alerts, and integrations views.
- **REQ-ADM-002 [P1]** Default landing page SHALL present request volume, error rate, p50/p95/p99 latency, first-token latency, total spend, unknown-cost percentage, security decisions, firewall overhead, and active alerts.
- **REQ-ADM-003 [P1]** Every aggregated figure SHALL indicate its time zone, aggregation window, source, completeness/freshness and whether calculated or estimated.
- **REQ-ADM-004 [P1]** Users SHALL drill from aggregate to an authorized request/execution detail and from there to its trace via a backend-neutral link or configured viewer.
- **REQ-ADM-005 [P1]** Admin APIs SHALL support filtering, stable pagination, tenant isolation, RBAC, throttling, and export authorization.
- **REQ-ADM-006 [P1]** Alert rules SHALL support request error/latency, provider failures, cost rate/budget, firewall fail-open, detector saturation, vault errors, dropped telemetry, and stalled agents.
- **REQ-ADM-007 [P1]** Alerts SHALL include routing, deduplication, silence windows, escalation policy, and actionable runbook links. Notifications MAY be implemented via webhook, email, or configured integrations.
- **REQ-ADM-008 [P1]** Each admin change to observability, pricing, budgets, detector policy or export settings SHALL create an auditable event with actor, time, scope, old/new safe summary, and result.
- **REQ-ADM-009 [P1]** UI SHALL clearly distinguish detection **rate** from empirically evaluated **accuracy**; unavailable quality labels SHALL not be replaced with guesses.

### 5.8 Aspire and export interoperability

- **REQ-ASP-001 [P1]** `OpenConnect.Hosting.Aspire` SHALL be optional and SHALL not be referenced by core packages.
- **REQ-ASP-002 [P1]** OpenConnect SHALL run in Aspire AppHost and independently (ordinary ASP.NET Core host, Docker Compose, Kubernetes or system service).
- **REQ-ASP-003 [P1]** Standalone Aspire Dashboard SHALL consume OpenConnect OTLP streams with no AppHost requirement.
- **REQ-ASP-004 [P1]** Integration documentation SHALL distinguish standalone telemetry viewing from AppHost-only resource orchestration features.
- **REQ-ASP-005 [P1]** Aspire UI SHALL be treated as sensitive operational administration; exposure SHALL require network isolation and authentication. Linking from Admin UI SHALL NOT bypass Aspire authorization.
- **REQ-ASP-006 [P1]** Production deployment SHALL support collector-to-backend pipelines (e.g. Prometheus-compatible metrics, Grafana-compatible traces/logs), without requiring vendor-specific SDK instrumentation.
- **REQ-ASP-007 [P1]** OpenConnect SHALL expose a capability/health report so the Admin UI can report which diagnostic backend is enabled, misconfigured, degraded or disconnected.

## 6. Required data semantics

### 6.1 Entity and identity model

At minimum, instrumentation and accounting SHALL define: `Tenant`, `Application`, `Environment`, `Service`, `Request`, `ConversationTurn`, `AgentExecution`, `AgentStep`, `ModelCall`, `ModelAttempt`, `FirewallEvaluation`, `DetectorExecution`, `VaultOperation`, `UsageRecord`, `PriceSchedule`, `CostAdjustment`, `Budget`, `AuditEvent`, and `TelemetryExportStatus`. IDs MUST be globally unambiguous within their declared scope. Public/metric labels SHALL be separate from confidential entity IDs.

### 6.2 Financial correctness

- A logical user request may make multiple paid model attempts, including retries, tool calls, planning and shadow calls.
- **Attribution:** each attempt belongs to exactly one ledger identity; parent summaries derive costs from children rather than posting duplicate line items.
- **Idempotency:** repeated finalization of the same provider attempt produces at most one final charge record plus explicit adjustments.
- **Partiality:** when usage is unknown, record `Unknown`; do not treat it as free.
- **Estimate revisions:** provisional estimates and final provider usage must be linked and versioned. Reconciliation adjustments retain an audit trail.
- **Currency:** amount/currency and conversion rate are explicit. No implicit mixed-currency sums.
- **Retention:** financial records and their price references remain for the configured accounting retention independent of trace sampling/TTL.

### 6.3 Firewall correctness

- Aggregate request block rate = number of distinct evaluated requests whose **final** action is `Block` / number of evaluated requests (same window/filter).
- Detector finding count, individual detector block recommendation count, final policy block count, and prevented provider call count are separate measures.
- Inspection latency is measured with a monotonic clock. End-to-end firewall overhead is the critical-path contribution and MUST account for concurrency and queuing.
- True positive, false positive, true negative and false negative require reviewed ground truth and explicit dataset provenance.

## 7. Privacy, safety and compliance requirements

- **REQ-SEC-001 [P0]** Default telemetry content policy is `MetadataOnly`; raw prompt/completion capture is prohibited by default.
- **REQ-SEC-002 [P0]** Trace attributes, log scopes, metrics labels, exceptions, stack traces and exporter headers SHALL be scrubbed with an explicit allowlist/denylist before export where technically feasible.
- **REQ-SEC-003 [P0]** Passwords, authorization headers, API keys, refresh tokens, vault mappings, tokenized original values and restoration output SHALL NEVER be exported as telemetry.
- **REQ-SEC-004 [P0]** Tenant isolation SHALL be tested against direct API access, bulk export, indirect trace links, dashboards, and cross-tenant joins.
- **REQ-SEC-005 [P0]** Telemetry transport SHALL support TLS/authenticated OTLP; production endpoints SHALL not be unauthenticated on public networks.
- **REQ-SEC-006 [P0]** Metrics labels SHALL use a documented finite-cardinality allowlist. Per-request, per-user, conversation and other unbounded IDs SHALL NOT be metric dimensions.
- **REQ-SEC-007 [P0]** Admin API/audit/storage SHALL use least privilege; secrets SHALL be injected through trusted configuration/secret stores.
- **REQ-SEC-008 [P1]** Data retention SHALL be individually configurable for metrics, traces, logs, ledgers, evaluations, and audit events, with deletion/archival workflows and legal-hold policy where applicable.
- **REQ-SEC-009 [P1]** Export destination changes, policy overrides, elevated diagnostic access, and report downloads SHALL be audited.
- **REQ-SEC-010 [P1]** Optional redacted-content debugging SHALL require an explicit admin policy, limited duration, access logging and per-tenant approval; it SHALL NOT weaken vault isolation.

## 8. Configuration and operational requirements

- **REQ-CFG-001 [P0]** Use options classes, enums, configuration validation, and `ValidateOnStart()`. Unknown enum values and malformed URIs MUST fail startup configuration validation where relevant.
- **REQ-CFG-002 [P0]** At least `Minimal`, `Standard` and `Full` deployment profiles SHALL be documented; low-resource mode SHALL require no Aspire, collector, message broker or hosted monitoring account.
- **REQ-CFG-003 [P0]** Detector enablement and policy inclusion SHALL be independently configurable; disabling one detector shall not trigger implicit model download or initialization.
- **REQ-CFG-004 [P0]** Sensitive values MUST come from secrets/environment/config providers, not checked-in JSON.
- **REQ-CFG-005 [P1]** Settings SHALL have precedence rules (code defaults < configuration providers < environment < explicit operator override) and publish their effective *non-secret* configuration in admin view.
- **REQ-CFG-006 [P1]** Runtime changes SHALL be classified as hot-reload-safe versus restart-required and audited.
- **REQ-CFG-007 [P1]** Package and database schema version compatibility SHALL be maintained through supported rolling upgrades.

## 9. Nonfunctional requirements and initial SLO proposals

Targets below are **proposed acceptance defaults**; deployers MAY override by environment and benchmark findings.

| ID | Area | Proposed target / constraint | Measurement |
|---|---|---|---|
| NFR-001 | Base gateway overhead | < 3% p95 latency increase from metrics-only instrumentation at reference load | A/B benchmark vs instrumentation off |
| NFR-002 | Standard tracing overhead | < 5% p95 increase at 10% sampling at reference load | Controlled load benchmark |
| NFR-003 | Export isolation | OTLP backend unavailable for 30 min without request failure attributable to exporter | Fault injection |
| NFR-004 | Accounting correctness | 100% of successfully persisted billable attempts reconcile without duplicate finalized IDs | Integration/replay tests |
| NFR-005 | Ledger durability | No acknowledged ledger event lost after process restart within selected persistence guarantees | Crash/recovery tests |
| NFR-006 | Dashboard freshness | New finalized usage visible within 60 s p95 in Standard profile | Synthetic usage events |
| NFR-007 | Tenant isolation | Zero cross-tenant read/export access in automated authorization suite | Penetration and property tests |
| NFR-008 | Cardinality | Bounded metric series per deployment based on configured label budgets | Load tests and metric scrape audit |
| NFR-009 | Health monitoring | Exporter/worker failure reported within 2 minutes in Full profile | Chaos tests |
| NFR-010 | Cost drift | Estimated vs provider-finalized discrepancy explicitly reported; no hidden adjustment | Reconciliation checks |
| NFR-011 | Detector completeness | Every scheduled detector yields exactly one terminal execution state | Correlation tests |
| NFR-012 | Graceful degradation | Analytics outage does not disable firewall or grant unsafe permissions | Failure simulation |

Performance tests SHALL specify hardware, concurrency, model selection, payload mix, sampling rates, database and collector placement, and network conditions. No universal detector latency SLA is implied.

## 10. Deployment profiles

| Capability | Minimal | Standard | Full |
|---|---|---|---|
| Built-in metrics | On | On | On |
| Structured logs | On | On | On |
| Trace collection | Off or manual | Sampled | Configurable/adaptive |
| Durable metering | SQLite or configured provider | SQLite/PostgreSQL | PostgreSQL or production database |
| OTLP exporter | Off | Optional | Recommended |
| Collector | Not required | Optional | Recommended |
| Aspire AppHost | Optional dev-only | Optional | Optional |
| Standalone Aspire | Optional dev-only | Optional | Optional |
| Production telemetry storage | None required | Optional | Required for historical dashboards |
| Admin analytics | Local, bounded | Operational | Full historical and multi-tenant |
| Alerting | Local limits | Basic | Routing/escalation |
| GPU/resource meters | Optional | Optional | Configured as available |

The package graph SHALL permit installing core instrumentation without the Aspire hosting package, Admin UI, external exporters, or vendor-specific integrations.

## 11. User stories and acceptance criteria

**US-01 — Small-environment deployer (P0):** As a developer operating a single-node instance, I can disable distributed tracing and external exporters, still see gateway/firewall counters, and still persist usage. **Accept:** startup succeeds offline; no external service is contacted; usage survives restart; absent detectors do not load models.

**US-02 — Platform operator (P1):** I can investigate a slow request from gateway to model and firewall. **Accept:** authorized request detail shows stage timeline, nested spans or explicit correlation IDs, queue duration, retries, provider TTFT, final outcome and trace link when retained.

**US-03 — Billing administrator (P0/P1):** I can calculate cost by tenant/model/provider and explain the result. **Accept:** record includes usage provenance, effective price version, units, currency, attempt identity, finalization state and corrections; totals avoid cached-token double charging.

**US-04 — Security administrator (P0/P1):** I can tell whether an unsafe request was blocked by DeBERTa, Qwen3Guard or rules, and measure their overhead. **Accept:** finding/action is trace-correlated; skipped/disabled states visible; aggregate block count is de-duplicated; no raw prompt is exposed.

**US-05 — Privacy officer (P0):** I can validate observability does not exfiltrate content or vault secrets. **Accept:** seeded secret/PII canary values never appear in logs, traces, metric labels, exports, errors or UI under default policy.

**US-06 — Agent operator (P0/P1):** I can distinguish interactive user-scoped tasks and scheduler-owned operational runs. **Accept:** correct actor type, workload identity, run lifecycle, retry/lease events and child expenses; no recorded auth token.

**US-07 — Dev with Aspire (P1):** I can run the AppHost sample and explore correlated traces. **Accept:** service defaults are not double registered and traces appear under expected service names.

**US-08 — Dev without Aspire (P1):** I can connect OpenConnect to a standalone Aspire dashboard or any OTLP receiver. **Accept:** OTLP export works from a normal host without AppHost or embedded Aspire code.

**US-09 — Budget owner (P1):** I can receive an alert or reject a model attempt when a quota is reached. **Accept:** concurrent requests follow documented reservation/settlement rules and alerts show clear currency and completeness.

**US-10 — Quality evaluator (P1):** I can view detector false-positive/negative statistics. **Accept:** dashboard requires labeled dataset, sampling method, dataset size, evaluation timestamp and detector/policy revision; live block rate is separately named.

**US-11 — Incident responder (P1):** I can observe an OTLP outage without an AI outage. **Accept:** exporter errors and drops surface through a separate health/diagnostic channel; inference policy and ledger remain correct.

**US-12 — Operator upgrading versions (P1):** I can perform a rolling deployment across instrumentation schema changes. **Accept:** compatible migrations, resource attribute schema version, documentation and backward-compatible metrics exist during the upgrade window.

## 12. Verification and release gates

### 12.1 Required automated test classes

1. Unit tests: pricing arithmetic, token subset normalization, enum validation, labeling, duration calculations, access policy, state transitions.
2. Contract tests: every model provider adapter normalizes usage and error semantics; semantic convention names/attributes are pinned.
3. Integration tests: HTTP/streaming gateway, optional firewall stages, background agent propagation, durable ledger, SQL idempotency, late usage correction.
4. Security tests: cross-tenant reads/exports, canary PII/secret leaks, malicious attributes, trace link authorization, injection into log fields.
5. Fault tests: OTLP unavailability, database outage, queue saturation, unexpected worker crash, duplicate events, clock skew, partial provider response.
6. Load tests: minimal/standard/full profile, metric cardinality, p50/p95/p99 latency, allocation, memory and collector pressure.
7. Evaluation tests: labeled detector datasets for security quality, stratified by language, policy, detector version, input length and modality where applicable.
8. UI tests: aggregation window, UTC vs local time, completeness indicators, empty state, estimated cost labeling, keyboard navigation and permitted drill-down.

### 12.2 Definition of done

A release MUST NOT be declared ready until P0 requirements have automated coverage, a documented self-hosted Minimal example exists, accounting/trace failures are independent, privacy canary testing passes, mandatory operational events have valid terminal states, and field names/retention/access rules are published. P1 features require production observability runbooks and alert validation before general availability.

## 13. Risks, mitigations and unresolved decisions

| Risk | Mitigation |
|---|---|
| Evolving OTel GenAI conventions | Pin a versioned semantic profile; map internally; compatibility tests; no unannounced renaming |
| Sampling hides a charged call | Separate ledger + audit from traces |
| Provider usage missing in streaming | Provisional estimate, late reconciliation, explicit `Unknown` |
| High-cardinality telemetry | Strict label allowlist and admission limits |
| Over-instrumentation overloads small hosts | Minimal profile; optional collectors/exporters; bounded buffering |
| Detector confidence mistaken for accuracy | Benchmark-derived metrics only |
| External dashboard exposes sensitive fields | Authentication, network restriction, no content capture |
| Collector or provider outage | Non-blocking bounded exporters, health and retry budgets |
| Pricing schedule changes | Immutable schedules, effective time and adjustments |
| Concurrency overspends quotas | Reservation/settlement with transactional enforcement |

**Architecture decisions to resolve before production:** default ledger DB and durable write policy; whether unknown-cost calls are allowed under hard budgets; tenant data retention and regional residency; accepted currency conversion source; whether per-user spend is exposed; telemetry backend selection for Full deployments; local detector GPU accounting availability; and any enterprise requirement for content-debug sessions. All have safe defaults in the companion specification.

## 14. References

The references are external technical guidance, not hard runtime dependencies:

1. OpenTelemetry GenAI client inference conventions: https://github.com/open-telemetry/semantic-conventions-genai/blob/main/docs/gen-ai/client-inference.md
2. OpenTelemetry GenAI token metric conventions: https://github.com/open-telemetry/semantic-conventions-genai/blob/main/docs/gen-ai/gen-ai-token-metrics.md
3. OpenTelemetry general metrics conventions: https://opentelemetry.io/docs/specs/semconv/general/metrics/
4. Aspire dashboard overview: https://learn.microsoft.com/dotnet/aspire/dashboard
5. Standalone Aspire dashboard: https://learn.microsoft.com/dotnet/aspire/fundamentals/dashboard/standalone

---

**Document ownership:** OpenConnect maintainers. Changes to P0 identifiers, monetary semantics, security defaults or published telemetry contracts require an architecture review and a versioned migration note.




---

## Annex 6: Prior bundle README

**Preserved source:** `README.md` · **SHA-256:** `1096df9c231d61af4a90be1f3e7a6ff4b21168ff9ebc067c8743ca5b12b440af` · **Content retained verbatim below.**

# OpenConnect engineering document bundle — v1.1

Read in this order:
1. `spec.md` — product scope, architecture and normative requirements.
2. `configuration.md` — **new** strongly typed .NET enums/options, default detector choices, Interactive/Operational agent contracts.
3. `firewall.md` — Python security worker, synchronous outbound gates, optional detectors and coverage-aware policy.
4. `implementation.md` — staged coding tasks (including IMP-090–099), CI/release execution guidance.
5. `checklist.md` — evidence gates and tests (including AT-031–042); items are unchecked intentionally.

## Key updates
- Privacy firewall remains default-on, but AI detector models are individually disabled by default. Rules-only is the lightweight baseline and is **not semantic PII protection**.
- Interactive agents run in active authenticated user context, reauthorized for tool use and bounded delegation.
- Operational agents run under distinct workload identities, scheduled and monitored by a durable .NET BackgroundService with NATS/outbox/leases.
- All closed options use C# enums and validated wire names; JSON/YAML/Python/TS schema parity is required.
- This bundle contains **design specifications**, not deployed code or verified CI results.


