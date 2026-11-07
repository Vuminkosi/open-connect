# OpenConnect Core Requirements

**Version:** 1.0-draft | **Date:** 2026-11-06
**Source Documents:** docs/plans/spec.md, docs/plans/firewall.md, docs/plans/configuration.md
**Traceability:** OC-* requirement identifiers map to source documents

## 1. Core Framework Requirements

### 1.1 Initialization and DI Registration

**REQ-001:** `services.AddOpenConnect()` MUST register a default lead agent with privacy firewall enabled by default.

**REQ-002:** Privacy firewall MUST be enabled by default. Explicit `Privacy.Enabled = false` MUST emit a security warning/metric and MUST NOT disable authentication, authorization, tenant isolation, tool approvals, schema validation, or audit.

**REQ-003:** The registration MUST be idempotent - calling `AddOpenConnect()` multiple times MUST NOT duplicate registrations.

**REQ-004:** Missing model/transport/persistence configuration MUST report an actionable error at startup, not silently fail.

**REQ-005:** `.AddOpenConnect()` MUST NOT automatically launch Docker containers or download models.

**Acceptance Criteria:**
- GIVEN a minimal ASP.NET host
- WHEN `AddOpenConnect()` is called with defaults
- THEN lead agent is registered
- AND privacy firewall is enabled
- AND no external resources are launched

### 1.2 Strongly Typed Configuration

**REQ-010:** All closed configuration choices MUST be represented by public C# enums with stable kebab-case wire names.

**REQ-011:** JSON/YAML/TypeScript/Pydantic schemas MUST use the same wire names with parity validation.

**REQ-012:** Unknown enum values and numeric payloads MUST be rejected at startup validation.

**REQ-013:** Configuration MUST use `IValidateOptions<T>` and `ValidateOnStart()` to catch invalid combinations before the application runs.

**REQ-014:** Invalid configurations MUST fail with clear error messages indicating the exact conflict.

**Wire Format Examples:**
- `AgentExecutionMode.Interactive` -> `"interactive"`
- `FirewallMode.StrictLocal` -> `"strict-local"`
- `SecurityDetectorKind.GlinerPii` -> `"gliner-pii"`

### 1.3 Options Model

**REQ-020:** `OpenConnectOptions` MUST be strongly typed with nested options for Transport, Runtime, Agents, and Privacy.

**REQ-021:** `PrivacyOptions` MUST include:
- `Enabled` (bool, default true)
- `Mode` (FirewallMode enum, default StrictLocal)
- `OnFailure` (FirewallFailureAction enum, default Deny)
- `Detectors` (SecurityDetectorOptions)
- `Policies` (SecurityPolicyOptions)

**REQ-022:** `SecurityDetectorOptions` MUST allow independent enable/disable of each detector:
- Rules (default ON)
- GLiNER PII (default OFF)
- DeBERTa Injection (default OFF)
- Qwen3Guard (default OFF)
- PaddleOCR (default OFF)
- YuNet (default OFF)

**REQ-023:** Disabled detectors MUST NOT download models, load modules, initialize GPU contexts, reserve cache, or advertise capabilities.

---

## 2. Agent Runtime Requirements

### 2.1 Agent Registration

**REQ-030:** A default `lead` agent MUST be registered automatically.

**REQ-031:** Additional agents MUST be registered with unique ID, version, instructions path, model capability requirements, tool allowlist, delegation depth limit, budget, and timeout.

**REQ-032:** Agent definitions MUST validate cycles, duplicate IDs, unknown capability references, and oversized budgets at startup.

### 2.2 Runtime Instructions

**REQ-040:** `agent-runtime.md` MUST be loaded from configured content root with path traversal prevention.

**REQ-041:** Markdown instructions MUST NOT have authority to grant tools, disable privacy, change tenant, or modify policy.

**REQ-042:** Instruction files MUST be validated for size limit, UTF-8 encoding, and hash revision.

### 2.3 Execution Modes

**REQ-050 (Interactive):** Interactive agents MUST execute under the authenticated active user's derived immutable auth snapshot.

**REQ-051 (Interactive):** Raw bearer tokens/cookies MUST NOT be transmitted to models, NATS, or workers.

**REQ-052 (Interactive):** Delegation MUST use equal-or-narrower tool grants; elevation MUST be impossible.

**REQ-053 (Interactive):** Reauthorization MUST occur before EVERY read/write tool invocation.

**REQ-054 (Operational):** Operational agents MUST execute under a dedicated constrained workload identity, NOT user context.

**REQ-055 (Operational):** Operational agents MUST be managed by `OpenConnectOperationalAgentSupervisor : BackgroundService`.

**REQ-056 (Operational):** Operational runs MUST NOT access `HttpContext` or inherit user cookies.

**REQ-057 (Operational):** Operational runs MUST use durable lease/fencing, transactional outbox, and NATS work events.

---

## 3. Privacy Firewall Requirements

### 3.1 Architecture

**REQ-100:** Privacy firewall MUST be enforced by the .NET host; Python security worker provides detection signals only.

**REQ-101:** Security worker MUST NOT have vault keys, database credentials, or authorization capability.

**REQ-102:** Raw content MUST NEVER be in NATS messages; authenticated point-to-point transport for scanning.

**REQ-103:** Missing required detector MUST block strict-mode external egress with `privacy_blocked` error.

### 3.2 Detection Pipeline

**REQ-110:** Entity categories MUST include at minimum: PERSON, EMAIL, PHONE, ADDRESS, DATE_OF_BIRTH, GOVERNMENT_ID, ACCOUNT_NUMBER, PAYMENT_CARD, IP_ADDRESS, SECRET, ORG_SENSITIVE, CUSTOM.

**REQ-111:** Each detector MUST report: entityType, start/end Unicode-safe offsets, detector ID/version, confidence, and policy decision candidate.

**REQ-112:** Unicode scalar offsets MUST be used with golden fixtures for emoji, combining accents, multilingual text.

**REQ-113:** Overlapping spans MUST resolve deterministically based on policy severity and validated identifier type.

### 3.3 Token Vault

**REQ-120:** Placeholders MUST be cryptographically random, unpredictable, and collision-resistant.

**REQ-121:** Original values MUST be stored in encrypted vault (AES-256-GCM minimum) with per-record random nonce.

**REQ-122:** Mappings MUST be bound to tenant + principal + session + turn + entity classification + policy revision.

**REQ-123:** Cross-tenant, cross-session, and cross-turn token replay MUST be denied.

**REQ-124:** Key rotation and TTL expiry MUST be supported.

### 3.4 Egress Gates

**REQ-130:** All MAF model invocations MUST pass through single enforceable model egress adapter.

**REQ-131:** Tool calls, agent handoffs, context providers, and retrieval MUST use equivalent gates.

**REQ-132:** Tool restoration MUST be schema-aware, authorized, and only for exact field-path allowlist.

**REQ-133:** Streaming restoration MUST handle split tokens, UTF-8 boundaries, and reconnect scenarios without partial leakage.

### 3.5 Fail-Closed Behavior

**REQ-140:** Missing detector in strict mode MUST block external egress (NOT silent pass-through).

**REQ-141:** Unready detector MUST block flows requiring its coverage.

**REQ-142:** No silent fallback from GLiNER to rules-only when policy requires semantic PII.

**REQ-143:** Required coverage missing MUST return `privacy_blocked` or use verified local-only processing with explicit policy.

---

## 4. Tool Security Requirements

### 4.1 Discovery

**REQ-200:** Tools MUST be discovered from registered .NET adapters/MCP connectors with explicit JSON Schema.

**REQ-201:** Tool descriptors MUST include: input/output schemas, description, side-effect level, owner, scopes, and approval policy.

**REQ-202:** Discovery MUST filter by: agent allowlist ∩ principal scopes ∩ tenant policy ∩ task context.

### 4.2 Authorization

**REQ-210:** Tool calls MUST be validated with typed args, reauthorized server-side, privacy-checked, and idempotency/approval verified before execution.

**REQ-211:** Approval receipts MUST be bound to exact normalized args hash, principal, tenant, session, and expiry.

**REQ-212:** Untrusted instructions and tool results MUST NOT modify tool permissions or system policy.

**REQ-213:** Failed authorization MUST return stable error codes: `tool_not_allowed`, `approval_required`, `capability_unavailable`, `privacy_blocked`.

---

## 5. Session and Event Requirements

### 5.1 Durable Sessions

**REQ-300:** Sessions MUST be persisted in PostgreSQL with optimistic concurrency (version columns).

**REQ-301:** Session queries MUST enforce tenant_id filtering with proper indexes.

**REQ-302:** Accepted turns MUST be persisted atomically with events and outbox.

### 5.2 Event Journal

**REQ-310:** Events MUST be persisted with per-session monotonic sequence (not broker sequence).

**REQ-311:** Event ID MUST be used for deduplication on replay.

**REQ-312:** Event payloads MUST be sanitized (no raw sensitive data).

**REQ-313:** Cursor expiry and resnapshot MUST be supported for replay.

### 5.3 Outbox Pattern

**REQ-320:** Domain events MUST be committed with task outcome in single transaction.

**REQ-321:** JetStream publish MUST use `Nats-Msg-Id` for dedup.

**REQ-322:** ACK MUST only occur after broker confirmation AND durable commit.

**REQ-323:** Recovery from broker/host restart MUST NOT produce duplicate committed turns.

---

## 6. Gateway Requirements

### 6.1 HTTP API

**REQ-400:** API version prefix MUST be `/openconnect/v1`.

**REQ-401:** Task creation MUST require idempotency key (`clientTaskId`).

**REQ-402:** Duplicate identical request with same clientTaskId MUST return original task.

**REQ-403:** Same clientTaskId with different payload MUST return 409 Conflict.

**REQ-404:** Client-supplied tenant/owner MUST be ignored; server MUST derive from auth context.

**REQ-405:** Rate limits, request size limits, and authentication MUST be enforced.

### 6.2 WebSocket Events

**REQ-410:** WebSocket MUST be authenticated with session-scoped subscriptions.

**REQ-411:** Origin check MUST be enforced.

**REQ-412:** Bearer tokens MUST NOT be in URL logs; use same-origin cookies or short-lived tickets.

**REQ-413:** Resume cursor MUST be supported for reconnection.

**REQ-414:** Heartbeat and bounded send queues MUST be implemented.

### 6.3 NATS Transport

**REQ-420:** Subjects MUST follow pattern: `oc.v1.task.accepted`, `oc.v1.worker.<kind>.request`, etc.

**REQ-421:** Workers MUST NOT be able to create streams or provision infrastructure.

**REQ-422:** Stream provisioning MUST use least-privilege admin account.

**REQ-423:** Subject allowlists MUST prevent wildcard privilege leakage.

---

## 7. Worker Requirements

### 7.1 Common Platform

**REQ-500:** All workers MUST be Python 3.12+ with `uv.lock` and Pydantic schema parity.

**REQ-501:** Workers MUST NOT have database credentials.

**REQ-502:** Workers MUST use authenticated NATS consumers.

**REQ-503:** Health MUST be process liveness; readiness MUST be model loaded + dependencies OK.

**REQ-504:** Model manifest MUST include checkpoint, revision, checksum, license, and runtime resources.

### 7.2 Worker Protocol

**REQ-510:** Worker MUST claim task atomically before processing.

**REQ-511:** Payload MUST be fetched through authorized host reference (not in NATS message).

**REQ-512:** Lease MUST be renewed with heartbeat during execution.

**REQ-513:** Completion MUST commit through host endpoint; NATS ACK only after durable commit.

**REQ-514:** Expired lease MUST NOT be able to commit.

### 7.3 Security Worker

**REQ-520:** Security worker MUST use authenticated private HTTP API (not NATS) for scan requests.

**REQ-521:** Security worker MUST have NO vault access, NO database access, NO authorization capability.

**REQ-522:** Missing required model MUST result in unready status, blocking strict-mode egress.

---

## 8. Client Requirements

### 8.1 .NET Client

**REQ-600:** `AddOpenConnectClient()` MUST configure HTTPS base address and WSS endpoint.

**REQ-601:** Auth handler, reconnect/backoff, and event replay MUST be supported.

**REQ-602:** `CancellationToken` MUST be honored for all operations.

**REQ-603:** Typed errors with stable codes MUST be returned.

### 8.2 Blazor Components

**REQ-610:** `<OpenConnectProvider />` MUST coordinate client state, session, component catalogue, and event listeners.

**REQ-611:** `<MiniComposer />` MUST be a MudBlazor FAB at bottom-right with responsive safe-area placement.

**REQ-612:** MiniComposer MUST support expand/collapse, keyboard, focus, ARIA, and high contrast.

**REQ-613:** Component proposals MUST be validated by schema, principal entitlement, and server reauthorization.

**REQ-614:** No arbitrary model-generated Razor/HTML/JS MUST be executed.

### 8.3 TypeScript Client

**REQ-620:** `@openconnect/client` MUST be ESM with TypeScript declarations.

**REQ-621:** Browser fetch/WebSocket and Node adapters MUST be supported.

**REQ-622:** AbortSignal, reconnect/resume, and typed errors MUST match C# client API.

**REQ-623:** No React dependency in core SDK.

---

## 9. Observability Requirements

**REQ-700:** OpenTelemetry traces MUST correlate HTTP -> session -> MAF agent -> tool/worker -> NATS -> client.

**REQ-701:** Metrics MUST include: task throughput, event lag, worker status, model latency, privacy blocks, tool denials.

**REQ-702:** No prompt text or raw PII in log fields, traces, or metric labels.

**REQ-703:** Health checks MUST expose: `/health/live` for process liveness, `/health/ready` for dependencies ready.

---

## 10. Release Requirements

**REQ-800:** All NuGet packages MUST share aligned version.

**REQ-801:** npm package MUST share version with NuGet.

**REQ-802:** Docker images MUST be tagged with version and immutable digest.

**REQ-803:** OIDC trusted publishing MUST be used (no long-lived API keys).

**REQ-804:** SBOM and signed provenance MUST be published with release artifacts.

**REQ-805:** Clean consumer tests MUST verify package installation from registry.

---

## Acceptance Test Traceability

| Requirement Block | Acceptance Tests |
|-------------------|------------------|
| Core Framework | AT-001, AT-002, AT-031, AT-032 |
| Agent Runtime | AT-003, AT-004, AT-033, AT-034 |
| Privacy Firewall | AT-008, AT-021-030, AT-039, AT-040 |
| Tool Security | AT-004 |
| Sessions & Events | AT-005, AT-006, AT-007, AT-016 |
| Gateway | AT-016 |
| Workers | AT-009, AT-010, AT-011, AT-012 |
| Clients | AT-014, AT-015, AT-017 |
| Release | AT-019, AT-020, AT-042 |
