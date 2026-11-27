using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenConnect.Privacy;

namespace OpenConnect.Knowledge;

/// <summary>Trusted-host client for tenant-scoped knowledge search.</summary>
public sealed class KnowledgeSearchClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly IEgressGate _gate;
    private readonly Func<EgressContext, string, string, IReadOnlyList<string>, CancellationToken, ValueTask<bool>> _authorize;
    private readonly Uri _endpoint;
    private readonly string _token;
    private readonly byte[] _key;
    private readonly bool _ownsHttp;
    private bool _disposed;

    /// <summary>The host must authenticate the principal and authorize the exact scope and roles before signing.</summary>
    public KnowledgeSearchClient(IEgressGate gate, Uri endpoint, string token, byte[] key,
        Func<EgressContext, string, string, IReadOnlyList<string>, CancellationToken, ValueTask<bool>> authorize,
        bool allowInsecureHttp = false)
        : this(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false }),
            gate, endpoint, token, key, authorize, allowInsecureHttp, true)
    {
    }

    internal KnowledgeSearchClient(HttpClient http, IEgressGate gate, Uri endpoint, string token, byte[] key,
        Func<EgressContext, string, string, IReadOnlyList<string>, CancellationToken, ValueTask<bool>> authorize,
        bool allowInsecureHttp = false, bool ownsHttp = false)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(authorize);
        if (!endpoint.IsAbsoluteUri || !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !(endpoint.Scheme == Uri.UriSchemeHttps || (allowInsecureHttp && endpoint.Scheme == Uri.UriSchemeHttp)))
            throw new ArgumentException("A trusted worker endpoint is required.", nameof(endpoint));
        if (string.IsNullOrWhiteSpace(token) || token.Any(char.IsWhiteSpace) || key.Length < 32)
            throw new ArgumentException("Worker credentials are invalid.");
        _http = http;
        _gate = gate;
        _endpoint = new Uri(endpoint, "/internal/v1/execute");
        _token = token;
        _key = (byte[])key.Clone();
        _authorize = authorize;
        _ownsHttp = ownsHttp;
    }

    /// <summary>Searches an authorized scope; the caller owns the returned sensitive JSON document.</summary>
    public Task<JsonDocument> SearchAsync(EgressContext context, string applicationId, string collectionId,
        IReadOnlyList<string> roleIds, string query, int limit = 10, CancellationToken cancellationToken = default)
        => SearchCoreAsync(context, applicationId, collectionId, roleIds, query, null, limit, cancellationToken);

    /// <summary>
    /// Runs hybrid search with an approved query embedder. The host must ensure the provider uses the supplied
    /// sanitized query and a matching index model space. The caller owns the returned sensitive JSON document.
    /// </summary>
    public Task<JsonDocument> SearchHybridAsync(EgressContext context, string applicationId, string collectionId,
        IReadOnlyList<string> roleIds, string query,
        Func<EgressContext, string, CancellationToken, Task<KnowledgeEmbeddingResult>> embedQuery,
        int limit = 10, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(embedQuery);
        return SearchCoreAsync(context, applicationId, collectionId, roleIds, query, embedQuery, limit, cancellationToken);
    }

    private async Task<JsonDocument> SearchCoreAsync(EgressContext context, string applicationId, string collectionId,
        IReadOnlyList<string> roleIds, string query,
        Func<EgressContext, string, CancellationToken, Task<KnowledgeEmbeddingResult>>? embedQuery,
        int limit, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(roleIds);
        if (roleIds.Count > 64)
            throw new ArgumentException("The search request is invalid.");
        var roles = Array.AsReadOnly(roleIds.ToArray());
        if (context.Tenant.Value == default || context.Principal.Value == default || context.Session.Value == default ||
            context.Destination != EgressDestination.Worker)
            throw new ArgumentException("An authenticated local worker context is required.", nameof(context));
        if (!ValidIdentifier(applicationId) || !ValidIdentifier(collectionId) || roles.Count > 64 ||
            roles.Any(role => !ValidIdentifier(role)) || string.IsNullOrWhiteSpace(query) || query.Length > 8192 ||
            limit is < 1 or > 32)
            throw new ArgumentException("The search request is invalid.");
        if (!await _authorize(context, applicationId, collectionId, roles, cancellationToken).ConfigureAwait(false))
            throw new UnauthorizedAccessException("Knowledge search was not authorized.");
        // Scope identifiers are sent to the worker and must remain exact for authorization.
        // A transformation cannot be applied without changing their meaning.
        var authorizedScope = JsonSerializer.SerializeToElement(new { applicationId, collectionId, roleIds = roles });
        await PrivacyJsonInspection.DemandGateUnchangedAsync(_gate, authorizedScope, context, cancellationToken).ConfigureAwait(false);
        var decision = await _gate.EvaluateAsync(query, context, cancellationToken).ConfigureAwait(false);
        if (!WorkerEgressValidation.IsSafe(query, decision) || string.IsNullOrWhiteSpace(decision!.SanitizedContent) || decision.SanitizedContent.Length > 8192)
            throw new PrivacyBlockedException("Knowledge search egress was denied by privacy policy.");
        if (decision.SanitizedContent.Contains('%'))
            await PrivacyJsonInspection.DemandGateUnchangedAsync(_gate, JsonSerializer.SerializeToElement(decision.SanitizedContent), context, cancellationToken).ConfigureAwait(false);

        object? hybrid = null;
        if (embedQuery is not null)
        {
            var embedding = await embedQuery(context, decision.SanitizedContent, cancellationToken).ConfigureAwait(false);
            if (embedding is null || embedding.Items is null || embedding.Items.Count != 1 || embedding.Items[0] is null ||
                embedding.Items[0].Truncated || embedding.Dimension is < 1 or > 8192 ||
                !ValidIdentifier(embedding.ModelId) || !Digest(embedding.Fingerprint) || !Digest(embedding.SpaceId) ||
                embedding.Items[0].Vector is null || embedding.Items[0].Vector.Count != embedding.Dimension)
                throw new InvalidOperationException("The query embedding is invalid.");
            var vector = embedding.Items[0].Vector.ToArray();
            var norm = Math.Sqrt(vector.Sum(value => (double)value * value));
            if (vector.Any(value => !float.IsFinite(value)) || !double.IsFinite(norm) || Math.Abs(norm - 1) > 0.001 ||
                !double.IsFinite(embedding.Items[0].Norm) || Math.Abs(embedding.Items[0].Norm - norm) > 0.001)
                throw new InvalidOperationException("The query embedding is invalid.");
            hybrid = new
            {
                vector,
                embeddingSpaceId = embedding.SpaceId,
                modelId = embedding.ModelId,
                fingerprint = embedding.Fingerprint
            };
        }

        var now = DateTimeOffset.UtcNow;
        var taskId = Guid.NewGuid().ToString("N");
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = "openconnect.v1",
            taskId,
            kind = "knowledge.search",
            tenantId = context.Tenant.Value.ToString(),
            issuedAt = now.AddSeconds(-5),
            expiresAt = now.AddMinutes(2),
            payload = new
            {
                applicationId,
                collectionId,
                principalId = context.Principal.Value.ToString(),
                roleIds = roles,
                query = decision.SanitizedContent,
                limit,
                hybrid
            }
        });
        try
        {
            if (body.Length > 128 * 1024)
                throw new ArgumentException("The search request is too large.");
            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            request.Headers.Add("X-OpenConnect-Signature", Convert.ToHexString(HMACSHA256.HashData(_key, body)).ToLowerInvariant());
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(100));
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException("The knowledge worker rejected the request.", null, response.StatusCode);
            const int maximumResponseBytes = 1024 * 1024;
            if (response.Content.Headers.ContentLength > maximumResponseBytes)
                throw new InvalidOperationException("Invalid worker response.");
            await using var input = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            try
            {
                int count;
                while ((count = await input.ReadAsync(chunk, deadline.Token).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + count > maximumResponseBytes)
                        throw new InvalidOperationException("Invalid worker response.");
                    buffer.Write(chunk, 0, count);
                }
                buffer.Position = 0;
                var document = JsonDocument.Parse(buffer);
                try
                {
                    Validate(document.RootElement, taskId, limit);
                    var outputContext = context with { Destination = EgressDestination.Output };
                    await PrivacyJsonInspection.DemandGateUnchangedAsync(_gate, document.RootElement, outputContext, deadline.Token).ConfigureAwait(false);
                    return document;
                }
                catch { document.Dispose(); throw; }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(chunk);
                if (buffer.TryGetBuffer(out var bytes))
                    CryptographicOperations.ZeroMemory(bytes.AsSpan());
            }
        }
        finally { CryptographicOperations.ZeroMemory(body); }
    }

    private static void Validate(JsonElement root, string taskId, int limit)
    {
        Properties(root, "schemaVersion", "taskId", "kind", "result");
        if (root.GetProperty("schemaVersion").GetString() != "openconnect.v1" ||
            root.GetProperty("taskId").GetString() != taskId || root.GetProperty("kind").GetString() != "knowledge.search")
            Invalid();
        var result = root.GetProperty("result");
        Properties(result, "hits", "model_id", "fingerprint", "score_type", "warnings");
        if (result.GetProperty("score_type").GetString() != "raw_logit")
            Invalid();
        var hits = result.GetProperty("hits");
        if (hits.ValueKind != JsonValueKind.Array || hits.GetArrayLength() > limit)
            Invalid();
        var model = result.GetProperty("model_id");
        var fingerprint = result.GetProperty("fingerprint");
        if (hits.GetArrayLength() > 0 && (!ValidIdentifier(model.GetString()) || !Digest(fingerprint.GetString())))
            Invalid();
        if ((model.ValueKind == JsonValueKind.Null) != (fingerprint.ValueKind == JsonValueKind.Null))
            Invalid();
        if (model.ValueKind != JsonValueKind.Null && (!ValidIdentifier(model.GetString()) || !Digest(fingerprint.GetString())))
            Invalid();
        var seen = new HashSet<(string, string)>();
        foreach (var ranked in hits.EnumerateArray())
        {
            Properties(ranked, "hit", "rerank_score");
            if (!Finite(ranked.GetProperty("rerank_score")))
                Invalid();
            var hit = ranked.GetProperty("hit");
            Properties(hit, "document_id", "source_revision", "chunk_id", "page_id", "page_index", "start", "end", "text", "score");
            var document = hit.GetProperty("document_id").GetString();
            var chunk = hit.GetProperty("chunk_id").GetString();
            var text = hit.GetProperty("text").GetString();
            if (!ValidIdentifier(document) || !Digest(chunk) || !ValidIdentifier(hit.GetProperty("source_revision").GetString()) ||
                !ValidIdentifier(hit.GetProperty("page_id").GetString()) || string.IsNullOrEmpty(text) || text.EnumerateRunes().Count() > 8192 ||
                !hit.GetProperty("page_index").TryGetInt32(out var page) || page is < 0 or > 29 ||
                !hit.GetProperty("start").TryGetInt32(out var start) || !hit.GetProperty("end").TryGetInt32(out var end) ||
                start < 0 || end <= start || end - start != text.EnumerateRunes().Count() || !Finite(hit.GetProperty("score")) ||
                !seen.Add((document!, chunk!)))
                Invalid();
        }
        var warnings = result.GetProperty("warnings");
        if (warnings.ValueKind != JsonValueKind.Array || warnings.GetArrayLength() > 1 ||
            warnings.EnumerateArray().Any(item => item.GetString() != "access_or_source_changed"))
            Invalid();
    }

    private static bool ValidIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        try
        { _ = new UTF8Encoding(false, true).GetByteCount(value); }
        catch (EncoderFallbackException) { return false; }
        return value.EnumerateRunes().Count() <= 256 && value.EnumerateRunes().All(rune => rune.Value >= 32 && rune.Value != 127);
    }
    private static bool Digest(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool Finite(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number);
    private static void Properties(JsonElement value, params string[] expected)
    {
        if (value.ValueKind != JsonValueKind.Object)
            Invalid();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!expected.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name))
                Invalid();
        if (seen.Count != expected.Length)
            Invalid();
    }
    private static void Invalid() => throw new InvalidOperationException("Invalid worker response.");

    /// <summary>Clears the signing key and disposes an owned transport.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        CryptographicOperations.ZeroMemory(_key);
        if (_ownsHttp)
            _http.Dispose();
    }
}
