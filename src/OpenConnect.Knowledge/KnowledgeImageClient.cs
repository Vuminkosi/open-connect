using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenConnect.Privacy;

namespace OpenConnect.Knowledge;

/// <summary>Orchestrates authorized local vision embeddings and tenant-scoped image indexing.</summary>
public sealed class KnowledgeImageClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly IEgressGate _gate;
    private readonly Uri _endpoint;
    private readonly string _token;
    private readonly byte[] _key;
    private readonly VisionEmbeddingClient _vision;
    private readonly Func<EgressContext, KnowledgeImageSubmission, ReadOnlyMemory<byte>, CancellationToken, ValueTask<bool>> _authorizeWrite;
    private readonly Func<EgressContext, string, string, IReadOnlyList<string>, CancellationToken, ValueTask<bool>> _authorizeSearch;
    private readonly bool _ownsHttp;
    private bool _disposed;

    /// <summary>The host authorizers must enforce tenant, principal, document and scope permissions.</summary>
    public KnowledgeImageClient(IEgressGate gate, Uri endpoint, string token, byte[] key, VisionEmbeddingClient vision,
        Func<EgressContext, KnowledgeImageSubmission, ReadOnlyMemory<byte>, CancellationToken, ValueTask<bool>> authorizeWrite,
        Func<EgressContext, string, string, IReadOnlyList<string>, CancellationToken, ValueTask<bool>> authorizeSearch,
        bool allowInsecureHttp = false)
        : this(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false }),
            gate, endpoint, token, key, vision, authorizeWrite, authorizeSearch, allowInsecureHttp, true)
    {
    }

    internal KnowledgeImageClient(HttpClient http, IEgressGate gate, Uri endpoint, string token, byte[] key, VisionEmbeddingClient vision,
        Func<EgressContext, KnowledgeImageSubmission, ReadOnlyMemory<byte>, CancellationToken, ValueTask<bool>> authorizeWrite,
        Func<EgressContext, string, string, IReadOnlyList<string>, CancellationToken, ValueTask<bool>> authorizeSearch,
        bool allowInsecureHttp = false, bool ownsHttp = false)
    {
        ArgumentNullException.ThrowIfNull(http); ArgumentNullException.ThrowIfNull(gate); ArgumentNullException.ThrowIfNull(endpoint); ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(vision); ArgumentNullException.ThrowIfNull(authorizeWrite);
        ArgumentNullException.ThrowIfNull(authorizeSearch);
        if (!endpoint.IsAbsoluteUri || !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !(endpoint.Scheme == Uri.UriSchemeHttps || (allowInsecureHttp && endpoint.Scheme == Uri.UriSchemeHttp)))
            throw new ArgumentException("A trusted worker endpoint is required.", nameof(endpoint));
        if (string.IsNullOrWhiteSpace(token) || token.Any(char.IsWhiteSpace) || key.Length < 32)
            throw new ArgumentException("Worker credentials are invalid.");
        _http = http; _gate = gate; _endpoint = new Uri(endpoint, "/internal/v1/execute"); _token = token; _key = (byte[])key.Clone();
        _vision = vision; _authorizeWrite = authorizeWrite; _authorizeSearch = authorizeSearch; _ownsHttp = ownsHttp;
    }

    /// <summary>Authorizes, scans and embeds image bytes, then stores the resulting vector under the parent ACL.</summary>
    public async Task UpsertAsync(EgressContext context, KnowledgeImageSubmission submission, ReadOnlyMemory<byte> image,
        CancellationToken cancellationToken = default)
    {
        ValidateContext(context);
        ArgumentNullException.ThrowIfNull(submission);
        if (image.Length is < 1 or > 10 * 1024 * 1024) throw new ArgumentException("Invalid image size.", nameof(image));
        var copy = image.ToArray();
        try
        {
            if (!await _authorizeWrite(context, submission, copy, cancellationToken).ConfigureAwait(false))
                throw new UnauthorizedAccessException("Image indexing was not authorized.");
            await DemandMetadataAsync(context, submission, cancellationToken).ConfigureAwait(false);
            var embedding = await _vision.EmbedImageAsync(context, copy, cancellationToken).ConfigureAwait(false);
            var payload = new { applicationId = submission.ApplicationId, collectionId = submission.CollectionId,
                documentId = submission.DocumentId, sourceRevision = submission.SourceRevision,
                expectedGeneration = submission.ExpectedGeneration, imageId = submission.ImageId,
                imageRevision = submission.ImageRevision, expectedImageRevision = submission.ExpectedImageRevision,
                vector = embedding.Items[0].Vector, embeddingSpaceId = embedding.SpaceId, modelId = embedding.ModelId,
                fingerprint = embedding.Fingerprint };
            using var response = await SendAsync(context, "knowledge.image.upsert", payload, 0, cancellationToken).ConfigureAwait(false);
        }
        finally { CryptographicOperations.ZeroMemory(copy); }
    }

    /// <summary>
    /// Authorizes and reads an exact version from the host's private MinIO bucket, verifies size and digest,
    /// then scans, embeds and indexes it. The resolver must use the supplied key and version without substitution.
    /// </summary>
    public async Task UpsertObjectAsync(EgressContext context, KnowledgeImageSubmission submission,
        KnowledgeImageObjectReference reference,
        Func<EgressContext, KnowledgeImageSubmission, KnowledgeImageObjectReference, CancellationToken, ValueTask<bool>> authorizeObject,
        Func<string, string, CancellationToken, Task<Stream>> openVersion,
        CancellationToken cancellationToken = default)
    {
        ValidateContext(context);
        ArgumentNullException.ThrowIfNull(submission);
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(authorizeObject);
        ArgumentNullException.ThrowIfNull(openVersion);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        if (!await authorizeObject(context, submission, reference, deadline.Token).ConfigureAwait(false))
            throw new UnauthorizedAccessException("Image object access was not authorized.");
        await DemandMetadataAsync(context, new { submission, reference }, deadline.Token).ConfigureAwait(false);
        var tenant = context.Tenant.Value.ToString();
        var tenantHash = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(tenant))).ToLowerInvariant();
        var objectKey = $"v1/tenants/{tenantHash}/documents/{reference.ObjectId}";
        var bytes = new byte[reference.ObjectSize + 1];
        try
        {
            await using var stream = await openVersion(objectKey, reference.ObjectVersion, deadline.Token).ConfigureAwait(false);
            if (stream is null || !stream.CanRead) throw new InvalidOperationException("Image object is unavailable.");
            var offset = 0;
            int count;
            while (offset < bytes.Length && (count = await stream.ReadAsync(bytes.AsMemory(offset), deadline.Token).ConfigureAwait(false)) > 0)
                offset += count;
            if (offset != reference.ObjectSize || !CryptographicOperations.FixedTimeEquals(
                    SHA256.HashData(bytes.AsSpan(0, offset)), Convert.FromHexString(reference.ObjectSha256)))
                throw new InvalidOperationException("Image object integrity verification failed.");
            await UpsertAsync(context, submission, bytes.AsMemory(0, offset), deadline.Token).ConfigureAwait(false);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    /// <summary>Searches an authorized scope using a privacy-gated text query; the caller owns the result.</summary>
    public async Task<JsonDocument> SearchAsync(EgressContext context, string applicationId, string collectionId,
        IReadOnlyList<string> roleIds, string query, int limit = 10, CancellationToken cancellationToken = default)
    {
        ValidateContext(context);
        ArgumentNullException.ThrowIfNull(roleIds);
        if (roleIds.Count > 64) throw new ArgumentException("The image search request is invalid.");
        var roles = Array.AsReadOnly(roleIds.ToArray());
        if (!KnowledgeIndexSubmission.Identifier(applicationId) || !KnowledgeIndexSubmission.Identifier(collectionId) ||
            roles.Count > 64 || roles.Any(value => !KnowledgeIndexSubmission.Identifier(value)) ||
            string.IsNullOrWhiteSpace(query) || query.Length > 8192 || limit is < 1 or > 100)
            throw new ArgumentException("The image search request is invalid.");
        if (!await _authorizeSearch(context, applicationId, collectionId, roles, cancellationToken).ConfigureAwait(false))
            throw new UnauthorizedAccessException("Image search was not authorized.");
        await DemandMetadataAsync(context, new { applicationId, collectionId, roles }, cancellationToken).ConfigureAwait(false);
        var embedding = await _vision.EmbedTextAsync(context, query, cancellationToken).ConfigureAwait(false);
        var payload = new { applicationId, collectionId, principalId = context.Principal.Value.ToString(), roleIds = roles,
            limit, vector = embedding.Items[0].Vector, embeddingSpaceId = embedding.SpaceId,
            modelId = embedding.ModelId, fingerprint = embedding.Fingerprint };
        return await SendAsync(context, "knowledge.image.search", payload, limit, cancellationToken).ConfigureAwait(false);
    }

    private void ValidateContext(EgressContext context)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(context);
        if (context.Tenant.Value == default || context.Principal.Value == default || context.Session.Value == default ||
            context.Destination != EgressDestination.Worker)
            throw new ArgumentException("An authenticated local worker context is required.", nameof(context));
    }

    private async Task<JsonDocument> SendAsync(EgressContext context, string kind, object payload, int limit,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var taskId = Guid.NewGuid().ToString("N");
        var body = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = "openconnect.v1", taskId, kind,
            tenantId = context.Tenant.Value.ToString(), issuedAt = now.AddSeconds(-5), expiresAt = now.AddMinutes(2), payload });
        if (body.Length > 128 * 1024) throw new ArgumentException("The image index request is too large.");
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        request.Headers.Add("X-OpenConnect-Signature", Convert.ToHexString(HMACSHA256.HashData(_key, body)).ToLowerInvariant());
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(100));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("The image index rejected the request.", null, response.StatusCode);
        const int maximum = 128 * 1024;
        if (response.Content.Headers.ContentLength > maximum) throw new InvalidOperationException("Invalid worker response.");
        await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int count;
        while ((count = await input.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + count > maximum) throw new InvalidOperationException("Invalid worker response.");
            buffer.Write(chunk, 0, count);
        }
        var document = JsonDocument.Parse(buffer.ToArray());
        try
        {
            Validate(document.RootElement, taskId, kind, limit);
            await PrivacyJsonInspection.DemandGateUnchangedAsync(_gate, document.RootElement,
                context with { Destination = EgressDestination.Output }, timeout.Token).ConfigureAwait(false);
            return document;
        }
        catch { document.Dispose(); throw; }
    }

    private Task DemandMetadataAsync(EgressContext context, object metadata, CancellationToken cancellationToken) =>
        PrivacyJsonInspection.DemandGateUnchangedAsync(_gate, JsonSerializer.SerializeToElement(metadata), context, cancellationToken);

    private static void Validate(JsonElement root, string taskId, string kind, int limit)
    {
        Properties(root, "schemaVersion", "taskId", "kind", "result");
        if (root.GetProperty("schemaVersion").GetString() != "openconnect.v1" || root.GetProperty("taskId").GetString() != taskId ||
            root.GetProperty("kind").GetString() != kind) Invalid();
        var result = root.GetProperty("result");
        if (kind == "knowledge.image.upsert")
        {
            Properties(result, "stored");
            if (result.GetProperty("stored").ValueKind != JsonValueKind.True) Invalid();
            return;
        }
        Properties(result, "hits");
        var hits = result.GetProperty("hits");
        if (hits.ValueKind != JsonValueKind.Array || hits.GetArrayLength() > limit) Invalid();
        var seen = new HashSet<(string, string)>();
        foreach (var hit in hits.EnumerateArray())
        {
            Properties(hit, "document_id", "image_id", "image_revision", "score");
            var document = hit.GetProperty("document_id").GetString();
            var image = hit.GetProperty("image_id").GetString();
            var score = hit.GetProperty("score");
            if (!KnowledgeIndexSubmission.Identifier(document) || !KnowledgeIndexSubmission.Identifier(image) ||
                !KnowledgeIndexSubmission.Identifier(hit.GetProperty("image_revision").GetString()) ||
                score.ValueKind != JsonValueKind.Number || !score.TryGetDouble(out var value) ||
                !double.IsFinite(value) || Math.Abs(value) > 1.001 || !seen.Add((document!, image!))) Invalid();
        }
    }

    private static void Properties(JsonElement value, params string[] expected)
    {
        if (value.ValueKind != JsonValueKind.Object) Invalid();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!expected.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name)) Invalid();
        if (seen.Count != expected.Length) Invalid();
    }
    private static void Invalid() => throw new InvalidOperationException("Invalid worker response.");

    /// <summary>Clears signing material and disposes the owned transport; the caller owns the vision client.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CryptographicOperations.ZeroMemory(_key);
        if (_ownsHttp) _http.Dispose();
    }
}
