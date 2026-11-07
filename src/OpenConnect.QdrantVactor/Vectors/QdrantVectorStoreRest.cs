using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OpenConnect.QdrantVactor;

/// <summary>Qdrant REST implementation using only the platform HTTP client.</summary>
public sealed class QdrantVectorStoreRest : IVectorStore
{
    private readonly HttpClient _httpClient;
    private readonly string _collectionName;
    private readonly int _vectorSize;

    public QdrantVectorStoreRest(HttpClient httpClient, Uri endpoint, string apiKey, string collectionName, int vectorSize)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);
        if (!endpoint.IsAbsoluteUri || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("Endpoint must be an absolute HTTP(S) URI.", nameof(endpoint));
        if (vectorSize <= 0) throw new ArgumentOutOfRangeException(nameof(vectorSize));

        _httpClient = httpClient;
        _httpClient.BaseAddress = endpoint.AbsoluteUri.EndsWith('/') ? endpoint : new Uri(endpoint.AbsoluteUri + "/");
        _httpClient.DefaultRequestHeaders.Remove("api-key");
        _httpClient.DefaultRequestHeaders.Add("api-key", apiKey);
        _collectionName = Uri.EscapeDataString(collectionName);
        _vectorSize = vectorSize;
    }

    public async Task UpsertAsync(VectorPoint point, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(point);
        ValidateVector(point.Vector.Span);
        var id = NormalizePointId(point.Id);
        var body = new
        {
            points = new[]
            {
                new
                {
                    id,
                    vector = point.Vector.ToArray(),
                    payload = new
                    {
                        tenant = point.TenantId,
                        agent = point.AgentId,
                        content = point.Content,
                        revision = point.Revision,
                        event_id = point.EventId,
                        session_id = point.SessionId,
                        privacy_class = point.PrivacyClass,
                        created_at = point.CreatedAt
                    }
                }
            }
        };
        using var response = await _httpClient.PutAsJsonAsync($"collections/{_collectionName}/points?wait=true", body, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<VectorPoint>> SearchAsync(string tenantId, string agentId, ReadOnlyMemory<float> vector, int limit, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        if (limit < 0) throw new ArgumentOutOfRangeException(nameof(limit));
        ValidateVector(vector.Span);
        var body = new
        {
            vector = vector.ToArray(), limit, with_payload = true, with_vector = true,
            filter = new { must = new[] { Match("tenant", tenantId), Match("agent", agentId) } }
        };
        using var response = await _httpClient.PostAsJsonAsync($"collections/{_collectionName}/points/search", body, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var points = new List<VectorPoint>();
        foreach (var result in document.RootElement.GetProperty("result").EnumerateArray())
        {
            var payload = result.GetProperty("payload");
            var resultTenant = payload.GetProperty("tenant").GetString();
            var resultAgent = payload.GetProperty("agent").GetString();
            // The server filter is not an authorization boundary; reject malformed or cross-scope results too.
            if (!string.Equals(resultTenant, tenantId, StringComparison.Ordinal) || !string.Equals(resultAgent, agentId, StringComparison.Ordinal)) continue;
            var values = result.GetProperty("vector").EnumerateArray().Select(item => item.GetSingle()).ToArray();
            if (values.Length != _vectorSize) continue;
            points.Add(new VectorPoint(result.GetProperty("id").GetString() ?? string.Empty,
                resultTenant!, resultAgent!, payload.TryGetProperty("content", out var content) ? content.GetString() ?? string.Empty : string.Empty,
                values, payload.TryGetProperty("revision", out var revision) ? revision.GetInt64() : 0,
                payload.TryGetProperty("event_id", out var eventId) ? eventId.GetString() : null,
                payload.TryGetProperty("session_id", out var sessionId) ? sessionId.GetString() : null,
                payload.TryGetProperty("privacy_class", out var privacyClass) ? privacyClass.GetString() : "Sanitized",
                payload.TryGetProperty("created_at", out var createdAt) && createdAt.ValueKind == JsonValueKind.String
                    ? createdAt.GetDateTimeOffset() : null,
                result.TryGetProperty("score", out var score) && score.ValueKind == JsonValueKind.Number ? score.GetDouble() : null));
        }
        return points;
    }

    public async Task DeleteAsync(string tenantId, string agentId, string documentId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        var body = new
        {
            filter = new
            {
                must = new object[] { Match("tenant", tenantId), Match("agent", agentId), new { has_id = new[] { NormalizePointId(documentId) } } }
            }
        };
        using var response = await _httpClient.PostAsJsonAsync($"collections/{_collectionName}/points/delete?wait=true", body, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    private void ValidateVector(ReadOnlySpan<float> vector)
    {
        if (vector.Length != _vectorSize) throw new ArgumentException("Vector dimension does not match configured collection size.", nameof(vector));
        foreach (var value in vector)
            if (!float.IsFinite(value)) throw new ArgumentException("Vector values must be finite.", nameof(vector));
    }

    private static object Match(string key, string value) => new { key, match = new { value } };

    private static Guid NormalizePointId(string id)
    {
        if (Guid.TryParse(id, out var guid)) return guid;
        // Existing indexer IDs are deterministic SHA-256 hex. Derive a stable RFC 4122-shaped UUID from them.
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(id));
        Span<byte> uuid = stackalloc byte[16];
        bytes.AsSpan(0, 16).CopyTo(uuid);
        uuid[6] = (byte)((uuid[6] & 0x0f) | 0x50);
        uuid[8] = (byte)((uuid[8] & 0x3f) | 0x80);
        return new Guid(uuid, bigEndian: true);
    }
}
