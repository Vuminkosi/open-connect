using System.Text.Json;

namespace OpenConnect.Docs.Cloud;

// A cloud-side decision observation, not an application-host receipt or a delivery record.
public sealed class CloudPrivacyEvidence
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ApplicationId { get; set; }
    public Guid RequestId { get; set; }
    public required string Outcome { get; set; }
    public required string TraceId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    // Null on historical observations: the earlier detector did not record categories.
    public string? CategoryCounts { get; set; }
}

public interface ICloudPrivacyEvidenceStore
{
    Task AppendAsync(CloudCredentialIdentity identity, Guid requestId, string outcome, string traceId,
        IReadOnlyList<CloudPrivacyCategoryCount> categories, CancellationToken cancellationToken);
}

public static class CloudPrivacyCategories
{
    public static string Encode(IReadOnlyList<CloudPrivacyCategoryCount> categories, string outcome)
    {
        ArgumentNullException.ThrowIfNull(categories);
        var values = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in categories)
        {
            if (item is null || item.Category is not ("EMAIL" or "PHONE" or "PAYMENT_CARD" or "SECRET" or "PERSONAL_DATA" or "OTHER") ||
                item.Count is < 1 or > 20000 || !values.TryAdd(item.Category, item.Count))
                throw new ArgumentException("Invalid Cloud privacy observation.");
        }
        if (values.Values.Sum(value => (long)value) > 20000 || outcome != "privacy_blocked" && values.Count > 0)
            throw new ArgumentException("Invalid Cloud privacy observation.");
        return JsonSerializer.Serialize(values);
    }

    public static IReadOnlyList<CloudPrivacyCategoryCount>? Decode(string? json, string outcome)
    {
        if (json is null) return null;
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException("Invalid Cloud privacy observation.");
        var items = document.RootElement.EnumerateObject()
            .Select(property => new CloudPrivacyCategoryCount(property.Name, property.Value.GetInt32())).ToArray();
        _ = Encode(items, outcome);
        return items;
    }
}
