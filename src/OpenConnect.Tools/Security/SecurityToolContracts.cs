using OpenConnect.Abstractions;

namespace OpenConnect.Tools.Security;

/// <summary>Canonical tool identifiers for host-brokered OpenConnect agent security tools (FW-090).</summary>
public static class SecurityToolIds
{
    public const string ScanText = "security_scan_text";
    public const string ScanImage = "security_scan_image";
    public const string ScanFile = "security_scan_file";
    public const string ScanPrompt = "security_scan_prompt";
    public const string RedactContent = "security_redact_content";
    public const string CheckDestination = "security_check_destination";
    public const string CheckDataAccess = "security_check_data_access";
    public const string ExplainEvent = "security_explain_event";

    /// <summary>All canonical security tool identifiers.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        ScanText,
        ScanImage,
        ScanFile,
        ScanPrompt,
        RedactContent,
        CheckDestination,
        CheckDataAccess,
        ExplainEvent
    ];

    /// <summary>Returns whether the specified tool ID is one of the canonical security tools.</summary>
    public static bool IsSecurityTool(ToolId toolId) =>
        All.Contains(toolId.Value, StringComparer.Ordinal);
}

/// <summary>Metadata describing a registered agent security tool, including its inspect vs enforce mode.</summary>
public sealed record SecurityToolMetadata(
    ToolId ToolId,
    string Name,
    string Mode,
    string Description,
    string JsonSchema,
    bool RequiresApproval,
    IReadOnlyList<string> BackingCapabilities);

/// <summary>Trusted host-bound identity context for a single brokered security tool invocation.</summary>
public sealed record SecurityToolExecutionContext(
    TenantId TenantId,
    PrincipalId PrincipalId,
    AgentId AgentId,
    TaskId TaskId,
    string ApplicationId = "openconnect-agent",
    string PolicyRevision = "1");

/// <summary>Provides ambient access to the trusted <see cref="SecurityToolExecutionContext"/> bound by <see cref="ToolBroker"/>.</summary>
public static class SecurityToolExecutionContextScope
{
    private static readonly AsyncLocal<SecurityToolExecutionContext?> CurrentContext = new();

    /// <summary>Gets the current trusted execution context, or <see langword="null"/> outside a brokered invocation.</summary>
    public static SecurityToolExecutionContext? Current => CurrentContext.Value;

    /// <summary>Pushes a trusted execution context for the current async flow until disposed.</summary>
    public static IDisposable Push(SecurityToolExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var previous = CurrentContext.Value;
        CurrentContext.Value = context;
        return new ScopePopper(previous);
    }

    private sealed class ScopePopper(SecurityToolExecutionContext? previous) : IDisposable
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

/// <summary>Tenant- and principal-authorized binary or text artifact resolved by the trusted host.</summary>
public sealed record SecurityArtifactContent(
    string ArtifactRef,
    TenantId TenantId,
    PrincipalId PrincipalId,
    string ContentType,
    ReadOnlyMemory<byte> Bytes,
    string Sha256Digest);

/// <summary>Result of storing a host-redacted artifact under trusted tenant/principal scope.</summary>
public sealed record SecurityRedactedArtifactResult(
    bool Succeeded,
    string? SanitizedArtifactRef,
    string HostDecision,
    int RedactedCount,
    string ReasonCode);

/// <summary>Resolves authorized artifact references strictly within trusted tenant and principal ownership.</summary>
public interface ISecurityArtifactResolver
{
    /// <summary>Resolves an artifact reference only if owned by the specified tenant and principal; otherwise returns <see langword="null"/>.</summary>
    ValueTask<SecurityArtifactContent?> TryResolveAsync(
        TenantId tenantId,
        PrincipalId principalId,
        string artifactRef,
        CancellationToken cancellationToken = default);

    /// <summary>Stores a redacted artifact bound to the trusted tenant and principal and returns its sanitized reference.</summary>
    ValueTask<SecurityRedactedArtifactResult> StoreRedactedAsync(
        TenantId tenantId,
        PrincipalId principalId,
        string sourceArtifactRef,
        string operation,
        ReadOnlyMemory<byte> redactedBytes,
        string contentType,
        CancellationToken cancellationToken = default);
}

/// <summary>Result of host-side content redaction execution.</summary>
public sealed record SecurityRedactionExecutionResult(
    bool Succeeded,
    ReadOnlyMemory<byte> RedactedBytes,
    string ContentType,
    int RedactedCount,
    string HostDecision,
    string ReasonCode);

/// <summary>Performs host-owned content redaction (such as flattened raster image redaction or text redaction).</summary>
public interface ISecurityContentRedactor
{
    /// <summary>Applies an approved redaction operation to an authorized artifact.</summary>
    ValueTask<SecurityRedactionExecutionResult> RedactAsync(
        TenantId tenantId,
        PrincipalId principalId,
        SecurityArtifactContent artifact,
        string operation,
        SecurityInspectionResponse? inspection,
        CancellationToken cancellationToken = default);
}

/// <summary>Read-only host policy assessment for a registered service destination or data operation.</summary>
public sealed record SecurityPolicyAssessment(
    bool PermittedByPolicy,
    string TargetId,
    string OperationId,
    string PolicyRevision,
    string ReasonCode,
    bool RequiresApproval = false);

/// <summary>Evaluates host egress and data-access policies for registered identifiers without executing target calls.</summary>
public interface ISecurityPolicyAssessmentProvider
{
    /// <summary>Assesses whether a registered service ID and operation ID are permitted by host policy.</summary>
    ValueTask<SecurityPolicyAssessment> AssessDestinationAsync(
        TenantId tenantId,
        PrincipalId principalId,
        string serviceId,
        string operationId,
        CancellationToken cancellationToken = default);

    /// <summary>Assesses whether a registered data operation ID is permitted by host policy.</summary>
    ValueTask<SecurityPolicyAssessment> AssessDataAccessAsync(
        TenantId tenantId,
        PrincipalId principalId,
        string dataOperationId,
        CancellationToken cancellationToken = default);
}

/// <summary>Looks up tenant-scoped, content-free security evidence events by identifier.</summary>
public interface ISecurityEvidenceLookup
{
    /// <summary>Returns the evidence event if it exists within the trusted tenant and application scope; otherwise <see langword="null"/>.</summary>
    ValueTask<SecurityEvidenceEvent?> GetByIdAsync(
        TenantId tenantId,
        string applicationId,
        Guid eventId,
        CancellationToken cancellationToken = default);
}

/// <summary>Passes serialized security tool output through the host privacy gate before returning to an agent.</summary>
public interface ISecurityToolOutputSanitizer
{
    /// <summary>Sanitizes serialized JSON tool output, throwing if content is blocked by privacy policy.</summary>
    ValueTask<string> SanitizeAsync(
        TenantId tenantId,
        PrincipalId principalId,
        string jsonOutput,
        CancellationToken cancellationToken = default);
}

/// <summary>Provides verified runtime capability health snapshots for dynamic security tool discovery and invocation gating.</summary>
public interface ISecurityCapabilityHealthProvider
{
    /// <summary>Returns the current verified capability snapshot with lease expiration.</summary>
    ValueTask<SecurityCapabilitiesSnapshot> GetCapabilitiesAsync(CancellationToken cancellationToken = default);
}

/// <summary>Configures tenant enablement, application approval, and resource bounds for agent security tools (FW-090).</summary>
public sealed class SecurityToolPolicyOptions
{
    /// <summary>Whether agent security tools are enabled at the host level. Defaults to <see langword="false"/>.</summary>
    public bool Enabled { get; set; }

    /// <summary>Application-approved security tool IDs. Empty means no security tools are approved.</summary>
    public ISet<string> ApplicationApprovedToolIds { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Optional predicate determining whether a capability is enabled for a given tenant. Defaults to deny-all if null.</summary>
    public Func<TenantId, string, bool>? IsCapabilityEnabledForTenant { get; set; }

    /// <summary>Approved redaction operations accepted by <c>security_redact_content</c>.</summary>
    public ISet<string> ApprovedRedactionOperations { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "redact_image",
        "redact_text",
        "mask_text"
    };

    /// <summary>Maximum UTF-8 byte length accepted for bounded text/prompt tool inputs (default 64 KiB).</summary>
    public int MaxTextBytes { get; set; } = 64 * 1024;

    /// <summary>Host threshold used by <c>security_scan_prompt</c> when reporting injection detection.</summary>
    public double PromptInjectionThreshold { get; set; } = 0.80;

    /// <summary>Application identifier bound into emitted security evidence.</summary>
    public string ApplicationId { get; set; } = "openconnect-agent";

    /// <summary>Policy revision identifier bound into worker requests and emitted security evidence.</summary>
    public string PolicyRevision { get; set; } = "1";
}
