using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using OpenConnect.Privacy;

namespace OpenConnect.Knowledge;

/// <summary>Trusted-host client for durable knowledge indexing and status.</summary>
public sealed class KnowledgeIndexClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly IEgressGate _gate;
    private readonly Uri _endpoint;
    private readonly string _token;
    private readonly byte[] _key;
    private readonly Func<EgressContext, KnowledgeIndexSubmission, CancellationToken, ValueTask<bool>> _authorizeSubmit;
    private readonly Func<EgressContext, string, string, string, IReadOnlyList<string>, CancellationToken, ValueTask<bool>> _authorizeStatus;
    private readonly bool _ownsHttp;
    private bool _disposed;

    /// <summary>The host authorizers must enforce tenant, object, scope, ACL and privacy policy.</summary>
    public KnowledgeIndexClient(IEgressGate gate, Uri endpoint, string token, byte[] key,
        Func<EgressContext, KnowledgeIndexSubmission, CancellationToken, ValueTask<bool>> authorizeSubmit,
        Func<EgressContext, string, string, string, IReadOnlyList<string>, CancellationToken, ValueTask<bool>> authorizeStatus,
        bool allowInsecureHttp = false)
        : this(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false }),
            gate, endpoint, token, key, authorizeSubmit, authorizeStatus, allowInsecureHttp, true)
    {
    }

    internal KnowledgeIndexClient(HttpClient http, IEgressGate gate, Uri endpoint, string token, byte[] key,
        Func<EgressContext, KnowledgeIndexSubmission, CancellationToken, ValueTask<bool>> authorizeSubmit,
        Func<EgressContext, string, string, string, IReadOnlyList<string>, CancellationToken, ValueTask<bool>> authorizeStatus,
        bool allowInsecureHttp = false, bool ownsHttp = false)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(authorizeSubmit);
        ArgumentNullException.ThrowIfNull(authorizeStatus);
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
        _authorizeSubmit = authorizeSubmit;
        _authorizeStatus = authorizeStatus;
        _ownsHttp = ownsHttp;
    }

    /// <summary>Queues one authorized immutable object reference; the caller owns the receipt.</summary>
    public async Task<JsonDocument> SubmitAsync(EgressContext context, KnowledgeIndexSubmission submission,
        CancellationToken cancellationToken = default)
    {
        ValidateContext(context);
        ArgumentNullException.ThrowIfNull(submission);
        if (!await _authorizeSubmit(context, submission, cancellationToken).ConfigureAwait(false))
            throw new UnauthorizedAccessException("Knowledge indexing was not authorized.");
        var payload = new
        {
            jobId = submission.JobId,
            applicationId = submission.ApplicationId,
            collectionId = submission.CollectionId,
            documentId = submission.DocumentId,
            sourceRevision = submission.SourceRevision,
            embeddingSpaceId = submission.EmbeddingSpaceId,
            objectId = submission.ObjectId,
            objectSha256 = submission.ObjectSha256,
            objectSize = submission.ObjectSize,
            objectVersion = submission.ObjectVersion,
            expectedGeneration = submission.ExpectedGeneration,
            principals = submission.Principals,
            roles = submission.Roles,
            documentExpiresAt = submission.DocumentExpiresAt
        };
        return await SendAsync(context, "knowledge.index", payload, submission.JobId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads status for an authorized principal and document; the caller owns the result.</summary>
    public async Task<JsonDocument> StatusAsync(EgressContext context, Guid jobId, string applicationId, string collectionId,
        string documentId, IReadOnlyList<string> roleIds, CancellationToken cancellationToken = default)
    {
        ValidateContext(context);
        ArgumentNullException.ThrowIfNull(roleIds);
        if (roleIds.Count > 64)
            throw new ArgumentException("The indexing status request is invalid.");
        var roles = Array.AsReadOnly(roleIds.ToArray());
        if (jobId == Guid.Empty || !KnowledgeIndexSubmission.Identifier(applicationId) ||
            !KnowledgeIndexSubmission.Identifier(collectionId) || !KnowledgeIndexSubmission.Identifier(documentId) ||
            roles.Count > 64 || roles.Any(role => !KnowledgeIndexSubmission.Identifier(role)))
            throw new ArgumentException("The indexing status request is invalid.");
        if (!await _authorizeStatus(context, applicationId, collectionId, documentId, roles, cancellationToken).ConfigureAwait(false))
            throw new UnauthorizedAccessException("Knowledge indexing status was not authorized.");
        var payload = new
        {
            jobId,
            applicationId,
            collectionId,
            documentId,
            principalId = context.Principal.Value.ToString(),
            roleIds = roles
        };
        return await SendAsync(context, "knowledge.index.status", payload, jobId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes the authorized index projection with a generation fence. The receipt reports reconciliation state,
    /// not deletion of source objects. The caller owns the returned JSON document.
    /// </summary>
    public async Task<JsonDocument> DeleteAsync(EgressContext context, string applicationId, string collectionId,
        string documentId, long expectedGeneration,
        Func<EgressContext, string, string, string, long, CancellationToken, ValueTask<bool>> authorizeDelete,
        CancellationToken cancellationToken = default)
    {
        ValidateContext(context);
        ArgumentNullException.ThrowIfNull(authorizeDelete);
        if (!KnowledgeIndexSubmission.Identifier(applicationId) || !KnowledgeIndexSubmission.Identifier(collectionId) ||
            !KnowledgeIndexSubmission.Identifier(documentId) || expectedGeneration < 0 || expectedGeneration == long.MaxValue)
            throw new ArgumentException("The indexing deletion request is invalid.");
        if (!await authorizeDelete(context, applicationId, collectionId, documentId, expectedGeneration, cancellationToken).ConfigureAwait(false))
            throw new UnauthorizedAccessException("Knowledge index deletion was not authorized.");
        var payload = new { applicationId, collectionId, documentId, expectedGeneration };
        return await SendAsync(context, "knowledge.index.delete", payload, Guid.Empty, cancellationToken,
            expectedGeneration).ConfigureAwait(false);
    }

    private void ValidateContext(EgressContext context)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(context);
        if (context.Tenant.Value == default || context.Principal.Value == default || context.Session.Value == default ||
            context.Destination != EgressDestination.Worker)
            throw new ArgumentException("An authenticated local worker context is required.", nameof(context));
    }

    private async Task<JsonDocument> SendAsync(EgressContext context, string kind, object payload, Guid jobId,
        CancellationToken cancellationToken, long? expectedDeleteGeneration = null)
    {
        // Scope, ACL and object identifiers must remain exact; a privacy transformation would change authority.
        await PrivacyJsonInspection.DemandGateUnchangedAsync(_gate, JsonSerializer.SerializeToElement(payload), context, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var taskId = Guid.NewGuid().ToString("N");
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = "openconnect.v1",
            taskId,
            kind,
            tenantId = context.Tenant.Value.ToString(),
            issuedAt = now.AddSeconds(-5),
            expiresAt = now.AddMinutes(2),
            payload
        });
        if (body.Length > 128 * 1024)
            throw new ArgumentException("The indexing request is too large.");
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
        const int maximum = 16384;
        if (response.Content.Headers.ContentLength > maximum)
            throw new InvalidOperationException("Invalid worker response.");
        await using var input = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int count;
        while ((count = await input.ReadAsync(chunk, deadline.Token).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + count > maximum)
                throw new InvalidOperationException("Invalid worker response.");
            buffer.Write(chunk, 0, count);
        }
        var document = JsonDocument.Parse(buffer.ToArray());
        try
        {
            Validate(document.RootElement, taskId, kind, jobId, expectedDeleteGeneration);
            await PrivacyJsonInspection.DemandGateUnchangedAsync(_gate, document.RootElement,
                context with { Destination = EgressDestination.Output }, deadline.Token).ConfigureAwait(false);
            return document;
        }
        catch { document.Dispose(); throw; }
    }

    private static void Validate(JsonElement root, string taskId, string kind, Guid jobId, long? expectedDeleteGeneration)
    {
        Properties(root, "schemaVersion", "taskId", "kind", "result");
        if (root.GetProperty("schemaVersion").GetString() != "openconnect.v1" || root.GetProperty("taskId").GetString() != taskId ||
            root.GetProperty("kind").GetString() != kind)
            Invalid();
        var result = root.GetProperty("result");
        if (kind == "knowledge.index")
        {
            Properties(result, "job_id", "created");
            if (!result.GetProperty("job_id").TryGetGuid(out var returned) || returned != jobId ||
                result.GetProperty("created").ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                Invalid();
        }
        else if (kind == "knowledge.index.delete")
        {
            Properties(result, "generation", "reconciliation_state");
            if (expectedDeleteGeneration is null || !result.GetProperty("generation").TryGetInt64(out var generation) ||
                generation != expectedDeleteGeneration.Value + 1 || result.GetProperty("reconciliation_state").GetString()
                    is not ("pending" or "leased" or "delivered" or "dead"))
                Invalid();
        }
        else
        {
            Properties(result, "state", "attempts", "result_generation", "retry_at");
            var state = result.GetProperty("state").GetString();
            var generation = result.GetProperty("result_generation");
            var retry = result.GetProperty("retry_at");
            if (state is not ("pending" or "leased" or "completed" or "dead") ||
                !result.GetProperty("attempts").TryGetInt32(out var attempts) || attempts is < 0 or > 8 ||
                (state == "completed" ? !generation.TryGetInt64(out var value) || value <= 0 : generation.ValueKind != JsonValueKind.Null) ||
                (retry.ValueKind != JsonValueKind.Null && (state != "pending" || !retry.TryGetDateTimeOffset(out _))))
                Invalid();
        }
    }

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
