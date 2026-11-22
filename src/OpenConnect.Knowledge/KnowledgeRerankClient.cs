using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using OpenConnect.Privacy;

namespace OpenConnect.Knowledge;

/// <summary>A candidate that the host has selected for an authorized retrieval scope.</summary>
public sealed class KnowledgeRerankCandidate
{
    /// <summary>Creates an immutable candidate; authorization is required before use.</summary>
    public KnowledgeRerankCandidate(string id, string text) { Id = id; Text = text; }
    /// <summary>Gets the authorized candidate identifier.</summary>
    public string Id { get; }
    /// <summary>Gets the sensitive candidate text.</summary>
    public string Text { get; }
}

/// <summary>Authorized, privacy-gated client for a private reranking worker.</summary>
public sealed class KnowledgeRerankClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly IEgressGate _gate;
    private readonly Uri _endpoint;
    private readonly string _token;
    private readonly byte[] _key;
    private readonly Func<EgressContext, IReadOnlyList<KnowledgeRerankCandidate>, CancellationToken, ValueTask<bool>> _authorize;
    private readonly bool _ownsHttp;
    private bool _disposed;

    /// <summary>The host authorizer must apply tenant and document ACLs before admitting candidates.</summary>
    public KnowledgeRerankClient(IEgressGate gate, Uri endpoint, string token, byte[] key,
        Func<EgressContext, IReadOnlyList<KnowledgeRerankCandidate>, CancellationToken, ValueTask<bool>> authorize,
        bool allowInsecureHttp = false)
        : this(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false }),
            gate, endpoint, token, key, authorize, allowInsecureHttp, true) { }

    internal KnowledgeRerankClient(HttpClient http, IEgressGate gate, Uri endpoint, string token, byte[] key,
        Func<EgressContext, IReadOnlyList<KnowledgeRerankCandidate>, CancellationToken, ValueTask<bool>> authorize,
        bool allowInsecureHttp = false, bool ownsHttp = false)
    {
        ArgumentNullException.ThrowIfNull(http); ArgumentNullException.ThrowIfNull(gate); ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(key); ArgumentNullException.ThrowIfNull(authorize);
        if (!endpoint.IsAbsoluteUri || !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !(endpoint.Scheme == Uri.UriSchemeHttps || allowInsecureHttp && endpoint.Scheme == Uri.UriSchemeHttp))
            throw new ArgumentException("A trusted worker endpoint is required.", nameof(endpoint));
        if (string.IsNullOrWhiteSpace(token) || token.Any(char.IsWhiteSpace) || key.Length < 32)
            throw new ArgumentException("Worker credentials are invalid.");
        _http = http; _gate = gate; _endpoint = new Uri(endpoint, "/internal/v1/execute"); _token = token;
        _key = (byte[])key.Clone(); _authorize = authorize; _ownsHttp = ownsHttp;
    }

    /// <summary>Returns a caller-owned result with raw model scores; scores are not calibrated probabilities.</summary>
    public async Task<JsonDocument> RerankAsync(EgressContext context, string query,
        IReadOnlyList<KnowledgeRerankCandidate> candidates, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(context); ArgumentNullException.ThrowIfNull(candidates);
        if (context.Tenant.Value == default || context.Principal.Value == default || context.Session.Value == default ||
            context.Destination != EgressDestination.Worker || string.IsNullOrWhiteSpace(query) || query.Length > 8192 || candidates.Count is < 1 or > 32)
            throw new ArgumentException("Invalid reranking request.");
        var snapshot = Array.AsReadOnly(candidates.ToArray());
        if (snapshot.Count is < 1 or > 32 || snapshot.Any(item => item is null || !KnowledgeIndexSubmission.Identifier(item.Id) ||
            string.IsNullOrWhiteSpace(item.Text) || item.Text.Length > 8192) || snapshot.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != snapshot.Count)
            throw new ArgumentException("Invalid reranking request.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(100));
        if (!await _authorize(context, snapshot, deadline.Token).ConfigureAwait(false)) throw new UnauthorizedAccessException("Reranking was not authorized.");
        await PrivacyJsonInspection.DemandGateUnchangedAsync(_gate, JsonSerializer.SerializeToElement(snapshot.Select(item => item.Id)), context, deadline.Token).ConfigureAwait(false);
        async Task<string> Sanitize(string text)
        {
            var decision = await _gate.EvaluateAsync(text, context, deadline.Token).ConfigureAwait(false);
            if (!WorkerEgressValidation.IsSafe(text, decision) || string.IsNullOrWhiteSpace(decision!.SanitizedContent) || decision.SanitizedContent.Length > 8192)
                throw new PrivacyBlockedException("Reranking was blocked by privacy policy.");
            return decision.SanitizedContent;
        }
        var safeQuery = await Sanitize(query).ConfigureAwait(false);
        var safeCandidates = new List<object>(snapshot.Count);
        var safeFragments = new List<string>(snapshot.Count + 1) { safeQuery };
        var totalCharacters = safeQuery.Length;
        var containsEncoded = safeQuery.Contains('%');
        foreach (var item in snapshot)
        {
            var text = await Sanitize(item.Text).ConfigureAwait(false);
            containsEncoded |= text.Contains('%');
            safeFragments.Add(text);
            totalCharacters += text.Length;
            if (totalCharacters > 65536) throw new ArgumentException("Invalid reranking request.");
            safeCandidates.Add(new { id = item.Id, text });
        }
        if (containsEncoded)
            await PrivacyJsonInspection.DemandGateUnchangedAsync(_gate, JsonSerializer.SerializeToElement(safeFragments), context, deadline.Token).ConfigureAwait(false);
        var taskId = Guid.NewGuid().ToString("N"); var now = DateTimeOffset.UtcNow;
        var body = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = "openconnect.v1", taskId, kind = "knowledge.rerank",
            tenantId = context.Tenant.Value.ToString(), issuedAt = now.AddSeconds(-5), expiresAt = now.AddMinutes(2),
            payload = new { query = safeQuery, candidates = safeCandidates } });
        byte[]? chunk = null; MemoryStream? buffer = null;
        try
        {
            if (body.Length > 128 * 1024) throw new ArgumentException("Invalid reranking request.");
            deadline.Token.ThrowIfCancellationRequested();
            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            request.Headers.Add("X-OpenConnect-Signature", Convert.ToHexString(HMACSHA256.HashData(_key, body)).ToLowerInvariant());
            request.Content = new ByteArrayContent(body); request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException("The reranking worker rejected the request.", null, response.StatusCode);
            if (response.Content.Headers.ContentLength > 64 * 1024) throw new InvalidOperationException("Invalid worker response.");
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            buffer = new MemoryStream(); chunk = new byte[8192]; int count;
            while ((count = await stream.ReadAsync(chunk, deadline.Token).ConfigureAwait(false)) > 0)
            { if (buffer.Length + count > 64 * 1024) throw new InvalidOperationException("Invalid worker response."); buffer.Write(chunk, 0, count); }
            buffer.Position = 0; var result = JsonDocument.Parse(buffer);
            try
            {
                Validate(result.RootElement, taskId, snapshot.Select((item, index) => (item.Id, index)).ToDictionary(item => item.Id, item => item.index, StringComparer.Ordinal));
                await PrivacyJsonInspection.DemandGateUnchangedAsync(_gate, result.RootElement, context with { Destination = EgressDestination.Output }, deadline.Token).ConfigureAwait(false);
                return result;
            }
            catch { result.Dispose(); throw; }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(body);
            if (chunk is not null) CryptographicOperations.ZeroMemory(chunk);
            if (buffer is not null) { if (buffer.TryGetBuffer(out var bytes)) CryptographicOperations.ZeroMemory(bytes.AsSpan()); buffer.Dispose(); }
        }
    }

    private static void Validate(JsonElement root, string taskId, Dictionary<string, int> ids)
    {
        try
        {
            Properties(root, "schemaVersion", "taskId", "kind", "result");
            if (root.GetProperty("schemaVersion").GetString() != "openconnect.v1" || root.GetProperty("taskId").GetString() != taskId || root.GetProperty("kind").GetString() != "knowledge.rerank") throw new InvalidOperationException();
            var result = root.GetProperty("result"); Properties(result, "candidates", "model_id", "fingerprint", "score_type");
            if (!KnowledgeIndexSubmission.Identifier(result.GetProperty("model_id").GetString()) || !KnowledgeIndexSubmission.Digest(result.GetProperty("fingerprint").GetString()) || result.GetProperty("score_type").GetString() != "raw_logit") throw new InvalidOperationException();
            var candidates = result.GetProperty("candidates");
            if (candidates.ValueKind != JsonValueKind.Array || candidates.GetArrayLength() != ids.Count) throw new InvalidOperationException();
            var previous = double.PositiveInfinity; var previousIndex = -1;
            foreach (var candidate in candidates.EnumerateArray())
            {
                Properties(candidate, "id", "score");
                var id = candidate.GetProperty("id").GetString();
                if (id is null || !ids.Remove(id, out var index) || !candidate.GetProperty("score").TryGetDouble(out var score) || !double.IsFinite(score) || score > previous || (score == previous && index <= previousIndex)) throw new InvalidOperationException();
                previous = score; previousIndex = index;
            }
            if (ids.Count != 0) throw new InvalidOperationException();
        }
        catch { throw new InvalidOperationException("Invalid worker response."); }
    }
    private static void Properties(JsonElement value, params string[] expected)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidOperationException();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject()) if (!expected.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name)) throw new InvalidOperationException();
        if (seen.Count != expected.Length) throw new InvalidOperationException();
    }
    /// <summary>Clears signing material and disposes the owned transport.</summary>
    public void Dispose() { if (_disposed) return; _disposed = true; CryptographicOperations.ZeroMemory(_key); if (_ownsHttp) _http.Dispose(); }
}
