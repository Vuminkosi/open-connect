using OpenConnect.Abstractions;

namespace OpenConnect.Privacy;

/// <summary>Identifies the boundary to which content may be sent.</summary>
public enum EgressDestination
{
    /// <summary>An external model provider.</summary>
    ExternalModel,
    /// <summary>A tool.</summary>
    Tool,
    /// <summary>A worker.</summary>
    Worker,
    /// <summary>Application output.</summary>
    Output,
    /// <summary>NATS transport.</summary>
    Nats,
    /// <summary>A WebSocket transport.</summary>
    WebSocket,
    /// <summary>An audit log.</summary>
    AuditLog
}
/// <summary>Describes the privacy policy action applied to content.</summary>
public enum PrivacyAction
{
    /// <summary>Allow content without modification.</summary>
    Allow,
    /// <summary>Mask detected sensitive entities.</summary>
    Mask,
    /// <summary>Remove detected sensitive entities.</summary>
    Redact,
    /// <summary>Prevent content from proceeding.</summary>
    Block,
    /// <summary>Require review before content proceeds.</summary>
    Review
}

/// <summary>Identifies the tenant, principal, session, and purpose for a privacy operation.</summary>
/// <param name="Tenant">Tenant that owns the operation.</param>
/// <param name="Principal">Principal performing the operation.</param>
/// <param name="Session">Session associated with the operation.</param>
/// <param name="Purpose">Purpose for which the protected value is used.</param>
public sealed record PrivacyScope(TenantId Tenant, PrincipalId Principal, SessionId Session, string Purpose)
{
    /// <summary>Validates this scope and returns it.</summary>
    /// <exception cref="ArgumentException">The purpose is null, empty, or whitespace.</exception>
    public PrivacyScope Validate()
    {
        if (string.IsNullOrWhiteSpace(Purpose)) throw new ArgumentException("A token purpose is required.", nameof(Purpose));
        return this;
    }
}

/// <summary>Describes the location and type of a detected entity in content.</summary>
/// <param name="Start">Zero-based start position in the original content.</param>
/// <param name="Length">Length of the detected span.</param>
/// <param name="EntityType">Detected entity category.</param>
public sealed record EntitySpan(int Start, int Length, string EntityType);
/// <summary>Provides identity and destination context for an egress evaluation.</summary>
/// <param name="Tenant">Tenant that owns the operation.</param>
/// <param name="Principal">Principal performing the operation.</param>
/// <param name="Session">Session associated with the operation.</param>
/// <param name="Destination">Destination to which content may be sent.</param>
public sealed record EgressContext(TenantId Tenant, PrincipalId Principal, SessionId Session, EgressDestination Destination);
/// <summary>Contains the outcome and any sanitized content from an egress evaluation.</summary>
/// <param name="Allowed">Whether the content may proceed.</param>
/// <param name="SanitizedContent">Sanitized content, or <see langword="null"/> when blocked.</param>
/// <param name="DetectedEntities">Entities detected in the content.</param>
/// <param name="Action">Policy action applied.</param>
/// <param name="Reason">Stable reason describing the decision.</param>
public sealed record EgressDecision(bool Allowed, string? SanitizedContent, IReadOnlyList<EntitySpan> DetectedEntities, SecurityPolicyAction Action, string Reason);
/// <summary>Contains detector availability, sanitized content, and detected entities.</summary>
/// <param name="Available">Whether the detector was available to perform inspection.</param>
/// <param name="SanitizedContent">Sanitized form of the input.</param>
/// <param name="DetectedEntities">Entities detected in the input.</param>
public sealed record DetectorResult(bool Available, string SanitizedContent, IReadOnlyList<EntitySpan> DetectedEntities);

/// <summary>Detects sensitive content and returns a sanitized representation.</summary>
public interface ISecurityDetector
{
    /// <summary>Detects and sanitizes content for the specified egress context.</summary>
    /// <param name="content">Content to inspect.</param>
    /// <param name="context">Tenant, principal, session, and destination context.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>The detector result.</returns>
    ValueTask<DetectorResult> DetectAndSanitizeAsync(string content, EgressContext context, CancellationToken cancellationToken = default);
}

/// <summary>Evaluates whether content may leave a privacy boundary.</summary>
public interface IPrivacyFirewall
{
    /// <summary>Gets whether privacy enforcement is enabled.</summary>
    bool Enabled { get; }
    /// <summary>Evaluates content for the specified egress context.</summary>
    /// <param name="content">Content to evaluate.</param>
    /// <param name="context">Tenant, principal, session, and destination context.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>The egress decision.</returns>
    ValueTask<EgressDecision> EvaluateAsync(string content, EgressContext context, CancellationToken cancellationToken = default);
}

/// <summary>Enforces privacy decisions at content egress boundaries.</summary>
public interface IEgressGate
{
    /// <summary>Evaluates content for the specified egress context.</summary>
    /// <param name="content">Content to evaluate.</param>
    /// <param name="context">Tenant, principal, session, and destination context.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>The egress decision.</returns>
    ValueTask<EgressDecision> EvaluateAsync(string content, EgressContext context, CancellationToken cancellationToken = default);
}

/// <summary>Stores and resolves protected values using scoped tokens.</summary>
public interface ITokenVault
{
    /// <summary>Stores a value and returns a scoped token for it.</summary>
    /// <param name="value">Value to protect.</param>
    /// <param name="entityType">Type of entity represented by the value.</param>
    /// <param name="scope">Scope that must be supplied to resolve the token.</param>
    /// <param name="ttl">How long the token remains valid.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>The generated token.</returns>
    ValueTask<string> StoreAsync(string value, string entityType, PrivacyScope scope, TimeSpan ttl, CancellationToken cancellationToken = default);
    /// <summary>Resolves a token only when it belongs to the supplied scope and has not expired.</summary>
    /// <param name="token">Token to resolve.</param>
    /// <param name="scope">Required token scope.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>The protected value, or <see langword="null"/> if it cannot be resolved.</returns>
    ValueTask<string?> ResolveAsync(string token, PrivacyScope scope, CancellationToken cancellationToken = default);
}

/// <summary>Configures privacy enforcement and token lifetime defaults.</summary>
public sealed class PrivacyOptions
{
    /// <summary>Gets or sets whether privacy enforcement is enabled. Defaults to <see langword="true"/>.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Gets or sets the firewall enforcement mode.</summary>
    public FirewallMode Mode { get; set; } = FirewallMode.StrictLocal;
    /// <summary>Gets or sets the default lifetime for privacy tokens.</summary>
    public TimeSpan DefaultTokenTtl { get; set; } = TimeSpan.FromMinutes(30);
}

/// <summary>Raised when privacy policy prevents content from proceeding.</summary>
/// <param name="message">Description of the privacy policy failure.</param>
public sealed class PrivacyBlockedException(string message) : InvalidOperationException(message);
