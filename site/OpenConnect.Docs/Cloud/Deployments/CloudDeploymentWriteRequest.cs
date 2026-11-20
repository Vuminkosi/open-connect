using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenConnect.Docs.Cloud;

public sealed record CloudDeploymentWriteRequest(string? CapabilityId, string? EndpointReference, string? Transport,
    string? Region, string? ContractVersion, string? ModelId, string? ModelRevision, string? ModelDigest,
    string? ImageDigest, int ConcurrencyLimit, bool Enabled);

public static class CloudDeploymentRequestReader
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static async Task<CloudDeploymentWriteRequest?> ReadAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (!request.HasJsonContentType() || request.Headers.ContainsKey("Content-Encoding") || request.ContentLength > 8192) return null;
        var buffer = new byte[8193];
        try
        {
            var length = 0;
            while (length < buffer.Length)
            {
                var count = await request.Body.ReadAsync(buffer.AsMemory(length), cancellationToken);
                if (count == 0) break;
                length += count;
            }
            if (length > 8192) return null;
            using var document = JsonDocument.Parse(buffer.AsMemory(0, length));
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in document.RootElement.EnumerateObject())
                if (!names.Add(property.Name)) return null;
            return document.RootElement.Deserialize<CloudDeploymentWriteRequest>(Options);
        }
        catch (JsonException) { return null; }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }
}
