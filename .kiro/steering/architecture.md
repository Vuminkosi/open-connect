---
inclusion: auto
name: openconnect-architecture
description: .NET solution architecture, package boundaries, and system design for OpenConnect
---

# OpenConnect Architecture Steering

Documentation LiveUI section targets derive from the same heading identifiers used by the rendered page and its table of contents. They allow read-only inspection, highlighting and scrolling on public documentation routes; Cloud management remains user-operated.

The API reference also exposes its REST and worker contracts heading as a read-only LiveUI target, so guided navigation can distinguish that section from the generated .NET reference.

The generated API index filters by document name and renders a bounded page of metadata at a time; generated reference volume must not require rendering the entire catalog in one response.

Documentation for document processing and semantic memory describes SDK client contracts, host authorization and scoped data handling; worker readiness or an indexing receipt is not presented as proof of durable, authorized retrieval.

The knowledge SDK's index and search clients require host authorization of the tenant, object, destination and roles, sign worker requests, and screen input and output through the host privacy gate. The composed workflow waits for an indexing completion before searching.

The optional development documentation knowledge example indexes an operator-approved synthetic object separately for each signed visitor, assigns that visitor's principal ACL and a one-hour expiry, and requires completed indexing before search. Index cleanup requires the independently provisioned expiry worker. Cloud embedding credentials, when selected, belong to the site deployment rather than the visitor.

The example reuses its visitor-bound job identity on retry, does not reindex after confirmed completion, and rejects results whose document ID or source revision differs from the approved visitor projection.

Knowledge embeddings can use either the private signed worker contract or the authenticated Cloud gateway contract. The SDK validates the response identity and model space and applies host privacy checks to both paths.

Cloud worker payload binding and tenant-ID conversion are shared gateway boundary operations; knowledge embedding payload preparation does not depend on analytics feature code.

Knowledge embedding execution composes reservation, privacy inspection, a deployment-bound claim, revocation monitoring, worker dispatch, output inspection and settlement before returning a result. Ambiguous outcomes fail closed and are not retried by the gateway execution layer.

The Cloud embedding route is opt-in and reports readiness only when its configured private control plane and worker are ready. The local server retains its existing voice argument position when additional capabilities are configured.

The document SDK composes separately authorized OCR and structured extraction clients, validates worker responses and maps citations to OCR pages. Its worker execution depends on explicitly provisioned services and does not by itself provide a public site workflow.

The optional documentation document example is a development-only SDK consumer. It binds operations to the current signed visitor, limits concurrent uploads before buffering and bounds upload time, requires a protected document socket and explicitly configured extraction transport, and releases upload buffers after processing. LiveUI can activate analysis only after a visitor selects a file; it cannot select the file.

A disposable local wire fixture exercises the documentation document workflow over a Unix socket and HTTP, including signed requests, tenant binding and citation handling. It does not establish scanner, model or real-worker readiness.

The API reference guide links authored client guides and versioned wire contracts, and documents the repository-pinned DocFX generation workflow separately from published-package validation.

The documentation image build context includes the LiveUI source projects and compiler referenced by the site, so a clean container build can compose the interactive documentation client.

The documentation application-host guide identifies the combined local Compose overlays and the default workers needed to run the public assistant; Cloud identity and optional transcription remain separately configured.

Privacy documentation distinguishes the token vault's tenant, principal, session and purpose scope from any additional turn or policy-revision restrictions imposed by the host.

The public privacy example runs fixed synthetic inputs through an isolated SDK rules firewall without a token vault or external egress. It does not accept visitor-entered sensitive data or imply that detection is complete. The documentation LiveUI policy may select only the predefined examples and trigger this idempotent example only on the privacy guide.

The LiveUI guide describes the site's actual permitted actions and requires inspection of the page and receipt when an interaction is denied or uncertain.

Composer voice controls use native buttons for keyboard operation and expose their toggle state. A transcription failure turns the microphone toggle off and reports the failure while preserving text input.

An opt-in local browser check exercises that failure against an intentionally unavailable transcription worker and verifies a subsequent text-driven interaction; it is not a substitute for a successful transcription test.

The transcription worker exposes authenticated local-realtime model readiness separately from its general readiness. It checks the local-only model path without cloud fallback and does not reserve a capture slot.

The development documentation application host exposes only an authenticated, bounded boolean readiness result for its configured local transcription worker; the worker credential remains on the host and worker errors fail closed.

When the local documentation-host readiness integration is enabled, the site checks it with its server-side visitor identity and disables microphone controls until the local model is available. Other SDK clients retain their existing behavior unless their host supplies an availability state.

If microphone availability is withdrawn during capture, the composer aborts the socket without an end-of-stream request and invalidates the active speech epoch.

The documentation assistant renders after its interactive server circuit starts, so its launcher is not presented before it can receive input. Public documentation remains server-rendered.

Interactive documentation examples likewise render their controls after their circuits start, preventing early input from being lost while the surrounding guide remains server-rendered.

The local documentation acceptance suite can exercise the assistant's LiveUI interaction with the synthetic privacy example and verify that the rules firewall reports the expected category.

The privacy demonstration uses a fixed set of synthetic email, phone, test-card, password and ordinary messages; LiveUI may select only those values. Changing the example clears the prior result.

The opt-in documentation speech acceptance check observes non-silent browser PCM playback through the configured application host and synthesis worker; it does not establish physical-device audio or transcription readiness.

The opt-in local Cloud voice journey issues and revokes a scoped test credential and verifies synthesis, usage and privacy records for a real capability request. It creates test ledger and audit entries and must use an appropriate test tenant.

When generated API documents are present, LiveUI may inspect, focus, or set the public API search field with a bounded value. It does not advertise that field when the catalog is absent.

When the local documentation assistant and local Cloud identity are composed together, the site uses a combined CA bundle that trusts both loopback HTTPS services. The identity and assistant remain separate services with their own credentials and authorization boundaries.

The opt-in local Cloud management browser check compares tenant-scoped ledger state around public documentation and authenticated management visits. It requires an isolated test tenant so concurrent capability calls do not affect the comparison.

The service catalog and Cloud guides distinguish management access from admitted capability execution and distinguish quota ledger records from invoices.

The Todo reference sample persists its scoped Todo items and OpenConnect state in its application-owned SQLite database; deployment must retain that database if items should survive replacement.

The documentation component showcase uses the SDK component registry and safe renderer. Its single idempotent display action is authorized only on the public documentation index; arbitrary component names and Cloud management controls are not exposed to that action.

Realtime transcription is application-neutral: only provider-confirmed final text may be submitted as a task, and the host does not rewrite it using sample-specific vocabulary. A capture and speech epoch use the host dispatcher's scoped idempotency mechanism. Provider errors and cancellation do not admit an unfinished turn.

## Solution Architecture

```
Developer Product (Blazor / .NET / HTTP Client)
  |- OpenConnect.Client / OpenConnect.Components.Blazor
  |    |- OpenConnectProvider, MiniComposer, IOpenConnectComponent
  |    |- HTTP(S) task management, WebSocket event playback
  v
ASP.NET Core Host: AddOpenConnect(), MapOpenConnect()
  |- AuthN/AuthZ + principal/tenant scope [always on]
  |- Task admission, durable session + outbox (PostgreSQL)
  |- LeadAgent -> Microsoft Agent Framework agents/workflows/subagents
  |- Policy-bound tool broker and component proposal admission
  |- Privacy firewall at model, tool, worker, output boundaries [default on]
  |- Capability registry, worker routing, observability
  |- NATS JetStream publisher/consumers
  v
NATS JetStream (internal event backbone; never browser-exposed)
  |- Durable task/work events (metadata; no raw sensitive prompts)
  |- Worker claim and commit control plane
  v
Python Worker Docker Images (opt-in, separately deployed)
  |- security | classification | mms-tts | nllb
  |- inference | transcription | realtime-voice
  v
Model Endpoints or Local Pinned Model Assets
```

## .NET Package Structure

### Core Packages

| Package | Purpose | Dependencies |
|---------|---------|--------------|
| `OpenConnect.Abstractions` | IDs, DTOs, interfaces, errors | None (no ASP.NET, Blazor, NATS, PostgreSQL, cloud SDK) |
| `OpenConnect.Core` | Core implementation, options, DI | Abstractions |
| `OpenConnect.Agents` | Agent registry, MAF adapters | Core, Abstractions |
| `OpenConnect.Privacy` | Firewall, policy engine, egress gates | Core, Abstractions |
| `OpenConnect.Persistence.EFCore` | Provider-neutral session, task, event and outbox persistence | Abstractions, EF Core |
| `OpenConnect.Tools` | Tool broker, discovery, authorization | Core, Abstractions |

### Infrastructure Packages

| Package | Purpose | Dependencies |
|---------|---------|--------------|
| `OpenConnect.Transport.Nats` | JetStream publisher, consumer patterns | Core, Workers.Contracts |
| `OpenConnect.Persistence.EFCore` | EF mappings and provider-neutral stores | Abstractions, EF Core |
| `OpenConnect.Hosting.AspNetCore` | HTTP/WS endpoints, auth integration | All core packages |
| `OpenConnect.Workers.Contracts` | Worker protocol, capability descriptors | Abstractions |

### Client Packages

| Package | Purpose | Dependencies |
|---------|---------|--------------|
| `OpenConnect.Client` | .NET task client, event subscription | Abstractions |
| `OpenConnect.Components.Contracts` | IOpenConnectComponent descriptor | Abstractions |
| `OpenConnect.Components.Blazor` | OpenConnectProvider, MiniComposer | Client, Components.Contracts |
| `OpenConnect.Observability` | OpenTelemetry integration | Core |
| `OpenConnect.Voice` | Voice state machine, audio processing (optional) | Core |

## TypeScript Package

| Package | Purpose |
|---------|---------|
| `@openconnect/client` | Browser/Node SDK, fetch/WebSocket adapters, ESM + types |

## Dependency Rules

```
Abstractions (no external framework dependencies)
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
```

### Critical Rules

- `Components.Contracts` MUST NOT reference Blazor
- Core MUST NOT reference MudBlazor
- Abstractions MUST NOT reference ASP.NET, Blazor, NATS, PostgreSQL, EF, or cloud SDKs
- No circular project references
- Align package versions for single framework release

## Microsoft Agent Framework Integration

**OC-ARC-002:** MAF adapters MUST use supported Microsoft Agent Framework APIs, pinned and verified by compilation smoke test. Do not invent unsupported MAF APIs.

**Key Integration Points:**
- `IChatClient` integration for model providers
- Agent/workflow lifecycle events
- Tool execution patterns
- Delegation mechanisms

**Constraint:** OpenConnect must not implement a duplicate LLM agent orchestrator.

## Gateway and Communication Protocols

## Cloud portal boundary (OC-CLOUD-001)

- The existing .NET 10 Blazor portal remains the public documentation host. Public documentation, search, API reference, health checks, and the optional assistant do not depend on cloud identity or control-plane availability.
- Cloud dashboard routes are additive and require authenticated server-side authorization. Interactive navigation state is not an authorization boundary.
- Cloud identity uses a configurable external OIDC provider with authorization-code flow and PKCE, backed by a server-side secure cookie. Cloud identity is disabled unless explicitly configured; enabling it with incomplete provider configuration fails application startup.
- Tenant membership and application enrollment are persisted in the separate `cloud` PostgreSQL schema when the optional Cloud connection is configured. The authenticated control-plane API is tenant-membership scoped; application creation is limited to owners and administrators. Entitlements, credentials, complete audit coverage, cloud data-plane routing, and acceptance evidence remain separate unfinished work and must not be reported as operational.
- Logical capability definitions, plan grants, tenant selections, application selections, and worker deployments are distinct records. A service selection never by itself authorizes execution; the future Cloud gateway must recheck entitlement, policy, credential scope, quota, region, and deployment readiness before dispatch.
- Cloud machine credentials are random, one-time-displayed server credentials. Persist only a one-way digest and non-secret fingerprint; bind each key to a tenant/application/environment and explicit service scopes. Credential validation identifies a caller but does not replace gateway capability admission.
- Cloud dashboard content must state when those control-plane services are unavailable; it must not show fabricated tenant state or enforcement evidence.

### HTTP API

Version prefix: `/openconnect/v1`

| Method | Route | Purpose |
|--------|-------|---------|
| `POST` | `/tasks` | Create accepted task (idempotency key required) |
| `GET` | `/tasks/{taskId}` | Read authorized state/result |
| `POST` | `/tasks/{taskId}/cancel` | Request cancellation |
| `GET` | `/sessions/{sessionId}` | Read authorized session projection |
| `POST` | `/sessions` | Create session |
| `GET` | `/sessions/{sessionId}/events?after={sequence}` | Bounded replay |
| `GET` | `/capabilities` | User-visible capability projection |
| `GET` | `/components` | Permission-filtered UI descriptors |
| `GET` | `/health/ready` | Authenticated readiness |
| `WS` | `/events` | Authenticated event stream with resume cursor |

### WebSocket Protocol

- Authenticated subscription with session scoping
- Origin check and ticket-based auth (no bearer tokens in URL)
- Bounded send queues with backpressure
- Resume cursor for reconnection
- Heartbeat and cancellation support

### NATS JetStream

**Subjects:** `oc.v1.task.accepted`, `oc.v1.worker.<kind>.request`, `oc.v1.worker.<kind>.status`, `oc.v1.execution.event`

**Constraints:**
- Stream provisioning via least-privilege admin account
- Workers cannot create streams
- Subject allowlists prevent wildcard privilege leakage
- Raw sensitive payloads never in NATS messages

## Worker Architecture

### Python Workers

All workers are Python 3.12+ with:
- `uv.lock` for reproducible builds
- Pydantic schema parity with C#
- Non-root Docker builds
- Pinned base digests
- Health vs readiness distinction
- Model manifest with checksums

### Worker Types

| Worker | Purpose | Required Capability |
|--------|---------|---------------------|
| `security` | Local privacy detection | Required for strict-mode egress |
| `classification` | Content classification | Optional |
| `mms-tts` | Multilingual TTS | Optional |
| `nllb` | Translation | Optional |
| `inference` | Self-hosted LLM | Optional |
| `transcription` | STT for recorded audio | Optional |
| `realtime-voice` | Live voice dialog | Optional |

### Worker Protocol

1. Worker claims task via authenticated NATS consumer
2. Fetches payload through authorized host reference
3. Executes with lease/heartbeat
4. Commits result through host `complete/fail` endpoint
5. Host ACK only after durable commit

## Session and State Management

### Session Lifecycle

- Per-session monotonic sequence for event ordering
- PostgreSQL persistence with expected-version CAS
- Cursor expiry and resnapshot for replay
- Outbox pattern for NATS resilience

### Event Journal

Events are persisted atomically with:
- Session sequence (monotonic per session)
- Event ID (dedup)
- Sanitized payload
- Correlation ID for tracing

### Outbox Pattern

- Domain events committed with task outcome
- JetStream publish with `Nats-Msg-Id` dedup
- ACK only after broker confirmation
- Recovery from broker/host restart without duplicate committed turns

## Capability Registry

A `CapabilityDescriptor` contains:
- `id`, `kind`, `version`
- `features`, `modalities`, `languages`
- `streaming` support
- `inputSchema`, `outputSchema`
- `resourceRequirements`
- `privacyClass`, `providerId`
- `health` status
- `limits`, `lastSeen`, `contractVersion`

**Rule:** Advertise *actual* capabilities only. Worker announces only installed/model-verified capabilities.

Hosts can opt into a private-worker capability monitor with explicitly owned capability IDs. The monitor publishes validated ready capabilities and marks its IDs stale on failed, invalid or incomplete worker responses. Its polling interval must be shorter than the registry lease, allowing for all configured sources.

The optional vision worker exposes image classification and paired image/text embedding through separate private endpoints. It requires approved, verified offline model artifacts, a restricted runtime and local malware scanning for image input before advertising readiness. Its process-local execution does not provide durable replay protection.

## Configuration Contract

```csharp
builder.Services.AddOpenConnect(options =>
{
    options.Privacy.Enabled = true; // default
    options.Runtime.InstructionsPath = "agent-runtime.md";
    options.Transport.Mode = OpenConnectTransportMode.JetStream;
});
builder.Services.AddOpenConnectPostgres(builder.Configuration.GetConnectionString("OpenConnect")!);
builder.Services.AddOpenConnectNats(builder.Configuration["Nats:Url"]!);

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapOpenConnect();
app.Run();
```

**Constraint:** `.AddOpenConnect()` registers services but NEVER launches Docker or downloads models.

### Shared Cloud JSON privacy inspection

Cloud features use a shared JSON privacy inspection service for decoded worker-bound payloads. Feature endpoints retain responsibility for their own authorization, request contracts, privacy evidence, and settlement; embedding responses use their dedicated bounded inspection budget. Missing detectors fail closed.

### Public API discovery

The documentation site exposes client guides and common gateway operations without authentication or generated metadata. Generated API metadata remains optional; authored examples follow the current SDK and gateway contracts.

LiveUI manifest labels for public documentation targets must match their rendered page content so assistant guidance remains accurate when pages change.

### Cloud worker capability catalog

Document, knowledge, vision and analytics capabilities enter the Cloud catalog as not configured, without overwriting existing capability settings. Entitlements, application selection, verified deployment and readiness remain separate gates before use.

### Local worker model provisioning

The root Compose configuration defaults model provisioning to verification of existing assets and offline operation. Downloading approved, pinned assets requires an explicit provisioning and network configuration; enabling a worker does not implicitly download models.

Knowledge demonstration passages are tied to the submitted question; editing the question clears the previous result and its status before another search.

### Cloud classification privacy evidence

Cloud classification evidence records bounded, allowlisted detector category counts alongside the decision. It never stores the detected values, and historical observations without category data remain valid. Evidence remains append-only.

### Host privacy evidence ingestion

Cloud accepts content-free host privacy decisions only from separately pinned host certificates and authenticated tenant applications. Evidence uses bounded category counts, idempotent event IDs, credential revocation fencing and append-only storage.

Host privacy report reads require tenant security membership, repeat authorization before returning results, and produce audit events. HTTP routes delegate data access to the host privacy reader.

The Cloud security dashboard links to a separate host privacy report view. It clears displayed evidence on identity or tenant changes and distinguishes host decisions from Cloud classification checks.
