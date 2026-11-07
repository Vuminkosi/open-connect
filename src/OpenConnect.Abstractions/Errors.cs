namespace OpenConnect.Abstractions;

/// <summary>Stable error codes shared across OpenConnect components.</summary>
public static class ErrorCodes
{
    public const string TaskNotFound = "task_not_found";
    public const string SessionNotFound = "session_not_found";
    public const string Unauthorized = "unauthorized";
    public const string ToolNotAllowed = "tool_not_allowed";
    public const string ApprovalRequired = "approval_required";
    public const string CapabilityUnavailable = "capability_unavailable";
    public const string PrivacyBlocked = "privacy_blocked";
    public const string InvalidRequest = "invalid_request";
    public const string Conflict = "conflict";
    public const string RateLimited = "rate_limited";
    public const string EfContextMissing = "ef_context_missing";
    public const string EfModelMissing = "ef_model_missing";
    public const string EfProviderUnsupported = "ef_provider_unsupported";
    public const string EfSchemaMissing = "ef_schema_missing";
    public const string EfSchemaMismatch = "ef_schema_mismatch";
    public const string EfMigrationNotAllowed = "ef_migration_not_allowed";
    public const string EfTransactionUnavailable = "ef_transaction_unavailable";
    public const string EfOutboxClaimConflict = "ef_outbox_claim_conflict";
    public const string EfLeaseExpired = "ef_lease_expired";
    public const string EfPersistenceUnavailable = "ef_persistence_unavailable";
    public const string EfInmemoryNotDurable = "ef_inmemory_not_durable";
    public const string EfConcurrencyConflict = "ef_concurrency_conflict";
}
