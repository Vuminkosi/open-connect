using System.Text.Json;
using OpenConnect.Abstractions;
using OpenConnect.Privacy;

namespace OpenConnect.Docs.Cloud;

/// <summary>Validated, content-free host evidence bound to an authenticated Cloud application.</summary>
public sealed record CloudHostPrivacyReport(Guid EventId, string HostTenantId, EgressDestination Destination,
    bool Allowed, SecurityPolicyAction Action, string CategoryCounts)
{
    public string ReasonCode { get; init; } = "not_recorded";
}

public static class CloudHostPrivacyReportParser
{
    /// <summary>Rejects unknown fields, duplicate fields, invalid categories and mismatched tenant identities.</summary>
    public static CloudHostPrivacyReport Parse(JsonElement body, CloudCredentialIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (identity.TenantId == Guid.Empty || identity.ApplicationId == Guid.Empty || body.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Invalid host privacy evidence.");
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in body.EnumerateObject())
            if (property.Name is not ("schemaVersion" or "eventId" or "tenantId" or "destination" or "allowed" or "action" or "reasonCode" or "categories") ||
                !fields.TryAdd(property.Name, property.Value)) throw new ArgumentException("Invalid host privacy evidence.");
        try
        {
            if (fields.Count is < 7 or > 8 || fields["schemaVersion"].GetString() != "openconnect.privacy-evidence.v1" ||
                !fields["eventId"].TryGetGuid(out var eventId) || eventId == Guid.Empty ||
                fields["tenantId"].GetString() != CloudTenantIdentity.ToHostTenantId(identity.TenantId).Value.ToString() ||
                !Enum.TryParse<EgressDestination>(fields["destination"].GetString(), false, out var destination) || !Enum.IsDefined(destination) ||
                fields["destination"].GetString() != destination.ToString() ||
                fields["allowed"].ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                !Enum.TryParse<SecurityPolicyAction>(fields["action"].GetString(), false, out var action) || !Enum.IsDefined(action) ||
                fields["action"].GetString() != action.ToString() || fields["categories"].ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Invalid host privacy evidence.");
            if (fields["allowed"].GetBoolean() && action is SecurityPolicyAction.Block or SecurityPolicyAction.Review)
                throw new ArgumentException("Invalid host privacy evidence.");
            var reason = fields.TryGetValue("reasonCode", out var reasonValue) ? reasonValue.GetString() : "not_recorded";
            if (reason is not ("allowed" or "inspection_failed" or "enforcement_unavailable" or "sensitive_detected" or "policy_blocked" or "not_recorded") ||
                reason != "not_recorded" && (reason == "allowed") != fields["allowed"].GetBoolean())
                throw new ArgumentException("Invalid host privacy evidence.");
            var counts = Categories(fields["categories"]);
            if (reason == "sensitive_detected" && counts.Count == 0)
                throw new ArgumentException("Invalid host privacy evidence.");
            return new(eventId, fields["tenantId"].GetString()!, destination, fields["allowed"].GetBoolean(), action, JsonSerializer.Serialize(counts)) { ReasonCode = reason };
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

}
