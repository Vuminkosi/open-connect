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

Privacy documentation distinguishes the token vault's tenant, principal, session and purpose scope from any additional turn or policy-revision restrictions imposed by the host.

The public privacy example runs fixed synthetic inputs through an isolated SDK rules firewall without a token vault or external egress. It does not accept visitor-entered sensitive data or imply that detection is complete. The documentation LiveUI policy may trigger this idempotent example only on the privacy guide.

When generated API documents are present, LiveUI may inspect, focus, or set the public API search field with a bounded value. It does not advertise that field when the catalog is absent.

When the local documentation assistant and local Cloud identity are composed together, the site uses a combined CA bundle that trusts both loopback HTTPS services. The identity and assistant remain separate services with their own credentials and authorization boundaries.

The opt-in local Cloud management browser check compares tenant-scoped ledger state around public documentation and authenticated management visits. It requires an isolated test tenant so concurrent capability calls do not affect the comparison.

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
