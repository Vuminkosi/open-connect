---
inclusion: auto
name: openconnect-architecture
description: .NET solution architecture, package boundaries, and system design for OpenConnect
---

# OpenConnect Architecture Steering

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
