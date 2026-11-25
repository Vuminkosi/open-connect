using System.Security.Cryptography;
using System.Text;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Messaging;

namespace OpenConnect.Tools.Messaging;

/// <summary>Canonical tool identifiers for the 11 host-brokered operational messaging tools.</summary>
public static class MessagingToolIds
{
    public const string ResolveRecipient = "messaging_resolve_recipient";
    public const string Send = "messaging_send";
    public const string RequestResponse = "messaging_request_response";
    public const string GetStatus = "messaging_get_status";
    public const string GetHistory = "messaging_get_history";
    public const string ScheduleFollowUp = "messaging_schedule_followup";
    public const string CancelFollowUp = "messaging_cancel_followup";
    public const string SendVoiceNote = "messaging_send_voice_note";
    public const string StartCall = "messaging_start_call";
    public const string RequestApproval = "messaging_request_approval";
    public const string Escalate = "messaging_escalate";

    /// <summary>All 11 canonical messaging tool identifiers.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        ResolveRecipient,
        Send,
        RequestResponse,
        GetStatus,
        GetHistory,
        ScheduleFollowUp,
        CancelFollowUp,
        SendVoiceNote,
        StartCall,
        RequestApproval,
        Escalate
    ];

    /// <summary>Returns whether the specified tool ID is one of the 11 canonical messaging tools.</summary>
    public static bool IsMessagingTool(ToolId toolId) =>
        All.Contains(toolId.Value, StringComparer.Ordinal);
}

/// <summary>Static metadata describing a registered operational messaging tool.</summary>
public sealed record MessagingToolMetadata(
    ToolId ToolId,
    string Name,
    string Description,
    string JsonSchema,
    MessagingPermission RequiredPermission,
    bool RequiresApproval);

/// <summary>Trusted host-bound execution context for a single brokered messaging tool invocation.</summary>
public sealed record MessagingToolExecutionContext(
    TenantId TenantId,
    PrincipalId PrincipalId,
    AgentId AgentId,
    TaskId TaskId,
    string ApplicationId = "openconnect",
    DeviceId? AgentDeviceId = null,
    int CausalDepth = 0,
    string? CausalEventId = null);

/// <summary>Ambient scope for trusted <see cref="MessagingToolExecutionContext"/> bound by the host.</summary>
public static class MessagingToolExecutionContextScope
{
    private static readonly AsyncLocal<MessagingToolExecutionContext?> CurrentContext = new();

    public static MessagingToolExecutionContext? Current => CurrentContext.Value;

    public static IDisposable Push(MessagingToolExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var previous = CurrentContext.Value;
        CurrentContext.Value = context;
        return new ScopePopper(previous);
    }

    private sealed class ScopePopper(MessagingToolExecutionContext? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            CurrentContext.Value = previous;
        }
    }
}

/// <summary>Host policy options governing operational agent messaging tools.</summary>
public sealed class MessagingToolPolicyOptions
{
    /// <summary>Whether agent messaging tools are enabled. Defaults to <see langword="false"/> (fail-closed).</summary>
    public bool Enabled { get; set; }

    /// <summary>Application identifier bound into agent messaging actors.</summary>
    public string ApplicationId { get; set; } = "openconnect";

    /// <summary>Maximum allowed causal execution depth to prevent agent-to-agent loops (default 3).</summary>
    public int MaxCausalDepth { get; set; } = 3;

    /// <summary>Maximum plaintext message character length accepted from an agent (default 8000).</summary>
    public int MaxMessageLength { get; set; } = 8000;

    /// <summary>Maximum history entries returned by <c>messaging_get_history</c> (default 50).</summary>
    public int MaxHistoryLimit { get; set; } = 50;

    /// <summary>Maximum follow-up attempts allowed per scheduled follow-up (default 3).</summary>
    public int MaxFollowUpAttempts { get; set; } = 3;

    /// <summary>Whether endpoint E2EE is required for agent sends and calls (default <see langword="true"/>).</summary>
    public bool RequireEncryption { get; set; } = true;

    /// <summary>Whether unencrypted transport-only fallback is allowed (default <see langword="false"/>).</summary>
    public bool AllowUnencryptedFallback { get; set; }

    /// <summary>Default expiry window for typed approval requests (default 15 minutes).</summary>
    public TimeSpan ApprovalDefaultLifetime { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Optional HMAC-SHA256 signing key for typed approval descriptors (if null, ephemeral CSPRNG key is generated).</summary>
    public byte[]? ApprovalSigningKey { get; set; }

    /// <summary>Per-agent granted messaging permissions. Agents not listed have zero permissions (default deny).</summary>
    public IDictionary<AgentId, ISet<MessagingPermission>> AgentPermissions { get; } = new Dictionary<AgentId, ISet<MessagingPermission>>();

    /// <summary>Grants a set of messaging permissions to a specific agent.</summary>
    public void GrantPermissions(AgentId agentId, params MessagingPermission[] permissions)
    {
        if (!AgentPermissions.TryGetValue(agentId, out var set))
        {
            set = new HashSet<MessagingPermission>();
            AgentPermissions[agentId] = set;
        }
        foreach (var permission in permissions)
            set.Add(permission);
    }

    /// <summary>Checks whether the specified agent holds the required permission.</summary>
    public bool HasPermission(AgentId agentId, MessagingPermission permission) =>
        AgentPermissions.TryGetValue(agentId, out var set) && set.Contains(permission);
}

/// <summary>Typed, time-bounded, HMAC-signed approval descriptor bound to a specific conversation action (never inferred from free text).</summary>
public sealed record TypedApprovalDescriptor(
    ApprovalRequestId ApprovalRequestId,
    TenantId TenantId,
    ConversationId ConversationId,
    AgentId AgentId,
    PrincipalId TargetHumanPrincipalId,
    string ActionType,
    string ActionSummary,
    string CanonicalParametersHashSha256,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string SignatureHex);

/// <summary>Creates and verifies HMAC-SHA256 signatures over <see cref="TypedApprovalDescriptor"/> instances.</summary>
public sealed class MessagingApprovalDescriptorSigner
{
    private readonly byte[] _key;

    public MessagingApprovalDescriptorSigner(byte[]? signingKey = null)
    {
        _key = signingKey is { Length: >= 32 } ? [.. signingKey] : RandomNumberGenerator.GetBytes(32);
    }

    public TypedApprovalDescriptor CreateSigned(
        ApprovalRequestId approvalRequestId,
        TenantId tenantId,
        ConversationId conversationId,
        AgentId agentId,
        PrincipalId targetHumanPrincipalId,
        string actionType,
        string actionSummary,
        string canonicalParametersJson,
        DateTimeOffset issuedAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionType);
        ArgumentException.ThrowIfNullOrWhiteSpace(actionSummary);
        if (expiresAtUtc <= issuedAtUtc)
            throw new ArgumentException("Approval expiry must be after issuedAtUtc.", nameof(expiresAtUtc));

        var paramHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalParametersJson ?? "{}"))).ToLowerInvariant();
        var canonical = BuildCanonical(approvalRequestId, tenantId, conversationId, agentId, targetHumanPrincipalId, actionType, paramHash, issuedAtUtc, expiresAtUtc);
        var sig = Convert.ToHexString(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();

        return new TypedApprovalDescriptor(
            approvalRequestId,
            tenantId,
            conversationId,
            agentId,
            targetHumanPrincipalId,
            actionType,
            actionSummary,
            paramHash,
            issuedAtUtc,
            expiresAtUtc,
            sig);
    }

    public bool Verify(TypedApprovalDescriptor descriptor, DateTimeOffset utcNow)
    {
        if (descriptor is null || utcNow >= descriptor.ExpiresAtUtc || string.IsNullOrWhiteSpace(descriptor.SignatureHex))
            return false;

        var canonical = BuildCanonical(
            descriptor.ApprovalRequestId,
            descriptor.TenantId,
            descriptor.ConversationId,
            descriptor.AgentId,
            descriptor.TargetHumanPrincipalId,
            descriptor.ActionType,
            descriptor.CanonicalParametersHashSha256,
            descriptor.IssuedAtUtc,
            descriptor.ExpiresAtUtc);

        var expected = HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(canonical));
        byte[] provided;
        try { provided = Convert.FromHexString(descriptor.SignatureHex); }
        catch (FormatException) { return false; }

        return provided.Length == expected.Length && CryptographicOperations.FixedTimeEquals(provided, expected);
    }

    private static string BuildCanonical(
        ApprovalRequestId approvalRequestId,
        TenantId tenantId,
        ConversationId conversationId,
        AgentId agentId,
        PrincipalId targetHumanPrincipalId,
        string actionType,
        string paramHash,
        DateTimeOffset issuedAtUtc,
        DateTimeOffset expiresAtUtc) =>
        $"{approvalRequestId.Value}|{tenantId.Value}|{conversationId.Value}|{agentId.Value}|{targetHumanPrincipalId.Value}|{actionType}|{paramHash}|{issuedAtUtc.ToUnixTimeMilliseconds()}|{expiresAtUtc.ToUnixTimeMilliseconds()}";
}

/// <summary>Participant-scoped decrypted message entry returned by <see cref="IMessagingToolHistoryProvider"/> at an authorized agent endpoint.</summary>
public sealed record MessagingHistoryEntry(
    MessageId MessageId,
    long Sequence,
    ParticipantKind SenderKind,
    string SenderDisplayId,
    string PlaintextContent,
    DateTimeOffset CreatedAtUtc);

/// <summary>Result of an operational escalation to a human supervisor.</summary>
public sealed record MessagingEscalationResult(
    string EscalationId,
    ConversationId ConversationId,
    PrincipalId TargetHumanPrincipalId,
    string Status,
    DateTimeOffset RecordedAtUtc);

/// <summary>Provides participant-scoped decrypted history strictly at an authorized agent endpoint.</summary>
public interface IMessagingToolHistoryProvider
{
    ValueTask<IReadOnlyList<MessagingHistoryEntry>> GetParticipantScopedHistoryAsync(
        TenantId tenantId,
        ConversationId conversationId,
        AgentId requestingAgentId,
        int limit,
        CancellationToken cancellationToken = default);
}

/// <summary>Persists typed, signed approval requests bound to a conversation action.</summary>
public interface IMessagingApprovalStore
{
    ValueTask<TypedApprovalDescriptor> SaveRequestAsync(
        TypedApprovalDescriptor descriptor,
        CancellationToken cancellationToken = default);
}

/// <summary>Handles operational agent escalations to verified human supervisors.</summary>
public interface IMessagingEscalationHandler
{
    ValueTask<MessagingEscalationResult> EscalateAsync(
        TenantId tenantId,
        ConversationId conversationId,
        AgentId requestingAgentId,
        PrincipalId targetHumanPrincipalId,
        string reason,
        string severity,
        CancellationToken cancellationToken = default);
}
