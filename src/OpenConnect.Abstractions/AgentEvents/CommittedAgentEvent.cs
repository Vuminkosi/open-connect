namespace OpenConnect.Abstractions;

/// <summary>A committed, privacy-minimized agent event suitable for downstream projection.</summary>
/// <remarks>Events are published from a transactional outbox; this contract does not make <see cref="IEventJournal"/> a message bus. The reference identifies a projection only and must not contain prompts or PII.</remarks>
public sealed record CommittedAgentEvent(
    Guid EventId,
    string TenantId,
    string SessionId,
    string ExecutionId,
    string AgentId,
    AgentExecutionMode AgentMode,
    string EventType,
    long Sequence,
    string ProjectionReference,
    DateTimeOffset CommittedAt);
