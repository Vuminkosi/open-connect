using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using OpenConnect.Abstractions;

namespace OpenConnect.Privacy;

/// <summary>Content-free metadata for one host privacy decision; this does not deliver an event.</summary>
public sealed class HostPrivacyEvidence
{
    private const string DefaultEnforcementAuthority = "application-host";

    private HostPrivacyEvidence(
        Guid eventId,
        string tenantId,
        EgressDestination destination,
        bool allowed,
        SecurityPolicyAction action,
        string reasonCode,
        IReadOnlyDictionary<string, int> categories,
        string? traceId,
        string? requestId,
        DateTimeOffset? occurredAt,
        string? policyId,
        string? policyRevision,
        string enforcementAuthority,
        string integrityDigest)
    {
        EventId = eventId;
        TenantId = tenantId;
        Destination = destination;
        Allowed = allowed;
        Action = action;
        ReasonCode = reasonCode;
        Categories = categories;
        TraceId = traceId;
        RequestId = requestId;
        OccurredAt = occurredAt;
        PolicyId = policyId;
        PolicyRevision = policyRevision;
        EnforcementAuthority = enforcementAuthority;
        IntegrityDigest = integrityDigest;
    }

    /// <summary>Gets the caller-supplied stable event identifier for retry deduplication.</summary>
    public Guid EventId { get; }
    /// <summary>Gets the tenant identifier asserted by the local host.</summary>
    public string TenantId { get; }
    /// <summary>Gets the inspected boundary, including output where applicable.</summary>
    public EgressDestination Destination { get; }
    /// <summary>Gets whether the decision allowed content to proceed.</summary>
    public bool Allowed { get; }
    /// <summary>Gets the policy action applied by the firewall.</summary>
    public SecurityPolicyAction Action { get; }
    /// <summary>Gets an allowlisted reason category; raw detector messages are never included.</summary>
    public string ReasonCode { get; }
    /// <summary>Gets counts of allowlisted categories; unknown categories are aggregated as OTHER.</summary>
    public IReadOnlyDictionary<string, int> Categories { get; }
    /// <summary>Gets the optional sanitized trace identifier correlating this host receipt with Cloud observations.</summary>
    public string? TraceId { get; }
    /// <summary>Gets the optional sanitized request identifier for this host decision.</summary>
    public string? RequestId { get; }
    /// <summary>Gets the optional UTC timestamp when the host decision occurred.</summary>
    public DateTimeOffset? OccurredAt { get; }
    /// <summary>Gets the optional sanitized policy identifier applied by the host.</summary>
    public string? PolicyId { get; }
    /// <summary>Gets the optional sanitized policy revision applied by the host.</summary>
    public string? PolicyRevision { get; }
    /// <summary>Gets the enforcement authority that produced this receipt; defaults to application-host.</summary>
    public string EnforcementAuthority { get; }
    /// <summary>Gets the deterministic SHA-256 hex digest computed over the canonical sanitized metadata fields.</summary>
    public string IntegrityDigest { get; }

    /// <summary>Creates bounded metadata without copying content, token values, entity offsets, or detector reasons.</summary>
    public static HostPrivacyEvidence Create(Guid eventId, EgressContext context, EgressDecision decision) =>
        Create(eventId, context, decision, traceId: null);

    /// <summary>Creates bounded metadata with optional sanitized correlation and policy receipt fields.</summary>
    public static HostPrivacyEvidence Create(
        Guid eventId,
        EgressContext context,
        EgressDecision decision,
        string? traceId,
        string? requestId = null,
        DateTimeOffset? occurredAt = null,
        string? policyId = null,
        string? policyRevision = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(decision);
        if (eventId == Guid.Empty || context.Tenant.Value == default || context.Principal.Value == default ||
            context.Session.Value == default || !Enum.IsDefined(context.Destination) ||
            !Enum.IsDefined(decision.Action) || decision.Allowed && decision.Action is SecurityPolicyAction.Block or SecurityPolicyAction.Review ||
            decision.DetectedEntities is null || decision.DetectedEntities.Count > 20000)
            throw new ArgumentException("Invalid privacy evidence.");
        var normalizedTraceId = ValidateOptionalIdentifier(traceId, 128, allowSlash: false);
        var normalizedRequestId = ValidateOptionalIdentifier(requestId, 128, allowSlash: false);
        var normalizedPolicyId = ValidateOptionalIdentifier(policyId, 128, allowSlash: true);
        var normalizedPolicyRevision = ValidateOptionalIdentifier(policyRevision, 64, allowSlash: false);
        DateTimeOffset? normalizedOccurredAt = occurredAt?.ToUniversalTime();

        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var entity in decision.DetectedEntities)
        {
            if (entity is null) throw new ArgumentException("Invalid privacy evidence.");
            var category = entity.EntityType is "EMAIL" or "PHONE" or "PAYMENT_CARD" or "SECRET" or "PERSONAL_DATA"
                ? entity.EntityType : "OTHER";
            counts.TryGetValue(category, out var count);
            counts[category] = checked(count + 1);
        }
        var reasonCode = decision.Allowed ? "allowed" : decision.Reason switch
        {
            "detector_failure" => "inspection_failed",
            "required_detector_unavailable" or "privacy_enforcement_unavailable" => "enforcement_unavailable",
            "structured_content_inspection" or "literal_sensitive_value_detected" or "sensitive_boundary_blocked"
                when counts.Count > 0 => "sensitive_detected",
            "structured_content_inspection" or "remote_egress_forbidden" => "policy_blocked",
            _ => "not_recorded",
        };
        var tenantId = context.Tenant.Value.ToString();
        var readOnlyCounts = new ReadOnlyDictionary<string, int>(counts);
        var digest = ComputeIntegrityDigest(
            eventId,
            tenantId,
            context.Destination,
            decision.Allowed,
            decision.Action,
            reasonCode,
            readOnlyCounts,
            DefaultEnforcementAuthority,
            normalizedTraceId,
            normalizedRequestId,
            normalizedOccurredAt,
            normalizedPolicyId,
            normalizedPolicyRevision);
        return new(
            eventId,
            tenantId,
            context.Destination,
            decision.Allowed,
            decision.Action,
            reasonCode,
            readOnlyCounts,
            normalizedTraceId,
            normalizedRequestId,
            normalizedOccurredAt,
            normalizedPolicyId,
            normalizedPolicyRevision,
            DefaultEnforcementAuthority,
            digest);
    }

    /// <summary>Computes the deterministic lowercase SHA-256 hex digest over canonical sanitized receipt metadata.</summary>
    public static string ComputeIntegrityDigest(
        Guid eventId,
        string tenantId,
        EgressDestination destination,
        bool allowed,
        SecurityPolicyAction action,
        string reasonCode,
        IReadOnlyDictionary<string, int> categories,
        string enforcementAuthority = DefaultEnforcementAuthority,
        string? traceId = null,
        string? requestId = null,
        DateTimeOffset? occurredAt = null,
        string? policyId = null,
        string? policyRevision = null)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(reasonCode);
        ArgumentNullException.ThrowIfNull(categories);
        ArgumentNullException.ThrowIfNull(enforcementAuthority);

        var sortedCategories = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var pair in categories)
        {
            sortedCategories[pair.Key] = pair.Value;
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("schemaVersion", "openconnect.privacy-evidence.v1");
            writer.WriteString("eventId", eventId.ToString("D"));
            writer.WriteString("tenantId", tenantId);
            writer.WriteString("destination", destination.ToString());
            writer.WriteBoolean("allowed", allowed);
            writer.WriteString("action", action.ToString());
            writer.WriteString("reasonCode", reasonCode);
            writer.WritePropertyName("categories");
            writer.WriteStartObject();
            foreach (var pair in sortedCategories)
            {
                writer.WriteNumber(pair.Key, pair.Value);
            }
            writer.WriteEndObject();
            writer.WriteString("enforcementAuthority", enforcementAuthority);
            if (traceId is not null) writer.WriteString("traceId", traceId);
            if (requestId is not null) writer.WriteString("requestId", requestId);
            if (occurredAt is not null) writer.WriteString("occurredAt", occurredAt.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            if (policyId is not null) writer.WriteString("policyId", policyId);
            if (policyRevision is not null) writer.WriteString("policyRevision", policyRevision);
            writer.WriteEndObject();
        }

        var hash = SHA256.HashData(stream.GetBuffer().AsSpan(0, (int)stream.Length));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string? ValidateOptionalIdentifier(string? value, int maxLength, bool allowSlash)
    {
        if (value is null) return null;
        if (value.Length == 0 || value.Length > maxLength)
            throw new ArgumentException("Invalid privacy evidence.");
        foreach (var ch in value)
        {
            var allowed = ch is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '.' or '_' or '-' or ':' ||
                          (allowSlash && ch == '/');
            if (!allowed)
                throw new ArgumentException("Invalid privacy evidence.");
        }
        return value;
    }
}
