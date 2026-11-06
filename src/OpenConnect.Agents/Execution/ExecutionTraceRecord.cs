namespace OpenConnect.Agents.Execution;

/// <summary>Describes one agent execution and its parent-child relationship.</summary>
public sealed record ExecutionTraceRecord(
    Guid Id,
    Guid? ParentId,
    string AgentId,
    string Kind,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt);
