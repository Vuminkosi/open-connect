using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenConnect.Abstractions;
using OpenConnect.Privacy;

namespace OpenConnect.Docs.Cloud;

/// <summary>Validated, content-free host evidence bound to an authenticated Cloud application.</summary>
public sealed record CloudHostPrivacyReport(Guid EventId, string HostTenantId, EgressDestination Destination,
    bool Allowed, SecurityPolicyAction Action, string CategoryCounts)
{
    public string ReasonCode { get; init; } = "not_recorded";
    public string? TraceId { get; init; }
    public string? RequestId { get; init; }
    public DateTimeOffset? OccurredAt { get; init; }
    public string? PolicyId { get; init; }
    public string? PolicyRevision { get; init; }
    public string EnforcementAuthority { get; init; } = "application-host";
    public string? IntegrityDigest { get; init; }
}

public static class CloudHostPrivacyReportParser
{
    private const string ExpectedEnforcementAuthority = "application-host";

    /// <summary>Rejects unknown fields, duplicate fields, invalid categories, mismatched tenant identities, and tampered digests.</summary>
    public static CloudHostPrivacyReport Parse(JsonElement body, CloudCredentialIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (identity.TenantId == Guid.Empty || identity.ApplicationId == Guid.Empty || body.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Invalid host privacy evidence.");
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in body.EnumerateObject())
            if (property.Name is not ("schemaVersion" or "eventId" or "tenantId" or "destination" or "allowed" or "action" or "reasonCode" or "categories"
                    or "traceId" or "requestId" or "occurredAt" or "policyId" or "policyRevision" or "enforcementAuthority" or "integrityDigest") ||
                !fields.TryAdd(property.Name, property.Value)) throw new ArgumentException("Invalid host privacy evidence.");
        try
        {
            if (fields.Count < 7 || fields["schemaVersion"].GetString() != "openconnect.privacy-evidence.v1" ||
                !fields["eventId"].TryGetGuid(out var eventId) || eventId == Guid.Empty ||
                fields["tenantId"].GetString() != CloudTenantIdentity.ToHostTenantId(identity.TenantId).Value.ToString() ||
                !Enum.TryParse<EgressDestination>(fields["destination"].GetString(), false, out var destination) || !Enum.IsDefined(destination) ||
                fields["destination"].GetString() != destination.ToString() ||
                fields["allowed"].ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                !Enum.TryParse<SecurityPolicyAction>(fields["action"].GetString(), false, out var action) || !Enum.IsDefined(action) ||
                fields["action"].GetString() != action.ToString() || fields["categories"].ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Invalid host privacy evidence.");
            var allowed = fields["allowed"].GetBoolean();
            if (allowed && action is SecurityPolicyAction.Block or SecurityPolicyAction.Review)
                throw new ArgumentException("Invalid host privacy evidence.");
            var reason = fields.TryGetValue("reasonCode", out var reasonValue) ? reasonValue.GetString() : "not_recorded";
            if (reason is not ("allowed" or "inspection_failed" or "enforcement_unavailable" or "sensitive_detected" or "policy_blocked" or "not_recorded") ||
                reason != "not_recorded" && (reason == "allowed") != allowed)
                throw new ArgumentException("Invalid host privacy evidence.");
            var counts = Categories(fields["categories"]);
            if (reason == "sensitive_detected" && counts.Count == 0)
                throw new ArgumentException("Invalid host privacy evidence.");

            var traceId = ParseOptionalIdentifier(fields, "traceId", 128, allowSlash: false);
            var requestId = ParseOptionalIdentifier(fields, "requestId", 128, allowSlash: false);
            var occurredAt = ParseOptionalTimestamp(fields, "occurredAt");
            var policyId = ParseOptionalIdentifier(fields, "policyId", 128, allowSlash: true);
            var policyRevision = ParseOptionalIdentifier(fields, "policyRevision", 64, allowSlash: false);
            var enforcementAuthority = ParseOptionalAuthority(fields);
            var integrityDigest = ParseOptionalDigest(fields);

            var hostTenantId = fields["tenantId"].GetString()!;
            if (integrityDigest is not null)
            {
                var expectedDigest = HostPrivacyEvidence.ComputeIntegrityDigest(
                    eventId,
                    hostTenantId,
                    destination,
                    allowed,
                    action,
                    reason,
                    counts,
                    enforcementAuthority,
                    traceId,
                    requestId,
                    occurredAt,
                    policyId,
                    policyRevision);
                if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(integrityDigest),
                    Encoding.ASCII.GetBytes(expectedDigest)))
                {
                    throw new ArgumentException("Invalid host privacy evidence.");
                }
            }

            return new(eventId, hostTenantId, destination, allowed, action, JsonSerializer.Serialize(counts))
            {
                ReasonCode = reason,
                TraceId = traceId,
                RequestId = requestId,
                OccurredAt = occurredAt,
                PolicyId = policyId,
                PolicyRevision = policyRevision,
                EnforcementAuthority = enforcementAuthority,
                IntegrityDigest = integrityDigest,
            };
        }
        catch (Exception exception) when (exception is InvalidOperationException or KeyNotFoundException or FormatException)
        { throw new ArgumentException("Invalid host privacy evidence."); }
    }

    internal static SortedDictionary<string, int> Categories(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException("Invalid host privacy evidence.");
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var category in value.EnumerateObject())
            if (category.Name is not ("EMAIL" or "PHONE" or "PAYMENT_CARD" or "SECRET" or "PERSONAL_DATA" or "OTHER") ||
                !category.Value.TryGetInt32(out var count) || count is < 1 or > 20000 || !counts.TryAdd(category.Name, count))
                throw new ArgumentException("Invalid host privacy evidence.");
        if (counts.Values.Sum(value => (long)value) > 20000) throw new ArgumentException("Invalid host privacy evidence.");
        return counts;
    }

    private static string? ParseOptionalIdentifier(Dictionary<string, JsonElement> fields, string name, int maxLength, bool allowSlash)
    {
        if (!fields.TryGetValue(name, out var element) || element.ValueKind == JsonValueKind.Null) return null;
        if (element.ValueKind != JsonValueKind.String) throw new ArgumentException("Invalid host privacy evidence.");
        var value = element.GetString();
        if (string.IsNullOrEmpty(value) || value.Length > maxLength) throw new ArgumentException("Invalid host privacy evidence.");
        foreach (var ch in value)
        {
            var valid = ch is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '.' or '_' or '-' or ':' ||
                        (allowSlash && ch == '/');
            if (!valid) throw new ArgumentException("Invalid host privacy evidence.");
        }
        return value;
    }

    private static DateTimeOffset? ParseOptionalTimestamp(Dictionary<string, JsonElement> fields, string name)
    {
        if (!fields.TryGetValue(name, out var element) || element.ValueKind == JsonValueKind.Null) return null;
        if (element.ValueKind != JsonValueKind.String) throw new ArgumentException("Invalid host privacy evidence.");
        var raw = element.GetString();
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 64 ||
            !DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            throw new ArgumentException("Invalid host privacy evidence.");
        return parsed.ToUniversalTime();
    }

    private static string ParseOptionalAuthority(Dictionary<string, JsonElement> fields)
    {
        if (!fields.TryGetValue("enforcementAuthority", out var element) || element.ValueKind == JsonValueKind.Null)
            return ExpectedEnforcementAuthority;
        if (element.ValueKind != JsonValueKind.String || element.GetString() != ExpectedEnforcementAuthority)
            throw new ArgumentException("Invalid host privacy evidence.");
        return ExpectedEnforcementAuthority;
    }

    private static string? ParseOptionalDigest(Dictionary<string, JsonElement> fields)
    {
        if (!fields.TryGetValue("integrityDigest", out var element) || element.ValueKind == JsonValueKind.Null) return null;
        if (element.ValueKind != JsonValueKind.String) throw new ArgumentException("Invalid host privacy evidence.");
        var value = element.GetString();
        if (value is null || value.Length != 64) throw new ArgumentException("Invalid host privacy evidence.");
        foreach (var ch in value)
        {
            if (ch is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
                throw new ArgumentException("Invalid host privacy evidence.");
        }
        return value;
    }
}
