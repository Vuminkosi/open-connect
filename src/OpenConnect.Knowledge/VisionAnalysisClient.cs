using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;
using OpenConnect.Privacy;
using OpenConnect.Privacy.Gates;

namespace OpenConnect.Knowledge;

/// <summary>Authorized, privacy-gated client for a protected local vision analysis worker.</summary>
public sealed class VisionAnalysisClient : IDisposable
{
    private static readonly Uri Endpoint = new("http://localhost/internal/v1/execute");
    private readonly HttpClient _http;
    private readonly string _token;
    private readonly byte[] _key;
    private readonly IPrivacyFirewall _firewall;
    private readonly IOptions<PrivacyOptions> _options;
    private readonly Func<EgressContext, ReadOnlyMemory<byte>, CancellationToken, ValueTask<bool>> _authorize;
    private readonly bool _ownsHttp;
    private bool _disposed;

    /// <summary>The host must authorize the tenant, principal and image, and protect the worker socket.</summary>
    public VisionAnalysisClient(string socketPath, string token, byte[] key, IPrivacyFirewall firewall,
        IOptions<PrivacyOptions> options, Func<EgressContext, ReadOnlyMemory<byte>, CancellationToken, ValueTask<bool>> authorize)
        : this(CreateTransport(socketPath), token, key, firewall, options, authorize, true) { }

    internal VisionAnalysisClient(HttpClient http, string token, byte[] key, IPrivacyFirewall firewall,
        IOptions<PrivacyOptions> options, Func<EgressContext, ReadOnlyMemory<byte>, CancellationToken, ValueTask<bool>> authorize, bool ownsHttp = false)
    {
        ArgumentNullException.ThrowIfNull(http); ArgumentNullException.ThrowIfNull(key); ArgumentNullException.ThrowIfNull(firewall);
        ArgumentNullException.ThrowIfNull(options); ArgumentNullException.ThrowIfNull(authorize);
        if (string.IsNullOrWhiteSpace(token) || token.Any(char.IsWhiteSpace) || key.Length < 32) throw new ArgumentException("Worker credentials are invalid.");
        _http = http; _token = token; _key = (byte[])key.Clone(); _firewall = firewall; _options = options; _authorize = authorize; _ownsHttp = ownsHttp;
    }

    /// <summary>Returns a caller-owned sensitive result; generic classifications require application review.</summary>
    public async Task<JsonDocument> AnalyzeAsync(EgressContext context, ReadOnlyMemory<byte> image, int topK = 5,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(context);
        if (context.Tenant.Value == default || context.Principal.Value == default || context.Session.Value == default || context.Destination != EgressDestination.Worker ||
            image.Length is < 1 or > 10 * 1024 * 1024 || topK is < 1 or > 20) throw new ArgumentException("Invalid image analysis request.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(130));
        var operationToken = timeout.Token;
        var snapshot = image.ToArray(); byte[]? body = null; byte[]? chunk = null; MemoryStream? buffer = null;
        try
        {
            if (!await _authorize(context, snapshot, operationToken).ConfigureAwait(false)) throw new UnauthorizedAccessException("Image analysis was not authorized.");
            operationToken.ThrowIfCancellationRequested();
            if (!_firewall.Enabled || !_options.Value.Enabled || _options.Value.Mode == FirewallMode.Disabled)
            {
                if (!_firewall.Enabled && _firewall is EvidenceRecordingPrivacyFirewall unavailable)
                    await unavailable.CheckReadinessAsync(context, operationToken).ConfigureAwait(false);
                throw new PrivacyBlockedException("Image privacy enforcement is unavailable.");
            }
            if (_firewall is EvidenceRecordingPrivacyFirewall recording)
            {
                if (!await recording.CheckReadinessAsync(context, operationToken).ConfigureAwait(false)) throw new PrivacyBlockedException("Image privacy enforcement is unavailable.");
            }
            else
            {
                var readiness = await _firewall.EvaluateAsync(string.Empty, context, operationToken).ConfigureAwait(false);
                if (readiness is null || !readiness.Allowed || readiness.Action != SecurityPolicyAction.Allow ||
                    readiness.Reason == "privacy_enforcement_disabled" || readiness.DetectedEntities is not { Count: 0 } || readiness.SanitizedContent != string.Empty)
                    throw new PrivacyBlockedException("Image privacy enforcement is unavailable.");
            }
            if (!new ModalityEgressGate(_firewall).EvaluateModality(new ModalityEgressRequest(ContentModality.Image, context), _options.Value.Mode).Allowed)
                throw new PrivacyBlockedException("Image analysis was denied by privacy policy.");
            operationToken.ThrowIfCancellationRequested();
            if (!_firewall.Enabled || !_options.Value.Enabled || _options.Value.Mode == FirewallMode.Disabled)
                throw new PrivacyBlockedException("Image privacy enforcement is unavailable.");
            var now = DateTimeOffset.UtcNow; var taskId = Guid.NewGuid().ToString("N");
            body = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = "openconnect.v1", taskId, kind = "vision.analyze",
                tenantId = context.Tenant.Value.ToString(), issuedAt = now.AddSeconds(-5), expiresAt = now.AddMinutes(2),
                payload = new { contentBase64 = Convert.ToBase64String(snapshot), topK } });
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            request.Headers.Add("X-OpenConnect-Signature", Convert.ToHexString(HMACSHA256.HashData(_key, body)).ToLowerInvariant());
            request.Content = new ByteArrayContent(body); request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, operationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException("The vision worker rejected the request.", null, response.StatusCode);
            if (response.Content.Headers.ContentLength > 65536) throw new InvalidOperationException("Invalid worker response.");
            await using var input = await response.Content.ReadAsStreamAsync(operationToken).ConfigureAwait(false);
            buffer = new MemoryStream(); chunk = new byte[8192]; int count;
            while ((count = await input.ReadAsync(chunk, operationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + count > 65536) throw new InvalidOperationException("Invalid worker response.");
                buffer.Write(chunk, 0, count);
            }
            buffer.Position = 0;
            var result = JsonDocument.Parse(buffer);
            try
            {
                Validate(result.RootElement, taskId, topK);
                await PrivacyJsonInspection.DemandUnchangedAsync(_firewall, result.RootElement, context with { Destination = EgressDestination.Output }, operationToken).ConfigureAwait(false);
                return result;
            }
            catch { result.Dispose(); throw; }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(snapshot);
            if (body is not null) CryptographicOperations.ZeroMemory(body);
            if (chunk is not null) CryptographicOperations.ZeroMemory(chunk);
            if (buffer is not null) { if (buffer.TryGetBuffer(out var bytes)) CryptographicOperations.ZeroMemory(bytes.AsSpan()); buffer.Dispose(); }
        }
    }

    /// <summary>Authorizes and verifies an exact tenant-scoped object version before analysis.</summary>
    public async Task<JsonDocument> AnalyzeObjectAsync(EgressContext context, KnowledgeImageObjectReference reference,
        Func<EgressContext, KnowledgeImageObjectReference, CancellationToken, ValueTask<bool>> authorizeObject,
        Func<string, string, CancellationToken, Task<Stream>> openVersion, int topK = 5,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(authorizeObject);
        ArgumentNullException.ThrowIfNull(openVersion);
        if (context.Tenant.Value == default || context.Principal.Value == default || context.Session.Value == default ||
            context.Destination != EgressDestination.Worker || topK is < 1 or > 20)
            throw new ArgumentException("Invalid image analysis request.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        if (!await authorizeObject(context, reference, deadline.Token).ConfigureAwait(false))
            throw new UnauthorizedAccessException("Image object access was not authorized.");
        deadline.Token.ThrowIfCancellationRequested();
        var tenantHash = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(context.Tenant.Value.ToString()))).ToLowerInvariant();
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
            return await AnalyzeAsync(context, bytes.AsMemory(0, offset), topK, deadline.Token).ConfigureAwait(false);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static void Validate(JsonElement root, string taskId, int topK)
    {
        try
        {
            Properties(root, "schemaVersion", "taskId", "kind", "result");
            if (root.GetProperty("schemaVersion").GetString() != "openconnect.v1" || root.GetProperty("taskId").GetString() != taskId || root.GetProperty("kind").GetString() != "vision.analyze") Invalid();
            var result = root.GetProperty("result");
            Properties(result, "model_id", "model_fingerprint", "taxonomy_version", "preprocessing_version", "width", "height", "classifications");
            var model = result.GetProperty("model_id").GetString(); var fingerprint = result.GetProperty("model_fingerprint").GetString();
            var taxonomy = result.GetProperty("taxonomy_version").GetString();
            if (!KnowledgeIndexSubmission.Identifier(model) || !KnowledgeIndexSubmission.Digest(fingerprint) || !KnowledgeIndexSubmission.Identifier(taxonomy) || taxonomy!.EnumerateRunes().Count() > 128 ||
                result.GetProperty("preprocessing_version").GetString() != "imagenet-center224-v1" ||
                !result.GetProperty("width").TryGetInt32(out var width) || !result.GetProperty("height").TryGetInt32(out var height) ||
                width <= 0 || height <= 0 || (long)width * height > 16000000 || Math.Max(width, height) > 12L * Math.Min(width, height)) Invalid();
            var entries = result.GetProperty("classifications");
            if (entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() != topK) Invalid();
            var indexes = new HashSet<int>(); var previousScore = double.PositiveInfinity; var previousIndex = -1;
            foreach (var item in entries.EnumerateArray())
            {
                Properties(item, "label", "class_index", "score");
                var label = item.GetProperty("label").GetString();
                var index = 0; var score = 0d;
                if (string.IsNullOrWhiteSpace(label) || label.EnumerateRunes().Count() > 256 ||
                    !item.GetProperty("class_index").TryGetInt32(out index) || index is < 0 or > 999 || !indexes.Add(index) ||
                    !item.GetProperty("score").TryGetDouble(out score) || !double.IsFinite(score) || score is < 0 or > 1 ||
                    score > previousScore || (score == previousScore && index <= previousIndex)) Invalid();
                previousScore = score; previousIndex = index;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException) { throw new InvalidOperationException("Invalid worker response."); }
    }

    private static void Properties(JsonElement value, params string[] expected)
    {
        if (value.ValueKind != JsonValueKind.Object) Invalid();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject()) if (!expected.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name)) Invalid();
        if (seen.Count != expected.Length) Invalid();
    }
    [DoesNotReturn]
    private static void Invalid() => throw new InvalidOperationException("Invalid worker response.");
    private static HttpClient CreateTransport(string socketPath)
    {
        if (string.IsNullOrWhiteSpace(socketPath) || !Path.IsPathFullyQualified(socketPath)) throw new ArgumentException("An absolute protected socket path is required.", nameof(socketPath));
        var endpoint = new UnixDomainSocketEndPoint(socketPath);
        var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false };
        handler.ConnectCallback = async (_, token) => { var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try { await socket.ConnectAsync(endpoint, token).ConfigureAwait(false); return new NetworkStream(socket, true); }
            catch { socket.Dispose(); throw; } };
        return new HttpClient(handler);
    }

    /// <summary>Clears signing material and disposes the owned transport.</summary>
    public void Dispose() { if (_disposed) return; _disposed = true; CryptographicOperations.ZeroMemory(_key); if (_ownsHttp) _http.Dispose(); }
}
