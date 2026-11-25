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
    public const string ProviderUnavailable = "provider_unavailable";
    public const string WorkerUnavailable = "worker_unavailable";
    public const string WorkerHostingConflict = "worker_hosting_conflict";
    public const string VoiceEgressDenied = "voice_egress_denied";
    public const string MalwareDetected = "malware_detected";
    public const string PromptInjectionDetected = "prompt_injection_detected";
    public const string ServiceFencingDenied = "service_fencing_denied";
    public const string DatabaseFencingDenied = "database_fencing_denied";
    public const string MessagingDisabled = "messaging_disabled";
    public const string ConversationNotFound = "conversation_not_found";
    public const string ConversationClosed = "conversation_closed";
    public const string HumanParticipantRequired = "human_participant_required";
    public const string SecurityProfileImmutable = "security_profile_immutable";
    public const string PrivateE2EEAgentForbidden = "private_e2ee_agent_forbidden";
    public const string CryptoEpochMismatch = "crypto_epoch_mismatch";
    public const string CryptoVerificationFailed = "crypto_verification_failed";
    public const string MediaEncryptionRequired = "media_encryption_required";
    public const string EnrollmentNotFound = "enrollment_not_found";
    public const string EnrollmentExpired = "enrollment_expired";
    public const string EnrollmentInvalidState = "enrollment_invalid_state";
    public const string TokenReuseDetected = "token_reuse_detected";
    public const string DeviceRevoked = "device_revoked";
    public const string CallNotFound = "call_not_found";
    public const string CallInvalidState = "call_invalid_state";
    public const string FollowUpNotFound = "followup_not_found";
    public const string FollowUpMaxAttemptsReached = "followup_max_attempts_reached";
    public const string FollowUpQuietHours = "followup_quiet_hours";
}
