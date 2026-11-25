using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using OpenConnect.Abstractions;
using OpenConnect.Privacy;

namespace OpenConnect.Docs.Cloud;

/// <summary>
/// Strict content-free parser for <c>openconnect.security-evidence.v1</c> payloads.
/// Rejects unknown properties (including any raw PII, prompt, body, header, or secret fields)
/// and binds tenant and application identity strictly from authenticated host/cloud credentials.
/// </summary>
public static class CloudSecurityEvidenceParser
{
    private static readonly HashSet<string> AllowedProperties = new(StringComparer.Ordinal)
    {
        "schemaVersion",
        "eventId",
        "requestId",
        "traceId",
        "occurredAt",
        "capability",
        "boundary",
        "detectorId",
        "detectorVersion",
        "policyRevision",
        "inspectionStatus",
        "decision",
        "enforcementOutcome",
        "reasonCode",
        "categoryCounts",
        "durationMs",
    };

    private static readonly HashSet<string> AllowedCapabilities = new(StringComparer.Ordinal)
    {
        "pii",
        "malware",
        "image-privacy",
        "prompt-injection",
        "waf",
        "service-fencing",
        "database-fencing",
        "agent-tool",
    };

    private static readonly HashSet<string> AllowedBoundaries = new(StringComparer.Ordinal)
    {
        "text-ingress",
        "text-egress",
        "upload-release",
        "image-egress",
        "prompt-ingress",
        "tool-invocation",
        "tool-result",
        "http-egress",
        "gateway-ingress",
        "database-query",
    };

    /// <summary>
    /// Parses and validates a content-free <c>openconnect.security-evidence.v1</c> JSON object
    /// and binds it to the authenticated <paramref name="identity"/>.
    /// </summary>
    public static SecurityEvidenceEvent Parse(JsonElement body, CloudCredentialIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (identity.TenantId == Guid.Empty || identity.ApplicationId == Guid.Empty)
            throw new ArgumentException("Authenticated credential identity is invalid.");
        return ParseForBoundIdentity(body, identity.TenantId.ToString("D"), identity.ApplicationId.ToString("D"));
    }

    internal static SecurityEvidenceEvent ParseForBoundIdentity(JsonElement body, string boundTenantId, string boundApplicationId)
    {
        if (string.IsNullOrWhiteSpace(boundTenantId) || string.IsNullOrWhiteSpace(boundApplicationId))
            throw new ArgumentException("Bound tenant and application identifiers are required.");
        if (body.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Security evidence payload must be a JSON object.");

        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in body.EnumerateObject())
        {
            if (!AllowedProperties.Contains(property.Name) || !fields.TryAdd(property.Name, property.Value))
                throw new ArgumentException($"Security evidence contains disallowed or duplicate property '{property.Name}'.");
        }

        try
        {
            if (GetRequiredString(fields, "schemaVersion") != SecurityContractVersions.EvidenceV1)
                throw new ArgumentException("Unsupported security evidence schemaVersion.");

            if (!Guid.TryParseExact(GetRequiredString(fields, "eventId"), "D", out var eventId) || eventId == Guid.Empty)
                throw new ArgumentException("Invalid eventId.");

            var requestIdRaw = GetRequiredString(fields, "requestId");
            if (!Guid.TryParseExact(requestIdRaw, "D", out var requestGuid) || requestGuid == Guid.Empty)
                throw new ArgumentException("Invalid requestId.");

            string? traceId = null;
            if (fields.TryGetValue("traceId", out var traceElement) && traceElement.ValueKind != JsonValueKind.Null)
            {
                if (traceElement.ValueKind != JsonValueKind.String)
                    throw new ArgumentException("Invalid traceId.");
                traceId = traceElement.GetString();
                if (string.IsNullOrEmpty(traceId) || traceId.Length > 128)
                    throw new ArgumentException("Invalid traceId.");
            }

            var occurredAtRaw = GetRequiredString(fields, "occurredAt");
            if (occurredAtRaw.Length > 64 ||
                !DateTimeOffset.TryParse(occurredAtRaw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var occurredAt))
                throw new ArgumentException("Invalid occurredAt timestamp.");

            var capability = GetRequiredString(fields, "capability");
            if (!AllowedCapabilities.Contains(capability))
                throw new ArgumentException("Invalid capability.");

            var boundary = GetRequiredString(fields, "boundary");
            if (!AllowedBoundaries.Contains(boundary))
                throw new ArgumentException("Invalid boundary.");

            var detectorId = GetRequiredString(fields, "detectorId");
            var detectorVersion = GetRequiredString(fields, "detectorVersion");
            var policyRevision = GetRequiredString(fields, "policyRevision");
            var reasonCode = GetRequiredString(fields, "reasonCode");
            if (reasonCode.Length > 64 || !IsLowerReasonCode(reasonCode))
                throw new ArgumentException("Invalid reasonCode.");

            var inspectionStatus = ParseInspectionStatus(GetRequiredString(fields, "inspectionStatus"));
            var decision = ParseDecision(GetRequiredString(fields, "decision"));
            var outcome = ParseOutcome(GetRequiredString(fields, "enforcementOutcome"));

            var counts = ParseCategoryCounts(fields);

            if (!fields.TryGetValue("durationMs", out var durationElement) ||
                durationElement.ValueKind != JsonValueKind.Number ||
                !durationElement.TryGetInt64(out var durationMs) ||
                durationMs is < 0 or > 3_600_000)
                throw new ArgumentException("Invalid durationMs.");

            return SecurityEvidenceEvent.Create(
                eventId: eventId,
                requestId: requestGuid.ToString("D"),
                tenantId: boundTenantId,
                applicationId: boundApplicationId,
                occurredAt: occurredAt.ToUniversalTime(),
                capability: capability,
                boundary: boundary,
                detectorId: detectorId,
                detectorVersion: detectorVersion,
                policyRevision: policyRevision,
                inspectionStatus: inspectionStatus,
                decision: decision,
                enforcementOutcome: outcome,
                reasonCode: reasonCode,
                categoryCounts: counts,
                durationMs: durationMs,
                traceId: traceId);
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException or FormatException or ArgumentOutOfRangeException)
        {
            throw new ArgumentException("Invalid security evidence payload.", ex);
        }
    }

    private static string GetRequiredString(Dictionary<string, JsonElement> fields, string name)
    {
        if (!fields.TryGetValue(name, out var element) || element.ValueKind != JsonValueKind.String)
            throw new ArgumentException($"Missing or invalid '{name}'.");
        var value = element.GetString();
        if (string.IsNullOrEmpty(value))
            throw new ArgumentException($"Empty '{name}'.");
        return value;
    }

    private static SortedDictionary<string, int> ParseCategoryCounts(Dictionary<string, JsonElement> fields)
    {
        if (!fields.TryGetValue("categoryCounts", out var element) || element.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Missing or invalid categoryCounts.");
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var prop in element.EnumerateObject())
        {
            if (counts.Count >= 32 || prop.Name.Length is < 1 or > 64 || !IsCategoryKey(prop.Name))
                throw new ArgumentException("Invalid categoryCounts key.");
            if (prop.Value.ValueKind != JsonValueKind.Number || !prop.Value.TryGetInt32(out var count) || count is < 0 or > 100_000)
                throw new ArgumentException("Invalid categoryCounts value.");
            if (!counts.TryAdd(prop.Name, count))
                throw new ArgumentException("Duplicate categoryCounts key.");
        }
        return counts;
    }

    private static bool IsLowerReasonCode(string value)
    {
        foreach (var ch in value)
        {
            if (ch is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '.' or '-'))
                return false;
        }
        return true;
    }

    private static bool IsCategoryKey(string value)
    {
        foreach (var ch in value)
        {
            if (ch is not ((>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '.' or '-'))
                return false;
        }
        return true;
    }

    private static SecurityInspectionStatus ParseInspectionStatus(string value) => value switch
    {
        "completed" => SecurityInspectionStatus.Completed,
        "partial" => SecurityInspectionStatus.Partial,
        "degraded" => SecurityInspectionStatus.Degraded,
        "unavailable" => SecurityInspectionStatus.Unavailable,
        "failed" => SecurityInspectionStatus.Failed,
        "limit_exceeded" => SecurityInspectionStatus.LimitExceeded,
        "unsupported" => SecurityInspectionStatus.Unsupported,
        "skipped" or "not_run" => SecurityInspectionStatus.NotRun,
        _ => throw new ArgumentException($"Unsupported inspectionStatus '{value}'."),
    };

    private static SecurityEvidenceDecision ParseDecision(string value) => value switch
    {
        "allow" => SecurityEvidenceDecision.Allow,
        "mask" => SecurityEvidenceDecision.Mask,
        "redact" => SecurityEvidenceDecision.Redact,
        "block" => SecurityEvidenceDecision.Block,
        "quarantine" => SecurityEvidenceDecision.Quarantine,
        "review" => SecurityEvidenceDecision.Review,
        "unavailable" => SecurityEvidenceDecision.Unavailable,
        _ => throw new ArgumentException($"Unsupported decision '{value}'."),
    };

    private static SecurityEnforcementOutcome ParseOutcome(string value) => value switch
    {
        "not_executed" => SecurityEnforcementOutcome.NotExecuted,
        "executed" => SecurityEnforcementOutcome.Executed,
        "prevented" => SecurityEnforcementOutcome.Prevented,
        "unknown" => SecurityEnforcementOutcome.Unknown,
        _ => throw new ArgumentException($"Unsupported enforcementOutcome '{value}'."),
    };
}

/// <summary>Filter parameters for tenant-scoped <c>openconnect.security-evidence.v1</c> queries.</summary>
public sealed record CloudSecurityEvidenceFilter(
    string? Capability = null,
    SecurityEvidenceDecision? Decision = null,
    SecurityEnforcementOutcome? Outcome = null,
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null,
    string? TraceId = null,
    int Skip = 0,
    int Take = 50);

/// <summary>Tenant-scoped page of validated <c>openconnect.security-evidence.v1</c> events.</summary>
public sealed record CloudSecurityEvidencePage(
    IReadOnlyList<SecurityEvidenceEvent> Events,
    int TotalMatching,
    bool HasMore);

/// <summary>Store for content-free <c>openconnect.security-evidence.v1</c> events with tenant isolation and deduplication.</summary>
public interface ICloudSecurityEvidenceStore : ISecurityEvidenceSink
{
    CloudSecurityEvidencePage Query(Guid tenantId, CloudSecurityEvidenceFilter filter);
    SecurityEvidenceEvent? GetById(Guid tenantId, Guid eventId);
}

/// <summary>
/// Thread-safe store for <c>openconnect.security-evidence.v1</c> events backed by <see cref="InMemorySecurityEvidenceSink"/>.
/// Deduplicates by <c>(TenantId, ApplicationId, EventId)</c> and enforces strict tenant scoping on all reads.
/// </summary>
public sealed class CloudSecurityEvidenceStore : ICloudSecurityEvidenceStore
{
    private readonly InMemorySecurityEvidenceSink _sink = new(maxStoredEvents: 10_000);

    public ValueTask<SecurityEvidenceRecordResult> RecordAsync(SecurityEvidenceEvent evidence, CancellationToken cancellationToken = default)
        => _sink.RecordAsync(evidence, cancellationToken);

    public CloudSecurityEvidencePage Query(Guid tenantId, CloudSecurityEvidenceFilter filter)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("TenantId is required.", nameof(tenantId));
        ArgumentNullException.ThrowIfNull(filter);

        var skip = Math.Max(0, filter.Skip);
        var take = Math.Clamp(filter.Take, 1, 100);
        var tenantKey = tenantId.ToString("D");

        // Fetch bounded window for tenant from sink and apply additive filters.
        var allForTenant = _sink.Query(tenantKey, capability: filter.Capability, skip: 0, take: 500);
        IEnumerable<SecurityEvidenceEvent> filtered = allForTenant;

        if (filter.Decision is { } decision)
            filtered = filtered.Where(e => e.Decision == decision);
        if (filter.Outcome is { } outcome)
            filtered = filtered.Where(e => e.EnforcementOutcome == outcome);
        if (filter.FromUtc is { } fromUtc)
            filtered = filtered.Where(e => e.OccurredAt >= fromUtc.ToUniversalTime());
        if (filter.ToUtc is { } toUtc)
            filtered = filtered.Where(e => e.OccurredAt < toUtc.ToUniversalTime());
        if (!string.IsNullOrWhiteSpace(filter.TraceId))
            filtered = filtered.Where(e => string.Equals(e.TraceId, filter.TraceId.Trim(), StringComparison.Ordinal));

        var materialized = filtered.ToArray();
        var pageItems = materialized.Skip(skip).Take(take).ToArray();
        return new CloudSecurityEvidencePage(pageItems, materialized.Length, skip + pageItems.Length < materialized.Length);
    }

    public SecurityEvidenceEvent? GetById(Guid tenantId, Guid eventId)
    {
        if (tenantId == Guid.Empty || eventId == Guid.Empty)
            return null;
        var tenantKey = tenantId.ToString("D");
        var all = _sink.Query(tenantKey, skip: 0, take: 500);
        return all.FirstOrDefault(e => e.EventId == eventId);
    }
}

/// <summary>Tenant-authorized query service for <c>openconnect.security-evidence.v1</c> events.</summary>
public sealed class CloudSecurityEvidenceQueryService(
    ICloudSecurityEvidenceStore store,
    IDbContextFactory<CloudControlPlaneDbContext>? dbFactory = null)
{
    public async Task<bool> IsAuthorizedForTenantAsync(CloudSubject subject, Guid tenantId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subject);
        if (tenantId == Guid.Empty || dbFactory is null)
            return false;

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var role = await db.Memberships.AsNoTracking()
            .Where(item => item.TenantId == tenantId && item.SubjectIssuer == subject.Issuer && item.SubjectId == subject.SubjectId)
            .Select(item => item.Role)
            .SingleOrDefaultAsync(cancellationToken);
        return role is "Owner" or "Administrator" or "SecurityAnalyst";
    }

    public async Task<CloudSecurityEvidencePage?> QueryTenantAsync(
        CloudSubject subject,
        Guid tenantId,
        CloudSecurityEvidenceFilter filter,
        CancellationToken cancellationToken)
    {
        if (!await IsAuthorizedForTenantAsync(subject, tenantId, cancellationToken))
            return null;
        return store.Query(tenantId, filter);
    }

    public async Task<SecurityEvidenceEvent?> GetEventAsync(
        CloudSubject subject,
        Guid tenantId,
        Guid eventId,
        CancellationToken cancellationToken)
    {
        if (!await IsAuthorizedForTenantAsync(subject, tenantId, cancellationToken))
            return null;
        return store.GetById(tenantId, eventId);
    }
}

/// <summary>JSON serialization options for content-free <c>openconnect.security-evidence.v1</c> API responses.</summary>
internal static class CloudSecurityEvidenceJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
