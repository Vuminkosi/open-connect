---
inclusion: always
name: OpenConnect Design
description: Implementation-ready architecture and contracts
---

# OpenConnect Design Specification

**Version:** 1.1-draft
**Status:** Implementation-ready design
**Last Updated:** 2026-11-06

## Architecture Overview

OpenConnect follows a layered architecture with clear separation of concerns:

```
┌─────────────────────────────────────────────────────────────┐
│                    Applications                              │
│         VoiceDesk          │        PrivateDocs             │
├─────────────────────────────────────────────────────────────┤
│                    UI Layer                                  │
│   Blazor Components  │  TypeScript Client  │  .NET Client   │
├─────────────────────────────────────────────────────────────┤
│                    Gateway Layer                             │
│         ASP.NET Core Hosting (OpenConnect.Hosting)          │
├─────────────────────────────────────────────────────────────┤
│                    Agent Layer                               │
│         Microsoft Agent Framework (MAF) Integration          │
├─────────────────────────────────────────────────────────────┤
│                    Privacy Layer                             │
│         Privacy Firewall (OpenConnect.Privacy)              │
├─────────────────────────────────────────────────────────────┤
│                    Runtime Layer                             │
│         Core Services (OpenConnect.Core/Agents/Tools)       │
├─────────────────────────────────────────────────────────────┤
│                    Transport Layer                           │
│         Messaging (OpenConnect.Transport.Nats)              │
├─────────────────────────────────────────────────────────────┤
│                    Persistence Layer                         │
│         Storage (OpenConnect.Persistence.Postgres)          │
├─────────────────────────────────────────────────────────────┤
│                    Worker Layer (Optional Python)            │
│   Security  │  Classification  │  TTS  │  STT  │  Inference │
└─────────────────────────────────────────────────────────────┘
```

## Core Abstractions

### Identifiers (Strongly-Typed Value Objects)

```csharp
namespace OpenConnect.Abstractions;

public readonly record struct TenantId(Ulid Value);
public readonly record struct PrincipalId(Ulid Value);
public readonly record struct SessionId(Ulid Value);
public readonly record struct TaskId(Ulid Value);
public readonly record struct AgentId(string Value);
public readonly record struct ExecutionId(Ulid Value);
public readonly record struct CapabilityId(string Value);
public readonly record struct ToolId(string Value);
```

### Core Interfaces

```csharp
namespace OpenConnect.Abstractions;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public interface IIdGenerator
{
    Ulid NewUlid();
    string NewCorrelationId();
}

public interface IAgentDefinitionRegistry
{
    AgentDefinition? Get(AgentId agentId);
    IReadOnlyList<AgentDefinition> GetAll();
    void Register(AgentDefinition definition);
}

public interface IToolCatalog
{
    ToolDescriptor? Get(ToolId toolId);
    IReadOnlyList<ToolDescriptor> GetForAgent(AgentId agentId);
    IReadOnlyList<ToolDescriptor> GetAuthorized(AgentId agentId, PrincipalId principal, TenantId tenant);
}

public interface ICapabilityRegistry
{
    CapabilityDescriptor? Get(CapabilityId capabilityId);
    IReadOnlyList<CapabilityDescriptor> GetReady(CapabilityKind kind);
    void Register(CapabilityDescriptor descriptor);
    void MarkStale(CapabilityId capabilityId);
}

public interface ITaskStore
{
    Task<TaskRecord> AcceptAsync(TaskAcceptance acceptance, CancellationToken cancellationToken = default);
    Task<TaskRecord?> GetAsync(TaskId taskId, CancellationToken cancellationToken = default);
    Task<TaskRecord?> GetByIdempotencyKeyAsync(string clientTaskId, TenantId tenant, PrincipalId principal, CancellationToken cancellationToken = default);
}

public interface IEventJournal
{
    Task<long> AppendAsync(EventRecord @event, CancellationToken cancellationToken = default);
    IAsyncEnumerable<EventRecord> ReplayAsync(SessionId sessionId, long afterSequence = 0, CancellationToken cancellationToken = default);
}

public interface IOutboxStore
{
    Task<OutboxEntry> EnqueueAsync(OutboxEntry entry, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OutboxEntry>> GetPendingAsync(int limit, CancellationToken cancellationToken = default);
    Task MarkDeliveredAsync(Guid entryId, CancellationToken cancellationToken = default);
}
```

## Enum Contracts

### Agent Execution Modes

```csharp
namespace OpenConnect.Abstractions;

/// <summary>
/// Defines how an agent executes.
/// </summary>
/// <remarks>
/// Wire format: kebab-case strings ("interactive", "operational").
/// Numeric values are rejected in configuration.
/// </remarks>
public enum AgentExecutionMode
{
    /// <summary>
    /// Runs under authenticated current user's derived authorization scope.
    /// Reauthorizes consequential tool calls. Delegation cannot expand permissions.
    /// </summary>
    Interactive,
    
    /// <summary>
    /// Runs under separate constrained workload identity.
    /// Managed by BackgroundService supervisor with schedules, leases, and retries.
    /// </summary>
    Operational
}
```

### Agent Activation Modes

```csharp
namespace OpenConnect.Abstractions;

public enum AgentActivationMode
{
    /// <summary>Activated on-demand by task request.</summary>
    OnDemand,
    
    /// <summary>Activated on a schedule (cron expression).</summary>
    Scheduled,
    
    /// <summary>Runs continuously when enabled.</summary>
    Continuous,
    
    /// <summary>Activated by external event.</summary>
    EventTriggered
}
```

### Agent Run States

```csharp
namespace OpenConnect.Abstractions;

public enum AgentRunState
{
    Registered,
    Queued,
    Starting,
    Running,
    Waiting,
    Paused,
    Completed,
    Failed,
    Cancelled,
    Stale
}
```

### Agent Identity Modes

```csharp
namespace OpenConnect.Abstractions;

public enum AgentIdentityMode
{
    /// <summary>Use current authenticated user's context (Interactive mode).</summary>
    ActiveUser,
    
    /// <summary>Use dedicated workload identity (Operational mode).</summary>
    WorkloadIdentity
}
```

### Tool Impact Levels

```csharp
namespace OpenConnect.Abstractions;

public enum ToolImpact
{
    /// <summary>No side effects, read-only operations.</summary>
    ReadOnly,
    
    /// <summary>Writes to local state, no external effects.</summary>
    Write,
    
    /// <summary>External side effects (API calls, network operations).</summary>
    ExternalSideEffect
}
```

### Privacy Modes

```csharp
namespace OpenConnect.Abstractions;

public enum FirewallMode
{
    /// <summary>
    /// Detection executes locally. External egress only with sanitized payload.
    /// Missing required detector blocks protected operations.
    /// </summary>
    StrictLocal,
    
    /// <summary>
    /// Detection executes locally. No remote model egress permitted.
    /// </summary>
    LocalOnly,
    
    /// <summary>
    /// Detection and egress controlled by explicit policy configuration.
    /// </summary>
    PolicyControlled,
    
    /// <summary>
    /// Privacy enforcement disabled. Must be explicit in trusted server config.
    /// Authentication/authorization remain active.
    /// </summary>
    Disabled
}
```

### Firewall Failure Actions

```csharp
namespace OpenConnect.Abstractions;

public enum FirewallFailureAction
{
    /// <summary>Block operation when privacy requirements cannot be met.</summary>
    Deny,
    
    /// <summary>
    /// Allow local-only processing if capability is verified local.
    /// Requires explicit trust zone verification.
    /// </summary>
    LocalOnlyIfVerified
}
```

### Security Detector Kinds

```csharp
namespace OpenConnect.Abstractions;

public enum SecurityDetectorKind
{
    /// <summary>
    /// Rule-based detection for structured PII (emails, phones, IDs, secrets).
    /// Enabled by default. CPU-only, minimal resources.
    /// </summary>
    Rules,
    
    /// <summary>
    /// Semantic PII detection using local NER model.
    /// Disabled by default. Requires GLiNER checkpoint.
    /// </summary>
    GlinerPii,
    
    /// <summary>
    /// Prompt injection detection.
    /// Disabled by default. Requires DeBERTa model.
    /// </summary>
    DebertaInjection,
    
    /// <summary>
    /// Content safety classification.
    /// Disabled by default. Requires Qwen3Guard model.
    /// </summary>
    Qwen3Guard,
    
    /// <summary>
    /// Text extraction from images.
    /// Disabled by default. Requires PaddleOCR.
    /// </summary>
    PaddleOcr,
    
    /// <summary>
    /// Face detection in images.
    /// Disabled by default. Requires YuNet model.
    /// </summary>
    YuNet
}
```

### Detector States

```csharp
namespace OpenConnect.Abstractions;

public enum DetectorState
{
    /// <summary>Not configured or explicitly disabled.</summary>
    Disabled,
    
    /// <summary>Loading model resources.</summary>
    Loading,
    
    /// <summary>Ready for detection.</summary>
    Ready,
    
    /// <summary>Operational but with degraded capability.</summary>
    Degraded,
    
    /// <summary>Failed to load or runtime error.</summary>
    Failed
}
```

### Security Policy Actions

```csharp
namespace OpenConnect.Abstractions;

public enum SecurityPolicyAction
{
    /// <summary>Allow content to pass through.</summary>
    Allow,
    
    /// <summary>Replace with placeholder token.</summary>
    Mask,
    
    /// <summary>Remove entirely from content.</summary>
    Redact,
    
    /// <summary>Block operation.</summary>
    Block,
    
    /// <summary>Queue for human review.</summary>
    Review
}
```

### Transport Modes

```csharp
namespace OpenConnect.Abstractions;

public enum OpenConnectTransportMode
{
    /// <summary>Use NATS JetStream for durable messaging.</summary>
    JetStream,
    
    /// <summary>In-memory transport for development only.</summary>
    InMemoryDevelopment
}
```

## Options Classes

### OpenConnectOptions

```csharp
namespace OpenConnect.Core.Configuration;

public sealed class OpenConnectOptions
{
    public TransportOptions Transport { get; } = new();
    public RuntimeOptions Runtime { get; } = new();
    public AgentRegistryOptions Agents { get; } = new();
    public PrivacyOptions Privacy { get; } = new();
}

public sealed class TransportOptions
{
    public OpenConnectTransportMode Mode { get; set; } = OpenConnectTransportMode.JetStream;
}

public sealed class RuntimeOptions
{
    public string? InstructionsPath { get; set; } = "agent-runtime.md";
}

public sealed class AgentRegistryOptions
{
    public AgentExecutionMode DefaultExecutionMode { get; set; } = AgentExecutionMode.Interactive;
    public bool RegisterLeadAgent { get; set; } = true;
}
```

### PrivacyOptions

```csharp
namespace OpenConnect.Privacy.Configuration;

public sealed class PrivacyOptions
{
    public bool Enabled { get; set; } = true;
    public FirewallMode Mode { get; set; } = FirewallMode.StrictLocal;
    public FirewallFailureAction OnFailure { get; set; } = FirewallFailureAction.Deny;
    public SecurityDetectorOptions Detectors { get; } = new();
    public SecurityPolicyOptions Policies { get; } = new();
    
    internal bool RequiresSecurityWorker => 
        Enabled && Mode != FirewallMode.Disabled && 
        (Detectors.Rules.Enabled || Detectors.GlinerPii.Enabled);
}
```

### SecurityDetectorOptions

```csharp
namespace OpenConnect.Privacy.Configuration;

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

public enum DetectorDevice
{
    Auto,
    Cpu,
    Cuda
}

public enum DetectorLoadPolicy
{
    Startup,
    LazyOnFirstUse
}
```

## Dependency Injection

### Registration Pattern

```csharp
namespace OpenConnect.Hosting.AspNetCore;

public static class OpenConnectServiceCollectionExtensions
{
    public static IServiceCollection AddOpenConnect(
        this IServiceCollection services,
        Action<OpenConnectOptions>? configure = null)
    {
        // Options with defaults
        services.AddOptions<OpenConnectOptions>()
            .Configure(options =>
            {
                // Defaults already set in options classes
                configure?.Invoke(options);
            })
            .Validate(options => ValidateOptions(options))
            .ValidateOnStart();
        
        // Core services
        services.TryAddSingleton<IClock, SystemClock>();
        services.TryAddSingleton<IIdGenerator, UlidIdGenerator>();
        
        // Registries
        services.TryAddSingleton<IAgentDefinitionRegistry, AgentDefinitionRegistry>();
        services.TryAddSingleton<IToolCatalog, ToolCatalog>();
        services.TryAddSingleton<ICapabilityRegistry, CapabilityRegistry>();
        
        // Privacy (always registered, may be disabled via options)
        services.TryAddSingleton<IPrivacyFirewall, PrivacyFirewall>();
        services.TryAddSingleton<ITokenVault, EncryptedTokenVault>();
        
        // Register default lead agent if enabled
        var options = services.BuildServiceProvider()
            .GetRequiredService<IOptions<OpenConnectOptions>>().Value;
        
        if (options.Agents.RegisterLeadAgent)
        {
            services.RegisterDefaultLeadAgent();
        }
        
        return services;
    }
    
    private static bool ValidateOptions(OpenConnectOptions options)
    {
        // Privacy disabled must be explicit
        if (!options.Privacy.Enabled)
        {
            // Log warning - but don't fail validation
        }
        
        // Validate mode/identity combinations
        // Validate detector configurations
        // etc.
        
        return true;
    }
}
```

## PostgreSQL Schema

### Sessions Table

```sql
CREATE TABLE oc_sessions (
    id UUID PRIMARY KEY,
    tenant_id UUID NOT NULL,
    owner_id UUID NOT NULL,
    version BIGINT NOT NULL DEFAULT 1,
    context_checkpoint JSONB,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    expires_at TIMESTAMPTZ,
    
    CONSTRAINT uq_session_tenant_owner UNIQUE (tenant_id, owner_id, id)
);

CREATE INDEX idx_sessions_tenant_owner ON oc_sessions(tenant_id, owner_id);
```

### Tasks Table

```sql
CREATE TABLE oc_tasks (
    id UUID PRIMARY KEY,
    tenant_id UUID NOT NULL,
    principal_id UUID NOT NULL,
    session_id UUID NOT NULL,
    client_task_id TEXT NOT NULL,
    agent_id TEXT NOT NULL,
    state TEXT NOT NULL,
    canonical_hash TEXT NOT NULL,
    input_ref TEXT,
    result_ref TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    completed_at TIMESTAMPTZ,
    
    CONSTRAINT uq_task_idempotency UNIQUE (tenant_id, principal_id, client_task_id)
);

CREATE INDEX idx_tasks_session ON oc_tasks(session_id);
CREATE INDEX idx_tasks_state ON oc_tasks(state);
```

### Events Table

```sql
CREATE TABLE oc_events (
    id UUID PRIMARY KEY,
    session_id UUID NOT NULL,
    sequence BIGINT NOT NULL,
    event_type TEXT NOT NULL,
    payload JSONB NOT NULL,
    occurred_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    correlation_id TEXT,
    
    CONSTRAINT uq_event_sequence UNIQUE (session_id, sequence)
);

CREATE INDEX idx_events_session_sequence ON oc_events(session_id, sequence);
```

### Outbox Table

```sql
CREATE TABLE oc_outbox (
    id UUID PRIMARY KEY,
    topic TEXT NOT NULL,
    payload JSONB NOT NULL,
    state TEXT NOT NULL DEFAULT 'pending',
    attempts INT NOT NULL DEFAULT 0,
    next_attempt_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    delivered_at TIMESTAMPTZ
);

CREATE INDEX idx_outbox_pending ON oc_outbox(state, next_attempt_at) 
    WHERE state = 'pending';
```

### Privacy Tokens Table

```sql
CREATE TABLE oc_privacy_tokens (
    token TEXT PRIMARY KEY,
    encrypted_value BYTEA NOT NULL,
    tenant_id UUID NOT NULL,
    principal_id UUID NOT NULL,
    session_id UUID NOT NULL,
    turn_id UUID,
    entity_type TEXT NOT NULL,
    key_id TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    expires_at TIMESTAMPTZ NOT NULL,
    
    CONSTRAINT uq_token_scope UNIQUE (token, tenant_id, session_id)
);

CREATE INDEX idx_tokens_session ON oc_privacy_tokens(session_id);
CREATE INDEX idx_tokens_expiry ON oc_privacy_tokens(expires_at);
```

## Error Codes

```csharp
namespace OpenConnect.Abstractions;

public static class ErrorCodes
{
    public const string TaskNotFound = "task_not_found";
    public const string SessionNotFound = "session_not_found";
    public const string AgentNotFound = "agent_not_found";
    public const string ToolNotAllowed = "tool_not_allowed";
    public const string ApprovalRequired = "approval_required";
    public const string CapabilityUnavailable = "capability_unavailable";
    public const string PrivacyBlocked = "privacy_blocked";
    public const string Unauthorized = "unauthorized";
    public const string TenantIsolationViolation = "tenant_isolation_violation";
    public const string InvalidConfiguration = "invalid_configuration";
    public const string IdempotencyConflict = "idempotency_conflict";
}
```

## MAF Integration Points

### Agent Definition to MAF Agent

```csharp
namespace OpenConnect.Agents.MafAdapter;

public interface IMafAgentFactory
{
    IAgent CreateAgent(AgentDefinition definition, AgentExecutionContext context);
}

public class MafAgentFactory : IMafAgentFactory
{
    private readonly IChatClient _chatClient;
    private readonly IToolCatalog _toolCatalog;
    private readonly IPrivacyFirewall _privacyFirewall;
    
    public IAgent CreateAgent(AgentDefinition definition, AgentExecutionContext context)
    {
        // Create MAF agent with OpenConnect policy layers
        // Wrap chat client with privacy gate
        // Wrap tool execution with authorization
        // Project events to OpenConnect journal
        throw new NotImplementedException();
    }
}
```

## Event Types

```csharp
namespace OpenConnect.Abstractions.Events;

public abstract record EventRecord(
    Ulid EventId,
    string EventType,
    SessionId SessionId,
    long Sequence,
    DateTimeOffset OccurredAt,
    string? CorrelationId);

public record TaskAcceptedEvent(
    Ulid EventId,
    SessionId SessionId,
    long Sequence,
    DateTimeOffset OccurredAt,
    string? CorrelationId,
    TaskId TaskId,
    AgentId AgentId
) : EventRecord(EventId, "task.accepted", SessionId, Sequence, OccurredAt, CorrelationId);

public record TaskOutputDeltaEvent(
    Ulid EventId,
    SessionId SessionId,
    long Sequence,
    DateTimeOffset OccurredAt,
    string? CorrelationId,
    TaskId TaskId,
    ExecutionId ExecutionId,
    string Delta
) : EventRecord(EventId, "task.output.delta", SessionId, Sequence, OccurredAt, CorrelationId);
```

## Security Boundaries

### Egress Gate Interface

```csharp
namespace OpenConnect.Privacy;

public interface IEgressGate
{
    Task<EgressDecision> EvaluateAsync(
        string content,
        EgressContext context,
        CancellationToken cancellationToken = default);
}

public record EgressDecision(
    bool Allowed,
    string? SanitizedContent,
    IReadOnlyList<EntitySpan> DetectedEntities,
    SecurityPolicyAction Action,
    string Reason
);

public record EgressContext(
    TenantId Tenant,
    PrincipalId Principal,
    SessionId Session,
    EgressDestination Destination
);

public enum EgressDestination
{
    ExternalModel,
    Tool,
    Worker,
    WebSocket,
    AuditLog
}
```

## References

- Full requirements: `.kiro/specs/openconnect-core/requirements.md`
- Implementation tasks: `.kiro/specs/openconnect-core/tasks.md`
- Source documents: `docs/plans/*.md`
