namespace OpenConnect.Abstractions.Persistence;

/// <summary>Persisted session state and its concurrency version.</summary>
public sealed record SessionRecord(SessionId Id, TenantId TenantId, PrincipalId PrincipalId, long Version);

/// <summary>Persisted distributed lease and fencing information.</summary>
public sealed record LeaseRecord(string ResourceKey, TenantId TenantId, string Owner, DateTimeOffset ExpiresAt, long FencingVersion);

/// <summary>Persisted approval state and optional decision.</summary>
public sealed record ApprovalRecord(Guid Id, TenantId TenantId, PrincipalId PrincipalId, TaskId TaskId, string State, string? Decision);

/// <summary>Encrypted value associated with a privacy token and its scope.</summary>
public sealed record PrivacyTokenBinding(string TokenId, TenantId TenantId, string PrincipalOrWorkloadScope, SessionId SessionId, string EntityType, byte[] EncryptedValue, DateTimeOffset ExpiresAt);

/// <summary>A claimed outbox entry together with the lease fencing data.</summary>
public sealed record OutboxClaim(string EntryId, string TenantScope, long FencingVersion, string LeaseOwner, DateTimeOffset LeaseExpiry);
