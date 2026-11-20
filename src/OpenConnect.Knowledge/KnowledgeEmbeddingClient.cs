using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using OpenConnect.Privacy;

namespace OpenConnect.Knowledge;

/// <summary>A single normalized embedding and its truncation status.</summary>
public sealed record KnowledgeEmbedding(IReadOnlyList<float> Vector, bool Truncated, double Norm);

/// <summary>Embeddings and the model space to which they belong.</summary>
public sealed record KnowledgeEmbeddingResult(
    IReadOnlyList<KnowledgeEmbedding> Items, string ModelId, string Fingerprint, int Dimension, string SpaceId);

/// <summary>Private worker client; the caller must authorize the principal for the tenant.</summary>
public sealed class KnowledgeEmbeddingClient : IDisposable
{
    private const int MaximumResponseBytes = 8 * 1024 * 1024;
    private readonly HttpClient _httpClient;
    private readonly IEgressGate _egressGate;
    private readonly Uri _endpoint;
    private readonly string _bearerToken;
    private readonly byte[] _signingKey;
    private readonly EgressDestination _destination;
    private readonly bool _ownsHttpClient;
    private bool _disposed;

    /// <summary>Creates a client for an explicitly configured private worker.</summary>
    public KnowledgeEmbeddingClient(IEgressGate egressGate, Uri endpoint,
        string bearerToken, byte[] signingKey, EgressDestination destination = EgressDestination.ExternalModel,
        bool allowInsecureHttp = false)
        : this(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false }),
            egressGate, endpoint, bearerToken, signingKey, destination, allowInsecureHttp, ownsHttpClient: true)
    {
    }

    internal KnowledgeEmbeddingClient(HttpClient httpClient, IEgressGate egressGate, Uri endpoint,
        string bearerToken, byte[] signingKey, EgressDestination destination = EgressDestination.ExternalModel,
        bool allowInsecureHttp = false, bool ownsHttpClient = false)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(egressGate);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(signingKey);
        if (!endpoint.IsAbsoluteUri || !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !(endpoint.Scheme == Uri.UriSchemeHttps || (allowInsecureHttp && endpoint.Scheme == Uri.UriSchemeHttp)))
            throw new ArgumentException("A trusted worker endpoint is required.", nameof(endpoint));
        if (string.IsNullOrWhiteSpace(bearerToken) || bearerToken.Any(char.IsWhiteSpace))
            throw new ArgumentException("A worker bearer token is required.", nameof(bearerToken));
        if (signingKey.Length < 32)
            throw new ArgumentException("A signing key of at least 32 bytes is required.", nameof(signingKey));
        if (destination is not (EgressDestination.Worker or EgressDestination.ExternalModel))
            throw new ArgumentException("A worker or external-model destination is required.", nameof(destination));
        _httpClient = httpClient;
        _egressGate = egressGate;
        _endpoint = new Uri(endpoint, "/internal/v1/execute");
        _bearerToken = bearerToken;
        _signingKey = (byte[])signingKey.Clone();
        _destination = destination;
        _ownsHttpClient = ownsHttpClient;
    }

    /// <summary>Applies host privacy policy and submits a bounded embedding request.</summary>
    public async Task<KnowledgeEmbeddingResult> EmbedAsync(
        EgressContext context, string inputKind, IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(texts);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (context.Tenant.Value == default || context.Principal.Value == default || context.Session.Value == default ||
            context.Destination != _destination)
            throw new ArgumentException("An authenticated egress context matching the configured destination is required.", nameof(context));
        if (inputKind is not ("query" or "passage") || texts.Count is < 1 or > 32 ||
            texts.Any(text => string.IsNullOrEmpty(text) || text.Length > 8192) || texts.Sum(text => text.Length) > 65536)
            throw new ArgumentException("The embedding request is invalid.", nameof(texts));

        var safeTexts = new List<string>(texts.Count);
        foreach (var text in texts)
        {
            var decision = await _egressGate.EvaluateAsync(text, context, cancellationToken).ConfigureAwait(false);
            if (!decision.Allowed || string.IsNullOrEmpty(decision.SanitizedContent))
                throw new PrivacyBlockedException("Worker egress was denied by privacy policy.");
            safeTexts.Add(decision.SanitizedContent);
        }
        if (safeTexts.Any(text => text.Length > 8192) || safeTexts.Sum(text => text.Length) > 65536)
            throw new PrivacyBlockedException("Sanitized worker input exceeds the allowed size.");

        var now = DateTimeOffset.UtcNow;
        var taskId = Guid.NewGuid().ToString("N");
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = "openconnect.v1", taskId, kind = "knowledge.embed",
            tenantId = context.Tenant.Value.ToString(), issuedAt = now.AddSeconds(-5),
            expiresAt = now.AddMinutes(2), payload = new { inputKind, texts = safeTexts }
        });
        if (body.Length > 128 * 1024)
            throw new ArgumentException("The embedding request is too large.", nameof(texts));
        using var message = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _bearerToken);
        message.Headers.Add("X-OpenConnect-Signature", Convert.ToHexString(HMACSHA256.HashData(_signingKey, body)).ToLowerInvariant());
        message.Content = new ByteArrayContent(body);
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await _httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException("The knowledge worker rejected the request.", null, response.StatusCode);
        if (response.Content.Headers.ContentLength > MaximumResponseBytes)
            throw new InvalidOperationException("The knowledge worker response is too large.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + count > MaximumResponseBytes)
                throw new InvalidOperationException("The knowledge worker response is too large.");
            buffer.Write(chunk, 0, count);
        }
        using var document = JsonDocument.Parse(buffer.ToArray());
        var root = document.RootElement;
        EnsureProperties(root, "schemaVersion", "taskId", "kind", "result");
        if (root.GetProperty("schemaVersion").GetString() != "openconnect.v1" ||
            root.GetProperty("taskId").GetString() != taskId || root.GetProperty("kind").GetString() != "knowledge.embed")
            throw new InvalidOperationException("The knowledge worker response does not match the request.");
        var result = root.GetProperty("result");
        EnsureProperties(result, "items", "model_id", "fingerprint", "dimension", "space_id");
        var dimension = result.GetProperty("dimension").GetInt32();
        var modelId = result.GetProperty("model_id").GetString();
        var fingerprint = result.GetProperty("fingerprint").GetString();
        var spaceId = result.GetProperty("space_id").GetString();
        if (dimension is < 1 or > 8192 || string.IsNullOrWhiteSpace(modelId) ||
            !IsDigest(fingerprint) || !IsDigest(spaceId))
            throw new InvalidOperationException("The knowledge worker response metadata is invalid.");
        var items = new List<KnowledgeEmbedding>();
        foreach (var item in result.GetProperty("items").EnumerateArray())
        {
            if (items.Count >= texts.Count)
                throw new InvalidOperationException("The knowledge worker returned an invalid batch.");
            EnsureProperties(item, "vector", "truncated", "norm");
            var vector = item.GetProperty("vector").EnumerateArray().Select(value => value.GetSingle()).ToArray();
            var norm = item.GetProperty("norm").GetDouble();
            if (vector.Length != dimension || vector.Any(value => !float.IsFinite(value)) || !double.IsFinite(norm) || norm < 0)
                throw new InvalidOperationException("The knowledge worker returned an invalid embedding.");
            items.Add(new KnowledgeEmbedding(vector, item.GetProperty("truncated").GetBoolean(), norm));
        }
        if (items.Count != texts.Count)
            throw new InvalidOperationException("The knowledge worker returned an invalid batch.");
        return new KnowledgeEmbeddingResult(items, modelId, fingerprint!, dimension, spaceId!);
    }

    private static bool IsDigest(string? value) =>
        value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void EnsureProperties(JsonElement value, params string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("The knowledge worker response is invalid.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!allowed.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name))
                throw new InvalidOperationException("The knowledge worker response is invalid.");
        }
        if (seen.Count != allowed.Length)
            throw new InvalidOperationException("The knowledge worker response is invalid.");
    }

    /// <summary>Disposes the owned HTTP transport and clears the signing key.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        CryptographicOperations.ZeroMemory(_signingKey);
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }
}
