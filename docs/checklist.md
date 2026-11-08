# OpenConnect — Unified Implementation and Release Checklist

**Document:** `checklist.md` · **Baseline:** 2026-11-07 · **Status:** ALL requirements and acceptance tests begin `NOT RUN` / unchecked unless separately evidenced in the actual repository/CI.  
**Companions:** [`requirements.md`](./requirements.md), [`spec.md`](./spec.md).

## 0. Instructions to Codex / Muse / DeepSeek / maintainers

1. Read `requirements.md` canonical section and conflict table, `spec.md` canonical architecture, **then this checklist**. Consult the preserved specialist annex corresponding to the task; never implement conflicting obsolete examples verbatim.
2. Inspect actual code and pin source revisions before changing packages. Maintain scope: do not drop a subsystem because it has no immediately available infrastructure, GPU, auth provider or release credential. Instead implement truthful optional registration and mark real-model/release test `BLOCKED` until evidence exists.
3. Execute one small `IMP-*`, `EF-IMP-*`, `NAV-IMP-*`, `CMP-IMP-*`, `OBS-IMP-*`, `SITE-IMP-*`, `VEC-IMP-*` or `KMS-IMP-*` unit per PR (or a dependency-aligned small set). Include negative security tests and docs.
4. Change `[ ]` to `[x]` **only** with a linked passing real test run, source commit and review. A design or CI script with no green execution is `NOT RUN`, not `PASS`. Never mark an acceptance test complete because it is described in source annexes.
5. For source reference integration, record code path, actual SHA, license, model checkpoint, package versions, changes and test mapping. Do not copy proprietary/sensitive business data or erase legally required attribution.
6. Rules for stable claims: no permission escalation, tenant data leak, unauthorized restoration, silent privacy downgrade, invented agent events, lost accepted task/outbox commit or package-source mismatch. Anything failing this gate is explicitly experimental/unavailable, not silently removed from roadmap.
7. Every completed PR: update this checklist status, add an evidence-log entry, include command line and environment, mention compatibility/migration impact and the next dependency-unblocked task.

## 1. Canonical stage and dependency gates

| Gate | Required work | Exit requirement |
|---|---|---|
| G0 baseline | Licenses, source pin, MAF compile smoke, contracts, strongly typed options | Clean checkout builds/contracts pass, no secrets, basic threat model |
| G1 core auth/agents | Default lead, interactive context, operational identity, tool/MCP authorization and capability map | Scope/approval negative tests pass |
| G2 durable API | EF four-provider adapter, transactions/outbox/leases, NATS, HTTP/WS replay | Real SQL Server+Postgres+SQLite durability tests and InMemory dev constraints |
| G3 privacy | Default host firewall, Python security worker, opt-in detector lifecycle, vault, restore, output gating | Zero deliberate canary leaks across tested boundaries; required coverage blocks if missing |
| G4 client/UX | Blazor+TS clients, compact MiniComposer, component metadata/renderer | Pinned screenshot regression, a11y, reconnect, cleanup and text-only E2E |
| G5 extensions | Navigator, Playwright, Qdrant, Infisical, Python workers | Independent capability-specific tests; missing modules don't break core |
| G6 observability | OTel, metering ledger, budgets, optional admin/Aspire | No double count/PII export, user/workload RBAC and accounting correctness |
| G7 docs | Astro Starlight static site, API docs, optional docs assistant | Static build succeeds offline; docs assistant uses framework with safe citations |
| G8 distribution | CI required checks, NuGet/npm/GHCR, signatures/SBOM/docs | Clean consumers install from registries; full release manifest |

## 2. Cross-cutting core and configuration work

- [ ] **CORE-C-001 [SEC]** Existing source inventory, MAF compatibility, pinned dependency versions, third-party licensing and no accidental domain copy.
- [ ] **CORE-C-002** Canonical OpenAPI/JSON Schema/C#/TS/Pydantic wire DTOs and migration/compatibility rules (IDs, events, errors, capability descriptors).
- [ ] **CORE-C-003** `AddOpenConnect()` idempotently registers default lead, default-on .NET firewall and typed options without model downloads, Docker or schema mutation.
- [ ] **CORE-C-004 [SEC]** `agent-runtime.md` and declarative agent config parse/validate; Markdown cannot grant tools, change tenant or disable policy.
- [ ] **CORE-C-005 [SEC]** Interactive `AgentExecutionContext` derived from actual authenticated user; delegation narrows grants, tool execution reauthorizes, revoke/reconnect cannot revive rights.
- [ ] **CORE-C-006 [SEC]** Operational agent supervisor uses separate workload identity, scheduled/event activation, misfire, lease/fence, retries, review/RBAC and idempotent durable run ledger.
- [ ] **CORE-C-007 [SEC]** Tool/MCP registration with typed side effects, scopes, bounded discovery, exact-argument approvals and no arbitrary tool privileges.
- [ ] **CORE-C-008** MAF lead/subagent actual execution traces; lifecycle events represent real agents/tools, not model-imagined results.
- [ ] **CORE-C-009** UI component capability maps, descriptions, suggestions, schema-based local rendering and authorization; no model arbitrary script/UI authority.
- [ ] **CORE-C-010** Provider/model capabilities, fallback, trust zone and worker discovery truthful; missing optional capability unavailable without affecting others.
- [ ] **CORE-C-011** Independently configurable strongly typed enum/options and wire names across all modules with strict startup validation and no numeric/unknown enums.
- [ ] **CORE-C-012** Core works in configured text-only environment with every optional module omitted.

## 3. Provider-neutral persistence and event delivery

- [ ] **DATA-C-001** `OpenConnect.Persistence.EFCore` has no transitive SQL Server, SQLite, PostgreSQL or InMemory provider package refs.
- [ ] **DATA-C-002** `.AddOpenConnectEntityFramework<ApplicationDbContext>(...)` uses developer-registered DbContext and explicit `ApplyOpenConnectModel()` without requiring `DbSet<T>` properties.
- [ ] **DATA-C-003** Dedicated OpenConnectDbContext supported independently; no inferred connection string or automatically shared transactions.
- [ ] **DATA-C-004 [SEC]** SQL Server/PostgreSQL schemas, SQLite prefix, per-entity tenant/workload indexes, model mapping version and EF model-cache key verified.
- [ ] **DATA-C-005 [SEC]** Manual migrations default, no DDL on package/DI registration, shared context never auto-migrated, reviewed migration upgrades leave app business tables and rows untouched.
- [ ] **DATA-C-006** InMemory explicitly dev-only and rejects durable production/operational lease contracts.
- [ ] **DATA-C-007** Session/task/turn/agent event/outbox/lease/approval/privacy token store contract tests under 3 real relational providers.
- [ ] **DATA-C-008 [SEC]** Accepted task+event+outbox atomic transaction and scoped user/workload authorization; external business transaction coordination defined.
- [ ] **DATA-C-009** Dispatcher fenced claim, broker ACK, idempotent consumer, publish-failure retry, TTL/backoff/quarantine, crash/replay/clock-skew verified per provider.
- [ ] **DATA-C-010** Separate IAgentEventSubscription receives committed notices; IEventJournal retains known-SessionId replay scope.
- [ ] **DATA-C-011** Public HTTP task/session/WS protocol, resume cursor and dedup; JWT/cookie origin and scope tests.
- [ ] **DATA-C-012** DB provider-specific capacity and distributed suitability (SQLite single writer, SQL Server/Postgres scaled) documented and tested.

## 4. Firewall, privacy and optional security detector suite

- [ ] **FW-C-001 [SEC]** Default-on host egress interlock with typed allow/mask/redact/block/review policy; privacy off explicit trust-config only and auth never off.
- [ ] **FW-C-002 [SEC]** Local Python security worker authenticated, isolated and resource bounded, no vault key/database grant; disconnected required coverage blocks before external egress.
- [ ] **FW-C-003** Baseline rules/checksums/secrets/known values only; GLiNER, DeBERTa, Qwen3Guard, PaddleOCR, YuNet independently enable/disable/load with manifest and readiness.
- [ ] **FW-C-004 [SEC]** Missing detector only blocks requests that policy requires; no silent rules-only fallback when semantic PII needed or image/audio unprotected.
- [ ] **FW-C-005 [SEC]** MIME/text/image/PDF/face/EXIF/audio controls, strict-local original-content checks and unsupported-modality denial.
- [ ] **FW-C-006 [SEC]** Cryptographic opaque tokens, envelope AES-256-GCM encrypted per-scope mappings, TTL/key rotation, authorized restoration and streaming split-token property tests.
- [ ] **FW-C-007 [SEC]** Output/tool/retrieval/worker/telemetry/NATS gates and cross-tenant/operational versus interactive isolation with canary tests.
- [ ] **FW-C-008** Optional persistent protected local KEK plus optional Infisical KMS key wrapping/rotation, no required Infisical service.
- [ ] **FW-C-009** Detector accuracy/latency/overmask measured on reproducible synthetic and language/domain evaluation datasets; no unsupported quality claim.
- [ ] **FW-C-010** Optional security worker Docker CPU/GPU profiles, licensed pinned models, no request-time download, readiness/kill/backpressure and signed image publication tests.

## 5. MiniComposer, client SDK and Navigator

- [ ] **UI-C-001** `AddOpenConnectClient()` .NET and `@openconnect/client` TypeScript/Node support same authenticated task HTTP and event WS contracts with no DOM dependency in Node.
- [ ] **UI-C-002** Blazor `OpenConnectProvider` and `MiniComposer` integrate without duplicate MudBlazor providers or duplicated sessions.
- [ ] **UI-C-003** Browser `@openconnect/composer` and optional React renderer match Blazor composer; SSR-safe, scoped CSS and no host reflow.
- [ ] **UI-C-004** Reference faithful: 44–56px FAB bottom-right, compact floating bubble/tail, header handle/settings/stop/close, editor, status, voice/mic footer.
- [ ] **UI-C-005** Drag keyboard/pointer/touch with clamping, 24px arrows, Home reset, safe areas, mobile visual viewport, zoom, RTL, attached Navigator flow.
- [ ] **UI-C-006** Enter/Shift+Enter/IME/duplicate prevention, 8000-char input, text streaming/latest bounded response, explicit Stop versus Close, WS replay status.
- [ ] **UI-C-007** A11y and visual golden screenshots match reference anatomy in light/dark, mobile/desktop, no voice/voice on, error/approval and reduced motion.
- [ ] **UI-C-008** Real mic and speech states independent; optional TTS/STT absent means normal text/UI/MCP; stale audio fenced.
- [ ] **UI-C-009** Authorized component renderer, capabilities and suggestions; Navigator does not replace Composer or invent a new event bus.
- [ ] **NAV-C-001 [SEC]** Optional Navigator semantic UI targets/tours/highlighter, scope-filtered observation, browser bridge, consent, safe actions and bounded plans.
- [ ] **NAV-C-002 [SEC]** Optional Playwright MCP uses isolated workload/browser, narrow allowlisted safe commands, consent and network/SSRF defenses; unavailable Playwright does not block live text tours.
- [ ] **NAV-C-003** Operational Navigator uses existing operational supervisor, not another scheduler. No interactive identity impersonation.

## 6. Optional Python speech/model workers and vector/KMS extensions

- [ ] **WRK-C-001** Common Python worker kit: uv locked, Pydantic versioned schemas, auth, health/ready, bounded retries, cancellation, resource metrics and no DB secrets.
- [ ] **WRK-C-002** Classification, NLLB, inference, transcription, real-time-dialog independently deployable, tested and truthful per model capability/language.
- [ ] **WRK-C-003** Kokoro independent Python image and named cache, English `af_heart` only if verified, first-start approved pinned download and offline restart.
- [ ] **WRK-C-004** MMS-TTS independent Python image and named cache, opted-in language/voice manifests, checkpoint-specific license verification and offline restart.
- [ ] **WRK-C-005** Partial downloads/checksum mismatch/volume loss/duplicate provisioners handled atomically, model weights not baked into release by default.
- [ ] **WRK-C-006** Live voice distinction: acoustic segment, final STT, accepted turn, model response, synthesized/delivered audio, barge-in/cancellation epoch.
- [ ] **VEC-C-001** `.AddQdrantVactor()` truly optional; subscribed to committed safe events, authorized projection, approved embedding, idempotent upsert, filtered search, retention/deletion; no EventRecord vector property.
- [ ] **KMS-C-001** `IKeyEncryptionProvider` default local protected and optional Infisical adapter; no direct arbitrary token-restoration endpoint or required enterprise vault.

## 7. Observability, metering, admin and website

- [ ] **OBS-C-001** OTel ActivitySource/Meter/logging with versioned GenAI semantic convention profile and no duplicate Aspire instrumentation.
- [ ] **OBS-C-002 [SEC]** Metadata-only default with no prompt, PII, vault mapping, raw audio, secrets or request IDs as metrics labels.
- [ ] **OBS-C-003** Durable unique attempt ledger with reported/estimated/unknown token and modality units; retries and late usage reconcile without double counting.
- [ ] **OBS-C-004** Decimal cost, price version/currency, cache/reasoning token math, local compute estimates labeled distinct from external charges.
- [ ] **OBS-C-005** Hard budget reservation/settlement, alerts and policy for missing/estimated cost; ledger independent of OTLP/tracing sampling.
- [ ] **OBS-C-006** Tenant-scoped operator Admin API and optional embeddable UI with billing/security/health/agent dashboards and safe exports/RBAC.
- [ ] **OBS-C-007** Optional standalone Aspire/AppHost/OTLP exporter, collector down fault tolerance and no mandatory external observability account.
- [ ] **SITE-C-001** Astro Starlight static website with docs navigation/search, versioned MDX and responsive branded accessible UI.
- [ ] **SITE-C-002** DocFX public .NET API reference and OpenAPI reference generated from verified source, code examples tested.
- [ ] **SITE-C-003** Optional OpenConnect-powered read-only docs assistant with source citations, version-scoped retrieval, rate limits and no private repo access.
- [ ] **SITE-C-004** Static docs remain fully usable when docs API, model provider or assistants are offline; no browser API keys.

## 8. CI, NuGet/npm, GHCR and documentation release

- [ ] **REL-C-001** CI protected branch jobs for .NET, TypeScript, Python, EF provider compatibility, NATS/outbox, privacy, client UI, docs/DocFX and security scans.
- [ ] **REL-C-002** Reproducible dependency locks, source links/XML docs, npm typings/ESM exports, Python locks and model licenses; source provenance ledgers.
- [ ] **REL-C-003** Tag preflight, maintainer approval, exact Git SHA, immutable pack artifacts, SBOM/checksum manifest and OIDC registry publishing.
- [ ] **REL-C-004** Clean consumer installs from NuGet, npm and GHCR by version/digest; API/component/worker smoke including optional-absent mode.
- [ ] **REL-C-005** Worker images non-root, slim, model manifests/license notices, scan+attest and independent lifecycle tests.
- [ ] **REL-C-006** Publish versioned docs only after tested source API generation; partial registry failures recorded/recovered honestly.

## 9. Evidence registry and exact test ownership

The following work packages and acceptance IDs are inherited from the uploaded specialist documents, **not replaced** by the canonical tasks above. A canonical `*-C-*` item is a cross-cutting gate; the corresponding `IMP-*`/`EF-IMP-*`/`NAV-IMP-*`/`CMP-IMP-*`/`OBS-IMP-*`/`SITE-IMP-*` covers the detailed implementation. A partial or experimental release may defer a module's stable acceptance suite but MUST mark that module unavailable/experimental and preserve all unchecked items in the roadmap.

### 9.1 Specialist implementation identifiers

| Area | Required implementation IDs | Test IDs |
|---|---|---|
| Base framework | `IMP-001–099` where assigned in source | `AT-001–042` |
| EF Core | `EF-IMP-001–016` | `EF-AT-001–030` |
| Navigator | `NAV-IMP-001–020` | `NAV-AT-001–024` |
| Composer | `CMP-IMP-001–014` | `CMP-AT-001–024` |
| Observability | `OBS-IMP-001–012` (canonical below; map to every OBS/REQ source requirement) | `OBS-AT-001–025` (canonical below) |
| Documentation site | `SITE-IMP-001–010` | `SITE-AT-001–015` |
| Optional semantic memory | `VEC-IMP-001–005` | `VEC-AT-001–006` |
| Optional Infisical | `KMS-IMP-001–004` | `KMS-AT-001–005` |

### 9.2 Observability tasks and acceptance gates

- [ ] **OBS-IMP-001 / OBS-AT-001** Native .NET instrumentation, versioned GenAI attrs, zero duplicate instrumentation with optional Aspire.
- [ ] **OBS-IMP-002 / OBS-AT-002** Privacy-safe metadata-only defaults, tenant audit and canary scan across logs/traces/metrics/exports.
- [ ] **OBS-IMP-003 / OBS-AT-003** Per-attempt durable ledger write independent of sampled traces and exporter uptime.
- [ ] **OBS-IMP-004 / OBS-AT-004** Pricing catalog/currency/decimal math, cached/reasoning overlap rules and immutable adjustments.
- [ ] **OBS-IMP-005 / OBS-AT-005** Streaming/abort/late usage, retries, finalization and reconciliation without duplicate charges.
- [ ] **OBS-IMP-006 / OBS-AT-006** Cost aggregate/report APIs, provenance completeness and authorized tenant drill-down.
- [ ] **OBS-IMP-007 / OBS-AT-007** Hard/soft budgets, atomic reservation, limits and alerts.
- [ ] **OBS-IMP-008 / OBS-AT-008** Firewall per-detector latency/decision/coverage metrics; no raw entity values or false quality claims.
- [ ] **OBS-IMP-009 / OBS-AT-009** Agent/operational lifecycle traces, workload/user identity distinction and worker telemetry.
- [ ] **OBS-IMP-010 / OBS-AT-010** Optional OTLP exporters/collector/Aspire standalone or AppHost; outage without firewall/task regression.
- [ ] **OBS-IMP-011 / OBS-AT-011** Embeddable Admin API/UI, secure security/model budget/audit dashboards and paging/export.
- [ ] **OBS-IMP-012 / OBS-AT-012** SLO, load/fault/cardinality test matrix, resource-cost estimation and deployment runbooks.

**Further OBS-AT-013–025 (separate tests, all NOT RUN):**
- [ ] **OBS-AT-013** Provider usage `Unknown` not equated to zero.
- [ ] **OBS-AT-014** Price-effective interval and multi-currency conversion provenance.
- [ ] **OBS-AT-015** High-concurrency hard-budget reservation rejects overspend.
- [ ] **OBS-AT-016** Models with image/audio/video units accounted without inventing values.
- [ ] **OBS-AT-017** Detector NotInstalled/SkippedByPolicy/TimedOut not conflated with Disabled.
- [ ] **OBS-AT-018** Shadow detector does not alter live verdict without policy.
- [ ] **OBS-AT-019** Tested model quality reports require reviewed labeled dataset.
- [ ] **OBS-AT-020** User and workload audit attribution never discloses bearer tokens.
- [ ] **OBS-AT-021** Admin RBAC and tenant export isolation deny cross-tenant reads.
- [ ] **OBS-AT-022** OTLP unavailable for tested duration, ledger/firewall remain safe.
- [ ] **OBS-AT-023** GenAI semantic convention versions pinned; double emission prevented.
- [ ] **OBS-AT-024** Retention by ledger/traces/logs/audit independent and secure delete.
- [ ] **OBS-AT-025** Clean package consumer no mandatory Aspire or OTLP exporter.

### 9.3 Documentation site tasks

- [ ] **SITE-IMP-001 / SITE-AT-001** Inspect actual OpenConnect API/projects and publish implemented/planned feature matrix.
- [ ] **SITE-IMP-002 / SITE-AT-002** Astro Starlight static build, custom accessible dark/light navigation/search.
- [ ] **SITE-IMP-003 / SITE-AT-003** Versioned MDX/docs taxonomy, architecture Mermaid and copyable C# examples.
- [ ] **SITE-IMP-004 / SITE-AT-004** DocFX API metadata generated from actual public assemblies, links to tutorials.
- [ ] **SITE-IMP-005 / SITE-AT-005** OpenAPI endpoints reference generated, schema compatibility checks.
- [ ] **SITE-IMP-006 / SITE-AT-006** Optional docs agent uses real OpenConnect public client/host, read-only/versioned docs retrieval and citations.
- [ ] **SITE-IMP-007 / SITE-AT-007** Restricted anonymous/user sessions, rate limiting/CORS/privacy/security.
- [ ] **SITE-IMP-008 / SITE-AT-008** OpenConnect MiniComposer dogfood, no separate chat engine or CSS takeover.
- [ ] **SITE-IMP-009 / SITE-AT-009** Static build and Pagefind search usable if docs API down.
- [ ] **SITE-IMP-010 / SITE-AT-010** GitHub Actions preview, docs linking/test examples, protected deployment.
- [ ] **SITE-AT-011** Docs assistant cannot access private repo/operational control/unsafe tools.
- [ ] **SITE-AT-012** Docs version isolation in retrieval and answers.
- [ ] **SITE-AT-013** Backend down no broken public static UI.
- [ ] **SITE-AT-014** No public provider keys or secret canaries in site assets.
- [ ] **SITE-AT-015** External user can follow verified installation guide with released packages.

### 9.4 Optional vector and Infisical adapters

- [ ] **VEC-IMP-001 / VEC-AT-001** Define scoped committed event subscription separate from `IEventJournal`, with explicit ACK handling.
- [ ] **VEC-IMP-002 / VEC-AT-002** `.AddQdrantVactor()` opt-in; absent registration makes no Qdrant client/consumer/agent dependency.
- [ ] **VEC-IMP-003 / VEC-AT-003** Approved `task.completed`/memory candidate summaries projected, sanitized and embedded; no raw prompt/PII/vector in journal.
- [ ] **VEC-IMP-004 / VEC-AT-004** Deterministic vector IDs, semantic model revision, tenant-filtered retrieval and deleted/revoked point protection.
- [ ] **VEC-IMP-005 / VEC-AT-005** Qdrant outage/replay/indexing failure independent of agent execution.
- [ ] **VEC-AT-006** Clean package consumer no Qdrant assembly when disabled.
- [ ] **KMS-IMP-001 / KMS-AT-001** `IKeyEncryptionProvider` registration, local protected persistent KEK and KMS-neutral vault envelope.
- [ ] **KMS-IMP-002 / KMS-AT-002** Optional Infisical machine identity+KMS wrapping only DEK, original PII not sent to Infisical.
- [ ] **KMS-IMP-003 / KMS-AT-003** KEK rotation/unwrap audit, KMS outage fail closed and cache policy.
- [ ] **KMS-IMP-004 / KMS-AT-004** No required Infisical in clean core/EF package graph; explicit use only.
- [ ] **KMS-AT-005** Restoration scope/tenant/principal/workload denial remains host-owned regardless KMS choice.

## 10. Release status and evidence ledger

**Status conventions:** `NOT RUN`, `BLOCKED`, `FAIL`, `PASS`—only `PASS` with evidence permits ticking. Supported core implementation can be `Experimental` where acceptance criteria remain unmet, but never silently drop specialist obligations.

| UTC date | Task ID / acceptance ID | Commit/PR | Environment/provider/model | Exact command | Result | Artifact/log/review link |
|---|---|---|---|---|---|---|
| — | — | — | — | — | NOT RUN | — |

**Release sign-off:**
- [ ] No known cross-tenant PII restoration, user/workload context mix-up, unintended model egress or denied tool execution in tested capabilities.
- [ ] Every advertised stable package or worker has passing tests, validated model licensing, externally verified clean consumer install and versioned docs.
- [ ] Conditional optional modules do not block core `AddOpenConnect`, basic interactive/MCP or text composer when omitted.
- [ ] EF provider claims match real provider concurrency tests; EF InMemory dev mode explicitly non-durable.
- [ ] CI required checks protect release branch/tag; NuGet/npm/GHCR attestations and checksums verified.
- [ ] Outstanding modules remain visible in unchecked master checklist, release notes and roadmap, not deleted or downgraded silently.

---

# Part II — Preserved original engineering checklist and generated acceptance inventories

The uploaded existing `checklist(2).md` is retained below without dropping work. Some old checklist clauses mention PostgreSQL-only as default, which are **superseded** by `requirements.md` §4; update their implementation/test provider matrix to SQL Server/PostgreSQL/SQLite plus InMemory test mode, without skipping crash or idempotency tests.


---

## Annex 1: Original consolidated project checklist

**Preserved source:** `checklist(2).md` · **SHA-256:** `5640b4551cdf0032d8cc0acfbfbe67e5c756b57947076a22906c794334e8b259` · **Content retained verbatim below.**

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




---

# Part III — Automatically enumerated source requirement and acceptance coverage ledger

Each identifier here was found in an uploaded document and is retained by verbatim source annex. The checkbox is a **verification checkpoint**, not evidence that a requirement has been implemented. Some source documents use identifiers for ranges, not explicit per-number definitions; a range is preserved as a range and must be decomposed by the implementing agent where applicable. Canonical conflict precedence applies.


## Source requirement index: Foundation framework (`spec(2).md`, 68 explicit identifiers)

- [ ] **OC-AGT-001** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-AGT-002** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-AGT-003** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-AGT-004** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-AGT-005** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-AGT-006** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-AGT-007** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-AGT-008** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-AGT-020** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-AGT-028** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-API-001** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-API-002** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-ARC-001** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-ARC-002** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-ARC-003** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-ARC-004** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-ARC-005** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-ARC-006** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-ARC-007** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-CAP-001** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-CAP-002** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-CAP-003** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-CAP-004** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-CAP-005** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-CAP-006** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-CFG-001** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-CFG-002** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-CFG-003** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-CFG-010** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-CFG-016** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-CLI-001** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-CLI-002** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-CLI-003** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-CLI-004** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-CLI-005** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-CLI-006** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-CLI-007** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-DOC-001** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-DOC-002** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EVT-001** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EVT-002** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EVT-003** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EVT-004** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EVT-005** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EVT-006** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EVT-007** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-OBS-001** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-OBS-002** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-PRV-001** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-PRV-002** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-PRV-003** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-PRV-004** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-PRV-005** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-PRV-006** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-PRV-007** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-PRV-008** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-PRV-009** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-PRV-010** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-PRV-011** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-PRV-012** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-PRV-013** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-PRV-014** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-PRV-015** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-REL-001** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-REL-002** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-REL-003** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-REL-004** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-REL-005** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.

## Source requirement index: EF Core (`entityframework-requirements(1).md`, 96 explicit identifiers)

- [ ] **OC-EF-001** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-002** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-003** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-004** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-005** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-006** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-007** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-008** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-009** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-010** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-011** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-012** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-013** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-014** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-015** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-016** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-017** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-018** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-019** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-020** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-021** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-022** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-023** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-024** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-025** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-026** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-027** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-028** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-029** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-030** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-031** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-032** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-033** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-034** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-035** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-036** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-037** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-038** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-039** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-040** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-041** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-042** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-043** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-044** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-045** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-046** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-047** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-048** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-049** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-050** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-051** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-052** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-053** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-054** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-055** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-056** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-057** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-058** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-059** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-060** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-061** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-062** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-063** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-064** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-065** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-066** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-067** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-068** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-069** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-070** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-071** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-072** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-073** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-074** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-075** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-076** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-077** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-078** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-079** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-080** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-081** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-082** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-083** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-084** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-085** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-086** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-087** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-088** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-089** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-090** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-091** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-092** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-093** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-094** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-095** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-EF-096** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.

## Source requirement index: Firewall (`firewall(1).md`, 56 explicit identifiers)

- [ ] **OC-FW-001** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-FW-002** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-FW-003** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-FW-004** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-FW-005** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-FW-006** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-FW-007** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-FW-008** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-FW-009** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-FW-010** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-FW-011** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-FW-012** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-FW-013** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-FW-014** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-FW-015** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-FW-016** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-FW-017** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-FW-018** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-FW-019** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-FW-020** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-FW-021** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-FW-022** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-FW-023** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-FW-024** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-FW-025** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-FW-026** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-FW-027** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-FW-028** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-FW-029** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-FW-030** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-FW-031** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-FW-032** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-FW-033** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-FW-034** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-FW-035** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-FW-036** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-FW-037** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-FW-038** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-FW-039** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-FW-040** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-FW-041** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-FW-042** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-FW-043** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-FW-044** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-FW-045** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-FW-046** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-FW-047** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-FW-048** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-FW-049** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-FW-050** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-FW-051** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-FW-052** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-FW-053** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-FW-054** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-FW-055** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-FW-064** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.

## Source requirement index: Composer (`composer-requirements(1).md`, 61 explicit identifiers)

- [ ] **ACR-AUD-001** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **ACR-AUD-002** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **ACR-AUD-003** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **ACR-AUD-004** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **ACR-AUD-005** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **ACR-AUD-006** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **ACR-AUD-007** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **ACR-AUD-008** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **ACR-AUD-009** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **ACR-AUD-010** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **ACR-LIFE-001** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **ACR-LIFE-002** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **ACR-LIFE-003** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **ACR-LIFE-004** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **ACR-LIFE-005** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **ACR-OPS-001** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **ACR-OPS-002** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **ACR-OPS-003** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **ACR-OPS-004** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **ACR-OPS-005** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **ACR-OPS-006** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **ACR-SDK-001** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **ACR-SDK-002** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **ACR-SDK-003** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **ACR-SDK-004** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **ACR-SDK-005** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **ACR-SDK-006** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **ACR-SDK-007** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **ACR-SDK-008** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **ACR-SDK-009** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **ACR-SDK-010** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **ACR-TXT-001** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **ACR-TXT-002** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **ACR-TXT-003** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **ACR-TXT-004** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **ACR-TXT-005** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **ACR-TXT-006** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **ACR-TXT-007** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **ACR-TXT-008** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **ACR-TXT-009** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **ACR-TXT-010** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **ACR-UX-001** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **ACR-UX-002** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **ACR-UX-003** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **ACR-UX-004** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **ACR-UX-005** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **ACR-UX-006** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **ACR-UX-007** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **ACR-UX-008** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **ACR-UX-009** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **ACR-UX-010** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **ACR-VIS-001** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **ACR-VIS-002** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **ACR-VIS-003** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **ACR-VIS-004** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **ACR-VIS-005** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **ACR-VIS-006** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **ACR-VIS-007** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **ACR-VIS-008** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **ACR-VIS-009** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **ACR-VIS-010** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.

## Source requirement index: Navigator (`navigator-requirements(3).md`, 84 explicit identifiers)

- [ ] **R-API-001** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-API-002** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-API-003** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-API-004** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-API-005** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-API-006** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-API-007** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-API-008** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-DEP-001** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-DEP-002** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-DEP-003** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-DEP-004** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-DEP-005** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-GOAL-001** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-GOAL-002** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-GOAL-003** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-GOAL-004** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-GOAL-005** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-GOAL-006** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-GOAL-007** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-GOAL-008** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-GOAL-009** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-GOAL-010** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-ID-001** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-ID-002** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-ID-003** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-ID-004** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-ID-005** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-ID-006** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-ID-007** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-INT-001** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-INT-002** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-INT-003** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-INT-004** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-INT-005** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-INT-006** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-INT-007** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-INT-008** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-MCP-001** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-MCP-002** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-MCP-003** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-MCP-004** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-MCP-005** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-OPS-001** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-OPS-002** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-OPS-003** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-OPS-004** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-OPS-005** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-OPS-006** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-OPS-007** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-OPS-008** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-REL-001** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-REL-002** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-REL-003** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-REL-004** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-REL-005** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-REL-006** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-REL-007** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-TTS-001** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-TTS-002** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-TTS-003** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-TTS-004** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-TTS-005** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-TTS-006** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-TTS-007** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-TTS-008** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-TTS-009** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-TTS-010** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-TTS-011** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-TTS-012** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-TTS-013** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-TTS-014** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-TTS-015** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-UI-001** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-UI-002** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-UI-003** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-UI-004** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-UI-005** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-UI-006** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-UI-007** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-UI-008** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-UI-009** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-UI-010** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **R-UI-011** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.

## Source requirement index: Observability (`observability-requirements(1).md`, 128 explicit identifiers)

- [ ] **NFR-001** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **NFR-002** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **NFR-003** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **NFR-004** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **NFR-005** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **NFR-006** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **NFR-007** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **NFR-008** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **NFR-009** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **NFR-010** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **NFR-011** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **NFR-012** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OBS-001** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OBS-002** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OBS-003** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OBS-004** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OBS-005** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OBS-006** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OBS-007** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OBS-008** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OBS-009** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OBS-010** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OBS-011** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OBS-012** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OBS-013** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OBS-014** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OBS-015** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OBS-016** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OBS-017** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OBS-018** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OBS-019** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OBS-020** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OBS-021** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OBS-022** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OBS-023** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OBS-024** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OBS-025** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-ADM-001** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-ADM-002** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-ADM-003** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-ADM-004** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-ADM-005** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-ADM-006** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-ADM-007** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-ADM-008** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-ADM-009** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-AG-001** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-AG-002** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-AG-003** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-AG-004** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-AG-005** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-AG-006** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-AG-007** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-ASP-001** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-ASP-002** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-ASP-003** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-ASP-004** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-ASP-005** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-ASP-006** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-ASP-007** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-CFG-001** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-CFG-002** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-CFG-003** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-CFG-004** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-CFG-005** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-CFG-006** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-CFG-007** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-FW-001** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-FW-002** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-FW-003** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-FW-004** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-FW-005** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-FW-006** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-FW-007** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-FW-008** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-FW-009** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-FW-010** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-FW-011** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-FW-012** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-FW-013** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-FW-014** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-GW-001** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-GW-002** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-GW-003** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-GW-004** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-GW-005** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-GW-006** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-MET-001** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-MET-002** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-MET-003** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-MET-004** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-MET-005** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-MET-006** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-MET-007** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-MET-008** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-MET-009** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-MET-010** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-MET-011** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-MET-012** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-MET-013** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-MET-014** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-MET-015** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-MET-016** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-MM-001** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-MM-002** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-MM-003** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-MM-004** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-MM-005** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-SEC-001** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-SEC-002** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-SEC-003** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-SEC-004** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-SEC-005** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-SEC-006** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-SEC-007** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-SEC-008** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-SEC-009** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-SEC-010** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-TEL-001** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-TEL-002** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-TEL-003** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-TEL-004** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-TEL-005** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-TEL-006** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-TEL-007** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-TEL-008** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-TEL-009** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **REQ-TEL-010** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.

## Source requirement index: Configuration (`configuration.md`, 4 explicit identifiers)

- [ ] **OC-AGT-020** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-AGT-028** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-CFG-010** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.
- [ ] **OC-FW-049** — Verify corresponding source requirement in preserved annex; attach implementation/test evidence.

**Generated explicit requirement checkpoints:** 497 (may include references to identifiers defined in another document).

## Complete specialized acceptance status register


### Core framework — 42 tests, all NOT RUN until evidenced

- [ ] **AT-001** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **AT-002** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **AT-003** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **AT-004** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **AT-005** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **AT-006** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **AT-007** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **AT-008** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **AT-009** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **AT-010** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **AT-011** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **AT-012** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **AT-013** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **AT-014** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **AT-015** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **AT-016** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **AT-017** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **AT-018** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **AT-019** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **AT-020** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **AT-021** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **AT-022** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **AT-023** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **AT-024** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **AT-025** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **AT-026** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **AT-027** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **AT-028** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **AT-029** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **AT-030** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **AT-031** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **AT-032** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **AT-033** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **AT-034** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **AT-035** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **AT-036** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **AT-037** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **AT-038** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **AT-039** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **AT-040** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **AT-041** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **AT-042** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.

### EF Core — 30 tests, all NOT RUN until evidenced

- [ ] **EF-AT-001** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **EF-AT-002** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **EF-AT-003** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **EF-AT-004** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **EF-AT-005** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **EF-AT-006** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **EF-AT-007** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **EF-AT-008** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **EF-AT-009** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **EF-AT-010** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **EF-AT-011** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **EF-AT-012** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **EF-AT-013** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **EF-AT-014** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **EF-AT-015** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **EF-AT-016** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **EF-AT-017** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **EF-AT-018** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **EF-AT-019** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **EF-AT-020** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **EF-AT-021** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **EF-AT-022** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **EF-AT-023** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **EF-AT-024** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **EF-AT-025** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **EF-AT-026** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **EF-AT-027** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **EF-AT-028** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **EF-AT-029** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **EF-AT-030** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.

### Navigator — 24 tests: 16 PASS (executed unit/integration evidence below), 8 NOT VERIFIED (explicitly out of the client+server slice or missing harness)

- [x] **NAV-AT-001** — PASS (unit). Existing core lead + MCP run without Navigator/TTS/Playwright registration. Evidence: At001CoreWithoutNavigatorTests (Core.Tests 17/17, Release, hermetic).
- [x] **NAV-AT-002** — PASS (unit). Navigator static text tour with bridge, no model workers/browser. Evidence: navigator acceptance.test.ts 9/9 + navigator.test.ts (packages/navigator 34/34, vitest).
- [x] **NAV-AT-003** — PASS (unit). Interactive navigator action cannot exceed authenticated user's grants. Evidence: GrantTests + digest/binding tests.
- [x] **NAV-AT-004** — PASS (unit + live single-host PG). Operational job uses isolated workload identity and background lease/recovery. Evidence: OperationalNavigatorHandlerTests, DurableStore replay/expiry/fence tests, PostgresIsolationTests + PostgresHandlerIntegrationTests incl live Testcontainers PG16 + DI-resolved cross-scope replay (PostgresHandlerReceiptTests/DI_resolved_handler_replays_completed_receipt_across_scopes) + in-process single-winner dedup (DurableHandlerReplayTests); cross-connection fencing + concurrent single-winner proven live (PostgresCrossProcessFencingTests, disposable PG16). Residual: cross-process multi-host, production DB; multi-host same-DB proven at connection level; OS-process separation remains untested.
- [x] **NAV-AT-005** — PASS (unit). Unregistered/ambiguous/stale target never causes a mutating click. Evidence: ApprovalFlowTests revision gating + target registry scoping.
- [x] **NAV-AT-006** — PASS (unit). Approval hash mismatch, expired grant, reconnect and revocation deny execution. Evidence: ApprovalFlowTests + grant validation tests.
- [x] **NAV-AT-007** — PASS (unit; no live browser). React SSR-safe import; host MiniComposer + Navigator provider coexist. Evidence: ssr.test.tsx, NavigatorProvider coexistence + acceptance tests (react 25/25).
- [x] **NAV-AT-008** — PASS (unit; live WS transport NOT VERIFIED). Only committed, safe Navigator events become visible on existing WS; duplicates deduped. Evidence: projector/hook/commit/relay/schema tests incl live PG relay tests; single-transaction event+outbox commit with rollback proof (live PG16) + commit-hook decorator DI (CommittingStoreRegistrationTests) + outbox relay adapter with ACK-after-broker-ACK (PostgresOutboxRelayAdapterTests/PostgresRelayTests, live) + relay-to-live-NATS-broker proof (PostgresRelayLiveBrokerTests, disposable infra).
- [x] **NAV-AT-009** — PASS (unit; live browser NOT VERIFIED). Optional Playwright absent: live tour still works; arbitrary MCP browser tools refused. Evidence: allowlist deny tests + isolated-browser origin/SSRF/profile tests.
- [x] **NAV-AT-010** — PASS (unit). Capture denied or pixel-redacted under sensitive/ambiguous field scenarios. Evidence: observation redaction tests + acceptance tests + CaptureGateTests.
- [ ] **NAV-AT-011** — NOT VERIFIED (out of slice). Kokoro-only first start provisions pinned `af_heart`, checks SHA, returns correct audio metadata. Kokoro/MMS workers, first-start provisioning, and GHCR images do not exist by design in the client+server slice.
- [ ] **NAV-AT-012** — NOT VERIFIED (out of slice). Kokoro restart with network denied uses persisted cache; no download attempted. Kokoro/MMS workers, first-start provisioning, and GHCR images do not exist by design in the client+server slice.
- [ ] **NAV-AT-013** — NOT VERIFIED (out of slice). MMS-only provisions exactly opted-in licensed language checkpoint; no full catalog fetch. Kokoro/MMS workers, first-start provisioning, and GHCR images do not exist by design in the client+server slice.
- [ ] **NAV-AT-014** — NOT VERIFIED (out of slice). MMS commercial/unapproved license scenario blocked; core text tours still work. Kokoro/MMS workers, first-start provisioning, and GHCR images do not exist by design in the client+server slice.
- [ ] **NAV-AT-015** — NOT VERIFIED (out of slice). Interrupted download cannot corrupt active validated cache; retry recovers. Kokoro/MMS workers, first-start provisioning, and GHCR images do not exist by design in the client+server slice.
- [ ] **NAV-AT-016** — NOT VERIFIED (out of slice). Concurrent initializer instances cannot expose partial model files. Kokoro/MMS workers, first-start provisioning, and GHCR images do not exist by design in the client+server slice.
- [x] **NAV-AT-017** — PASS (unit). Missing/failed both TTS workers: interactive, MCP, Navigator complete text-only. Evidence: SpeechRouterTests disabled/missing/unhealthy.
- [x] **NAV-AT-018** — PASS (unit). Speech stop/barge-in rejects stale chunks and maintains tour checkpoint. Evidence: speech epoch tests.
- [x] **NAV-AT-019** — PASS (unit). Client/worker options enum validation and capability handshakes reject invalid combinations. Evidence: SchemaValidationTests + options validators.
- [ ] **NAV-AT-020** — NOT VERIFIED (out of slice). End-to-end GHCR image install + persistent volume + NuGet/npm consumer smoke. No GHCR images published; no clean-consumer smoke run.
- [x] **NAV-AT-021** — PASS (unit, Navigator surfaces). Observability/log/NATS/capture artifacts contain no seeded sensitive canaries. Evidence: NavigatorTelemetryTests + redaction tests. Residual: full-artifact sweep across all surfaces unmapped.
- [ ] **NAV-AT-022** — NOT VERIFIED. Model provenance, artifact notices, code migration ledger and release security scan complete. Release-gate artifact (provenance/SBOM/ledger/security scan): no release run.
- [x] **NAV-AT-023** — PASS (unit; live browser NOT VERIFIED). Operational Playwright worker SSRF, profile reuse and tool-allowlist adversarial tests pass. Evidence: BrowserBoundaryTests + Playwright allowlist tests.
- [x] **NAV-AT-024** — PASS (unit). Event consumer optional QdrantVactor absent causes no Navigator failure or requirement. Evidence: subscriber-failure tolerance + DI absent-relay tests.

### Composer — 24 tests, all NOT RUN until evidenced

- [ ] **CMP-AT-001** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **CMP-AT-002** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **CMP-AT-003** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **CMP-AT-004** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **CMP-AT-005** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **CMP-AT-006** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **CMP-AT-007** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **CMP-AT-008** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **CMP-AT-009** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **CMP-AT-010** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **CMP-AT-011** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **CMP-AT-012** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **CMP-AT-013** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **CMP-AT-014** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **CMP-AT-015** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **CMP-AT-016** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **CMP-AT-017** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **CMP-AT-018** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **CMP-AT-019** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **CMP-AT-020** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **CMP-AT-021** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **CMP-AT-022** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **CMP-AT-023** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.
- [ ] **CMP-AT-024** — NOT RUN. Ground test in its preserved specification/requirements; add CI evidence and real capability status.

**Additional extension gates:** OBS-AT-001–025, SITE-AT-001–015, VEC-AT-001–006 and KMS-AT-001–005 are already specified in the canonical checklist above and remain NOT RUN.
