using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenConnect.Abstractions;

/// <summary>Host policy decision recorded in structured security evidence.</summary>
[JsonConverter(typeof(SnakeCaseStringEnumConverter<SecurityEvidenceDecision>))]
public enum SecurityEvidenceDecision
{
    Allow,
    Mask,
    Redact,
    Block,
    Review,
    Quarantine,
    Unavailable
}

/// <summary>Actual execution outcome observed at the enforcement boundary.</summary>
[JsonConverter(typeof(SnakeCaseStringEnumConverter<SecurityEnforcementOutcome>))]
public enum SecurityEnforcementOutcome
{
    NotExecuted,
    Executed,
    Prevented,
    Unknown
}

/// <summary>Result of attempting to record a security evidence event.</summary>
public sealed record SecurityEvidenceRecordResult(bool Recorded, bool Deduplicated, string? ReasonCode = null);

/// <summary>Sink for recording content-free, tenant-bound security evidence events.</summary>
public interface ISecurityEvidenceSink
{
    /// <summary>Records an evidence event idempotently, deduplicating by (TenantId, ApplicationId, EventId).</summary>
    ValueTask<SecurityEvidenceRecordResult> RecordAsync(SecurityEvidenceEvent evidence, CancellationToken cancellationToken = default);
}

/// <summary>
/// Content-free, host-bound security evidence event (<c>openconnect.security-evidence.v1</c>).
/// Strictly separates inspection status, policy decision, and actual enforcement outcome.
/// </summary>
public sealed class SecurityEvidenceEvent
{
    private const int MaxCategoryCount = 64;

    private SecurityEvidenceEvent(
        Guid eventId,
        string requestId,
        string? traceId,
        string tenantId,
        string applicationId,
        DateTimeOffset occurredAt,
        string capability,
        string boundary,
        string detectorId,
        string detectorVersion,
        string policyRevision,
        SecurityInspectionStatus inspectionStatus,
        SecurityEvidenceDecision decision,
        SecurityEnforcementOutcome enforcementOutcome,
        string reasonCode,
        IReadOnlyDictionary<string, int> categoryCounts,
        long durationMs,
        string integrityDigest)
    {
        EventId = eventId;
        RequestId = requestId;
        TraceId = traceId;
        TenantId = tenantId;
        ApplicationId = applicationId;
        OccurredAt = occurredAt;
        Capability = capability;
        Boundary = boundary;
        DetectorId = detectorId;
        DetectorVersion = detectorVersion;
        PolicyRevision = policyRevision;
        InspectionStatus = inspectionStatus;
        Decision = decision;
        EnforcementOutcome = enforcementOutcome;
        ReasonCode = reasonCode;
        CategoryCounts = categoryCounts;
        DurationMs = durationMs;
        IntegrityDigest = integrityDigest;
    }

    public string SchemaVersion => SecurityContractVersions.EvidenceV1;
    public Guid EventId { get; }
    public string RequestId { get; }
    public string? TraceId { get; }
    public string TenantId { get; }
    public string ApplicationId { get; }
    public DateTimeOffset OccurredAt { get; }
    public string Capability { get; }
    public string Boundary { get; }
    public string DetectorId { get; }
    public string DetectorVersion { get; }
    public string PolicyRevision { get; }
    public SecurityInspectionStatus InspectionStatus { get; }
    public SecurityEvidenceDecision Decision { get; }
    public SecurityEnforcementOutcome EnforcementOutcome { get; }
    public string ReasonCode { get; }
    public IReadOnlyDictionary<string, int> CategoryCounts { get; }
    public long DurationMs { get; }
    public string IntegrityDigest { get; }

    /// <summary>Creates a validated, content-free security evidence event bound to host tenant and application identity.</summary>
    public static SecurityEvidenceEvent Create(
        Guid eventId,
        string requestId,
        string tenantId,
        string applicationId,
        DateTimeOffset occurredAt,
        string capability,
        string boundary,
        string detectorId,
        string detectorVersion,
        string policyRevision,
        SecurityInspectionStatus inspectionStatus,
        SecurityEvidenceDecision decision,
        SecurityEnforcementOutcome enforcementOutcome,
        string reasonCode,
        IReadOnlyDictionary<string, int>? categoryCounts = null,
        long durationMs = 0,
        string? traceId = null)
    {
        if (eventId == Guid.Empty)
            throw new ArgumentException("EventId must not be empty.", nameof(eventId));
        var normalizedRequestId = ValidateIdentifier(requestId, nameof(requestId), 128);
        var normalizedTenantId = ValidateIdentifier(tenantId, nameof(tenantId), 128);
        var normalizedApplicationId = ValidateIdentifier(applicationId, nameof(applicationId), 128);
        var normalizedCapability = ValidateIdentifier(capability, nameof(capability), 64);
        var normalizedBoundary = ValidateIdentifier(boundary, nameof(boundary), 64);
        var normalizedDetectorId = ValidateIdentifier(detectorId, nameof(detectorId), 64);
        var normalizedDetectorVersion = ValidateIdentifier(detectorVersion, nameof(detectorVersion), 128);
        var normalizedPolicyRevision = ValidateIdentifier(policyRevision, nameof(policyRevision), 64);
        var normalizedReasonCode = ValidateIdentifier(reasonCode, nameof(reasonCode), 64);
        var normalizedTraceId = traceId is null ? null : ValidateIdentifier(traceId, nameof(traceId), 128);

        if (!Enum.IsDefined(inspectionStatus))
            throw new ArgumentException("Invalid inspection status.", nameof(inspectionStatus));
        if (!Enum.IsDefined(decision))
            throw new ArgumentException("Invalid decision.", nameof(decision));
        if (!Enum.IsDefined(enforcementOutcome))
            throw new ArgumentException("Invalid enforcement outcome.", nameof(enforcementOutcome));
        if (durationMs < 0)
            throw new ArgumentOutOfRangeException(nameof(durationMs), "DurationMs cannot be negative.");

        // Enforce semantic consistency: a block or quarantine decision must never claim executed.
        if (decision is SecurityEvidenceDecision.Block or SecurityEvidenceDecision.Quarantine &&
            enforcementOutcome == SecurityEnforcementOutcome.Executed)
        {
            throw new ArgumentException("A blocked or quarantined decision cannot report an executed enforcement outcome.", nameof(enforcementOutcome));
        }

        var sortedCounts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        if (categoryCounts is not null)
        {
            if (categoryCounts.Count > MaxCategoryCount)
                throw new ArgumentException($"CategoryCounts cannot exceed {MaxCategoryCount} entries.", nameof(categoryCounts));
            foreach (var pair in categoryCounts)
            {
                var key = ValidateIdentifier(pair.Key, nameof(categoryCounts), 64);
                if (pair.Value < 0)
                    throw new ArgumentOutOfRangeException(nameof(categoryCounts), "Category count cannot be negative.");
                sortedCounts[key] = pair.Value;
            }
        }

        var utcOccurredAt = occurredAt.ToUniversalTime();
        var readOnlyCounts = new ReadOnlyDictionary<string, int>(sortedCounts);
        var digest = ComputeIntegrityDigest(
            eventId,
            normalizedRequestId,
            normalizedTraceId,
            normalizedTenantId,
            normalizedApplicationId,
            utcOccurredAt,
            normalizedCapability,
            normalizedBoundary,
            normalizedDetectorId,
            normalizedDetectorVersion,
            normalizedPolicyRevision,
            inspectionStatus,
            decision,
            enforcementOutcome,
            normalizedReasonCode,
            readOnlyCounts,
            durationMs);

        return new SecurityEvidenceEvent(
            eventId,
            normalizedRequestId,
            normalizedTraceId,
            normalizedTenantId,
            normalizedApplicationId,
            utcOccurredAt,
            normalizedCapability,
            normalizedBoundary,
            normalizedDetectorId,
            normalizedDetectorVersion,
            normalizedPolicyRevision,
            inspectionStatus,
            decision,
            enforcementOutcome,
            normalizedReasonCode,
            readOnlyCounts,
            durationMs,
            digest);
    }

    /// <summary>Computes the deterministic SHA-256 hex digest over canonical sanitized evidence fields.</summary>
    public static string ComputeIntegrityDigest(
        Guid eventId,
        string requestId,
        string? traceId,
        string tenantId,
        string applicationId,
        DateTimeOffset occurredAt,
        string capability,
        string boundary,
        string detectorId,
        string detectorVersion,
        string policyRevision,
        SecurityInspectionStatus inspectionStatus,
        SecurityEvidenceDecision decision,
        SecurityEnforcementOutcome enforcementOutcome,
        string reasonCode,
        IReadOnlyDictionary<string, int> categoryCounts,
        long durationMs)
    {
        ArgumentNullException.ThrowIfNull(categoryCounts);
        var sorted = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var pair in categoryCounts)
            sorted[pair.Key] = pair.Value;

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("schemaVersion", SecurityContractVersions.EvidenceV1);
            writer.WriteString("eventId", eventId.ToString("D"));
            writer.WriteString("requestId", requestId);
            if (traceId is not null) writer.WriteString("traceId", traceId);
            writer.WriteString("tenantId", tenantId);
            writer.WriteString("applicationId", applicationId);
            writer.WriteString("occurredAt", occurredAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            writer.WriteString("capability", capability);
            writer.WriteString("boundary", boundary);
            writer.WriteString("detectorId", detectorId);
            writer.WriteString("detectorVersion", detectorVersion);
            writer.WriteString("policyRevision", policyRevision);
            writer.WriteString("inspectionStatus", SnakeCaseNamingPolicy.Instance.ConvertName(inspectionStatus.ToString()));
            writer.WriteString("decision", SnakeCaseNamingPolicy.Instance.ConvertName(decision.ToString()));
            writer.WriteString("enforcementOutcome", SnakeCaseNamingPolicy.Instance.ConvertName(enforcementOutcome.ToString()));
            writer.WriteString("reasonCode", reasonCode);
            writer.WritePropertyName("categoryCounts");
            writer.WriteStartObject();
            foreach (var pair in sorted)
                writer.WriteNumber(pair.Key, pair.Value);
            writer.WriteEndObject();
            writer.WriteNumber("durationMs", durationMs);
            writer.WriteEndObject();
        }

        var hash = SHA256.HashData(stream.GetBuffer().AsSpan(0, (int)stream.Length));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string ValidateIdentifier(string? value, string paramName, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length > maxLength)
            throw new ArgumentException($"Invalid content-free identifier for {paramName}.", paramName);
        foreach (var ch in value)
        {
            var allowed = ch is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '.' or '_' or '-' or ':';
            if (!allowed)
                throw new ArgumentException($"Identifier '{paramName}' contains disallowed characters.", paramName);
        }
        return value;
    }
}
