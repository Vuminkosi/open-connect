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
