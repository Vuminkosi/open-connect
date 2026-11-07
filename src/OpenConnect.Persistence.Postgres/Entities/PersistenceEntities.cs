namespace OpenConnect.Persistence.Postgres.Entities;

/// <summary>Represents a persisted conversation session.</summary>
public sealed class SessionEntity
{
    /// <summary>Gets or sets the session identifier.</summary>
    public Guid Id { get; set; }
    /// <summary>Gets or sets the tenant identifier.</summary>
    public Guid TenantId { get; set; }
    /// <summary>Gets or sets the owning principal identifier.</summary>
    public Guid OwnerId { get; set; }
    /// <summary>Gets or sets the optimistic concurrency version.</summary>
    public long Version { get; set; } = 1;
    /// <summary>Gets or sets the serialized context checkpoint.</summary>
    public string? ContextCheckpoint { get; set; }
    /// <summary>Gets or sets the creation time.</summary>
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>Gets or sets the most recent update time.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
    /// <summary>Gets or sets the expiration time, when applicable.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }
}

/// <summary>Represents a persisted agent task.</summary>
public sealed class TaskEntity
{
    /// <summary>Gets or sets the task identifier.</summary>
    public Guid Id { get; set; }
    /// <summary>Gets or sets the tenant identifier.</summary>
    public Guid TenantId { get; set; }
    /// <summary>Gets or sets the principal identifier.</summary>
    public Guid PrincipalId { get; set; }
    /// <summary>Gets or sets the associated session identifier.</summary>
    public Guid SessionId { get; set; }
    /// <summary>Gets or sets the client-provided task idempotency key.</summary>
    public string ClientTaskId { get; set; } = "";
    /// <summary>Gets or sets the agent identifier.</summary>
    public string AgentId { get; set; } = "";
    /// <summary>Gets or sets the current task state.</summary>
    public string State { get; set; } = "";
    /// <summary>Gets or sets the optimistic concurrency version.</summary>
    public long Version { get; set; } = 1;
    /// <summary>Gets or sets the canonical task content hash.</summary>
    public string CanonicalHash { get; set; } = "";
    /// <summary>Gets or sets the reference to task input.</summary>
    public string? InputRef { get; set; }
    /// <summary>Gets or sets the reference to the task result.</summary>
    public string? ResultRef { get; set; }
    /// <summary>Gets or sets the creation time.</summary>
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>Gets or sets the completion time, when applicable.</summary>
    public DateTimeOffset? CompletedAt { get; set; }
    /// <summary>Gets or sets the durable completion receipt as JSON.</summary>
    public string? ReceiptPayload { get; set; }
    /// <summary>Gets or sets the durable receipt status.</summary>
    public string? ReceiptStatus { get; set; }
}

/// <summary>Represents one turn within a session.</summary>
public sealed class TurnEntity
{
    /// <summary>Gets or sets the turn identifier.</summary>
    public Guid Id { get; set; }
    /// <summary>Gets or sets the tenant identifier.</summary>
    public Guid TenantId { get; set; }
    /// <summary>Gets or sets the principal identifier.</summary>
    public Guid PrincipalId { get; set; }
    /// <summary>Gets or sets the associated session identifier.</summary>
    public Guid SessionId { get; set; }
    /// <summary>Gets or sets the sequence number within the session.</summary>
    public long Sequence { get; set; }
    /// <summary>Gets or sets the current turn state.</summary>
    public string State { get; set; } = "";
    /// <summary>Gets or sets the serialized turn context.</summary>
    public string? Context { get; set; }
    /// <summary>Gets or sets the turn start time.</summary>
    public DateTimeOffset StartedAt { get; set; }
    /// <summary>Gets or sets the completion time, when applicable.</summary>
    public DateTimeOffset? CompletedAt { get; set; }
}

/// <summary>Represents an event recorded for a session.</summary>
public sealed class EventEntity
{
    /// <summary>Gets or sets the event identifier.</summary>
    public Guid Id { get; set; }
    /// <summary>Gets or sets the associated session identifier.</summary>
    public Guid SessionId { get; set; }
    /// <summary>Gets or sets the tenant identifier.</summary>
    public Guid TenantId { get; set; }
    /// <summary>Gets or sets the session principal identifier.</summary>
    public Guid PrincipalId { get; set; }
    /// <summary>Gets or sets the sequence number within the session.</summary>
    public long Sequence { get; set; }
    /// <summary>Gets or sets the event type.</summary>
    public string EventType { get; set; } = "";
    /// <summary>Gets or sets the serialized event payload.</summary>
    public string Payload { get; set; } = "{}";
    /// <summary>Gets or sets the event occurrence time.</summary>
    public DateTimeOffset OccurredAt { get; set; }
    /// <summary>Gets or sets the correlation identifier, when available.</summary>
    public string? CorrelationId { get; set; }
}

/// <summary>Represents a message queued for reliable delivery.</summary>
public sealed class OutboxEntity
{
    /// <summary>Gets or sets the outbox message identifier.</summary>
    public Guid Id { get; set; }
    /// <summary>Gets or sets the delivery topic.</summary>
    public string Topic { get; set; } = "";
    /// <summary>Gets or sets the serialized message payload.</summary>
    public string Payload { get; set; } = "{}";
    /// <summary>Gets or sets the delivery state.</summary>
    public string State { get; set; } = "pending";
    /// <summary>Gets or sets the number of delivery attempts.</summary>
    public int Attempts { get; set; }
    /// <summary>Gets or sets the next scheduled delivery time.</summary>
    public DateTimeOffset NextAttemptAt { get; set; }
    /// <summary>Gets or sets the creation time.</summary>
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>Gets or sets the successful delivery time, when applicable.</summary>
    public DateTimeOffset? DeliveredAt { get; set; }
}

/// <summary>Represents a time-limited distributed lease.</summary>
public sealed class LeaseEntity
{
    /// <summary>Gets or sets the tenant identifier.</summary>
    public Guid TenantId { get; set; }
    /// <summary>Gets or sets the lease name.</summary>
    public string Name { get; set; } = "";
    /// <summary>Gets or sets the lease owner.</summary>
    public string Owner { get; set; } = "";
    /// <summary>Gets or sets the lease expiration time.</summary>
    public DateTimeOffset ExpiresAt { get; set; }
    /// <summary>Gets or sets the optimistic concurrency version.</summary>
    public long Version { get; set; }
}

/// <summary>Represents an approval request for a task.</summary>
public sealed class ApprovalEntity
{
    /// <summary>Gets or sets the approval identifier.</summary>
    public Guid Id { get; set; }
    /// <summary>Gets or sets the tenant identifier.</summary>
    public Guid TenantId { get; set; }
    /// <summary>Gets or sets the principal identifier.</summary>
    public Guid PrincipalId { get; set; }
    /// <summary>Gets or sets the associated task identifier.</summary>
    public Guid TaskId { get; set; }
    /// <summary>Gets or sets the command identifier protected by this approval.</summary>
    public string? CommandId { get; set; }
    /// <summary>Gets or sets the canonical command digest.</summary>
    public string? CommandDigest { get; set; }
    /// <summary>Gets or sets the approval expiration time.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }
    /// <summary>Gets or sets when the approval was consumed.</summary>
    public DateTimeOffset? ConsumedAt { get; set; }
    /// <summary>Gets or sets the approval state.</summary>
    public string State { get; set; } = "pending";
    /// <summary>Gets or sets the approval decision, when present.</summary>
    public string? Decision { get; set; }
    /// <summary>Gets or sets the creation time.</summary>
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>Gets or sets the decision time, when applicable.</summary>
    public DateTimeOffset? DecidedAt { get; set; }
}

/// <summary>Represents an encrypted value associated with a privacy token.</summary>
public sealed class PrivacyTokenEntity
{
    /// <summary>Gets or sets the privacy token.</summary>
    public string Token { get; set; } = "";
    /// <summary>Gets or sets the encrypted value.</summary>
    public byte[] EncryptedValue { get; set; } = [];
    /// <summary>Gets or sets the tenant identifier.</summary>
    public Guid TenantId { get; set; }
    /// <summary>Gets or sets the principal identifier.</summary>
    public Guid PrincipalId { get; set; }
    /// <summary>Gets or sets the associated session identifier.</summary>
    public Guid SessionId { get; set; }
    /// <summary>Gets or sets the associated turn identifier, when applicable.</summary>
    public Guid? TurnId { get; set; }
    /// <summary>Gets or sets the detected entity type.</summary>
    public string EntityType { get; set; } = "";
    /// <summary>Gets or sets the encryption key identifier.</summary>
    public string KeyId { get; set; } = "";
    /// <summary>Gets or sets the creation time.</summary>
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>Gets or sets the expiration time.</summary>
    public DateTimeOffset ExpiresAt { get; set; }
}
