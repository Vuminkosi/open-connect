using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;
using OpenConnect.Privacy;
using OpenConnect.Privacy.Gates;

namespace OpenConnect.Knowledge;

/// <summary>Authorized, privacy-gated client for a protected local vision embedding socket.</summary>
public sealed class VisionEmbeddingClient : IDisposable
{
    private static readonly Uri ExecuteUri = new("http://localhost/internal/v1/execute");
    private readonly HttpClient _http;
    private readonly string _token;
    private readonly byte[] _key;
    private readonly IEgressGate _textGate;
    private readonly IPrivacyFirewall _firewall;
    private readonly ModalityEgressGate _imageGate;
    private readonly IOptions<PrivacyOptions> _options;
    private readonly Func<EgressContext, ReadOnlyMemory<byte>, CancellationToken, ValueTask<bool>> _authorizeImage;
    private readonly Func<EgressContext, string, CancellationToken, ValueTask<bool>> _authorizeText;
    private readonly bool _ownsHttp;
    private readonly TimeSpan _requestTimeout;
    private bool _disposed;

    /// <summary>The host must authorize the tenant, principal and each image or query before egress.</summary>
    public VisionEmbeddingClient(string socketPath, string token, byte[] key, IEgressGate textGate,
        IPrivacyFirewall firewall, IOptions<PrivacyOptions> options,
        Func<EgressContext, ReadOnlyMemory<byte>, CancellationToken, ValueTask<bool>> authorizeImage,
        Func<EgressContext, string, CancellationToken, ValueTask<bool>> authorizeText)
        : this(CreateTransport(socketPath), token, key, textGate, firewall, options, authorizeImage, authorizeText, true)
    {
    }

    internal VisionEmbeddingClient(HttpClient http, string token, byte[] key, IEgressGate textGate,
        IPrivacyFirewall firewall, IOptions<PrivacyOptions> options,
        Func<EgressContext, ReadOnlyMemory<byte>, CancellationToken, ValueTask<bool>> authorizeImage,
        Func<EgressContext, string, CancellationToken, ValueTask<bool>> authorizeText, bool ownsHttp = false,
        TimeSpan? requestTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(textGate);
        ArgumentNullException.ThrowIfNull(firewall);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(authorizeImage);
        ArgumentNullException.ThrowIfNull(authorizeText);
        if (string.IsNullOrWhiteSpace(token) || token.Any(char.IsWhiteSpace) || key.Length < 32)
            throw new ArgumentException("Worker credentials are invalid.");
        _http = http;
        _token = token;
        _key = (byte[])key.Clone();
        _textGate = textGate;
        _firewall = firewall;
        _imageGate = new ModalityEgressGate(firewall);
        _options = options;
        _authorizeImage = authorizeImage;
        _authorizeText = authorizeText;
        _ownsHttp = ownsHttp;
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(130);
        if (_requestTimeout <= TimeSpan.Zero || _requestTimeout > TimeSpan.FromMinutes(10))
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
    }

    /// <summary>Embeds a bounded, host-authorized image after local modality policy checks.</summary>
    public async Task<KnowledgeEmbeddingResult> EmbedImageAsync(EgressContext context, ReadOnlyMemory<byte> image,
        CancellationToken cancellationToken = default)
    {
        ValidateContext(context);
        if (image.Length is < 1 or > 10 * 1024 * 1024)
            throw new ArgumentException("Invalid image size.", nameof(image));
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_requestTimeout);
        var operationToken = deadline.Token;
        var copy = image.ToArray();
        try
        {
            if (!await _authorizeImage(context, copy, operationToken).ConfigureAwait(false))
                throw new UnauthorizedAccessException("Image embedding was not authorized.");
            operationToken.ThrowIfCancellationRequested();
            if (!_firewall.Enabled || !_options.Value.Enabled || _options.Value.Mode == FirewallMode.Disabled)
            {
                if (!_firewall.Enabled && _firewall is EvidenceRecordingPrivacyFirewall recording)
                    await recording.CheckReadinessAsync(context, operationToken).ConfigureAwait(false);
                throw new PrivacyBlockedException("Image privacy enforcement is unavailable.");
            }
            if (_firewall is EvidenceRecordingPrivacyFirewall recorded)
            {
                if (!await recorded.CheckReadinessAsync(context, operationToken).ConfigureAwait(false))
                    throw new PrivacyBlockedException("Image privacy enforcement is unavailable.");
            }
            else
            {
                var readiness = await _firewall.EvaluateAsync(string.Empty, context, operationToken).ConfigureAwait(false);
                if (readiness is null || !readiness.Allowed || readiness.Action != SecurityPolicyAction.Allow ||
                    readiness.Reason == "privacy_enforcement_disabled" ||
                    readiness.DetectedEntities is null || readiness.DetectedEntities.Count != 0 || readiness.SanitizedContent != string.Empty)
                    throw new PrivacyBlockedException("Image privacy enforcement is unavailable.");
            }
            var decision = _imageGate.EvaluateModality(new ModalityEgressRequest(ContentModality.Image, context), _options.Value.Mode);
            if (!decision.Allowed)
                throw new PrivacyBlockedException("Image embedding was denied by privacy policy.");
            return await SendAsync(context, new { modality = "image", contentBase64 = Convert.ToBase64String(copy) },
                operationToken).ConfigureAwait(false);
        }
        finally { CryptographicOperations.ZeroMemory(copy); }
    }

    /// <summary>Embeds a host-authorized query after applying text privacy policy.</summary>
    public async Task<KnowledgeEmbeddingResult> EmbedTextAsync(EgressContext context, string text,
        CancellationToken cancellationToken = default)
    {
        ValidateContext(context);
        if (string.IsNullOrWhiteSpace(text) || text.Length > 8192)
            throw new ArgumentException("Invalid query.", nameof(text));
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_requestTimeout);
        var operationToken = deadline.Token;
        if (!await _authorizeText(context, text, operationToken).ConfigureAwait(false))
            throw new UnauthorizedAccessException("Text embedding was not authorized.");
        operationToken.ThrowIfCancellationRequested();
        var decision = await _textGate.EvaluateAsync(text, context, operationToken).ConfigureAwait(false);
        if (!WorkerEgressValidation.IsSafe(text, decision) || string.IsNullOrWhiteSpace(decision!.SanitizedContent) || decision.SanitizedContent.Length > 8192)
            throw new PrivacyBlockedException("Text embedding was denied by privacy policy.");
        if (decision.SanitizedContent.Contains('%'))
            await PrivacyJsonInspection.DemandGateUnchangedAsync(_textGate, JsonSerializer.SerializeToElement(decision.SanitizedContent), context, operationToken).ConfigureAwait(false);
        return await SendAsync(context, new { modality = "text", text = decision.SanitizedContent },
            operationToken).ConfigureAwait(false);
    }

    private void ValidateContext(EgressContext context)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(context);
        if (context.Tenant.Value == default || context.Principal.Value == default || context.Session.Value == default ||
            context.Destination != EgressDestination.Worker)
            throw new ArgumentException("An authenticated local worker context is required.", nameof(context));
    }

    private async Task<KnowledgeEmbeddingResult> SendAsync(EgressContext context, object payload, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = DateTimeOffset.UtcNow;
        var taskId = Guid.NewGuid().ToString("N");
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = "openconnect.v1",
            taskId,
            kind = "vision.embed",
            tenantId = context.Tenant.Value.ToString(),
            issuedAt = now.AddSeconds(-5),
            expiresAt = now.AddMinutes(2),
            payload
        });
        byte[]? chunk = null;
        MemoryStream? buffer = null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, ExecuteUri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            request.Headers.Add("X-OpenConnect-Signature", Convert.ToHexString(HMACSHA256.HashData(_key, body)).ToLowerInvariant());
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException("The vision worker rejected the request.", null, response.StatusCode);
            const int maxResponse = 64 * 1024;
            if (response.Content.Headers.ContentLength > maxResponse)
                throw new InvalidOperationException("Invalid worker response.");
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            buffer = new MemoryStream();
            chunk = new byte[4096];
            int count;
            while ((count = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + count > maxResponse)
                    throw new InvalidOperationException("Invalid worker response.");
                buffer.Write(chunk, 0, count);
            }
            using var doc = JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, (int)buffer.Length));
            var root = doc.RootElement;
            Properties(root, "schemaVersion", "taskId", "kind", "result");
            if (root.GetProperty("schemaVersion").GetString() != "openconnect.v1" || root.GetProperty("taskId").GetString() != taskId ||
                root.GetProperty("kind").GetString() != "vision.embed")
                Invalid();
            var result = root.GetProperty("result");
            Properties(result, "vector", "model_id", "fingerprint", "space_id", "dimension");
            var model = result.GetProperty("model_id").GetString();
            var fingerprint = result.GetProperty("fingerprint").GetString();
            var space = result.GetProperty("space_id").GetString();
            var vector = result.GetProperty("vector");
            if (!KnowledgeIndexSubmission.Identifier(model) || !KnowledgeIndexSubmission.Digest(fingerprint) ||
                !KnowledgeIndexSubmission.Digest(space) || !result.GetProperty("dimension").TryGetInt32(out var dimension) ||
                dimension != 512 || vector.ValueKind != JsonValueKind.Array || vector.GetArrayLength() != dimension)
                Invalid();
            await PrivacyJsonInspection.DemandGateUnchangedAsync(_textGate,
                JsonSerializer.SerializeToElement(new { model, fingerprint, space }),
                context with { Destination = EgressDestination.Output }, cancellationToken).ConfigureAwait(false);
            var values = new float[512];
            var index = 0;
            foreach (var item in vector.EnumerateArray())
            {
                if (!item.TryGetSingle(out var value) || !float.IsFinite(value) || Math.Abs(value) > 1)
                    Invalid();
                values[index++] = value;
            }
            var norm = Math.Sqrt(values.Sum(value => (double)value * value));
            if (!double.IsFinite(norm) || Math.Abs(norm - 1) > .001)
                Invalid();
            return new KnowledgeEmbeddingResult([new KnowledgeEmbedding(Array.AsReadOnly(values), false, norm)],
                model!, fingerprint!, 512, space!);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(body);
            if (chunk is not null)
                CryptographicOperations.ZeroMemory(chunk);
            if (buffer is not null)
            {
                if (buffer.TryGetBuffer(out var bytes))
                    CryptographicOperations.ZeroMemory(bytes.AsSpan());
                buffer.Dispose();
            }
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

    private static HttpClient CreateTransport(string socketPath)
    {
        if (string.IsNullOrWhiteSpace(socketPath) || !Path.IsPathFullyQualified(socketPath))
            throw new ArgumentException("An absolute, protected Unix socket path is required.", nameof(socketPath));
        var endpoint = new UnixDomainSocketEndPoint(socketPath);
        var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false };
        handler.ConnectCallback = async (_, cancellationToken) =>
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            { await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false); return new NetworkStream(socket, true); }
            catch { socket.Dispose(); throw; }
        };
        return new HttpClient(handler);
    }

    /// <summary>Clears signing material and disposes the owned transport.</summary>
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
