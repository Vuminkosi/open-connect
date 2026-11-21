using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;
using OpenConnect.Privacy;
using OpenConnect.Privacy.Gates;

namespace OpenConnect.Documents;

/// <summary>Submits authorized documents to a trusted local Unix-socket worker.</summary>
public sealed class DocumentWorkerClient : IDisposable
{
    private const int MaximumDocumentBytes = 10 * 1024 * 1024;
    private const int MaximumResponseBytes = 8 * 1024 * 1024;
    private static readonly Uri ExecuteUri = new("http://localhost/internal/v1/execute");
    private readonly HttpClient _httpClient;
    private readonly ModalityEgressGate _gate;
    private readonly IPrivacyFirewall _firewall;
    private readonly IOptions<PrivacyOptions> _options;
    private readonly Func<EgressContext, string, ReadOnlyMemory<byte>, CancellationToken, ValueTask<bool>> _authorize;
    private readonly string _token;
    private readonly byte[] _key;
    private readonly bool _ownsClient;
    private readonly TimeSpan _requestTimeout;
    private bool _disposed;

    /// <summary>
    /// Creates a client for a protected socket. The host authorizer must verify the principal's
    /// tenant, document and operation access; protect the socket against untrusted proxies.
    /// </summary>
    public DocumentWorkerClient(string socketPath, string bearerToken, byte[] signingKey,
        IPrivacyFirewall firewall, IOptions<PrivacyOptions> options,
        Func<EgressContext, string, ReadOnlyMemory<byte>, CancellationToken, ValueTask<bool>> authorize)
        : this(CreateTransport(socketPath, bearerToken, signingKey, firewall, options, authorize),
            bearerToken, signingKey, firewall, options, authorize, true)
    {
    }

    internal DocumentWorkerClient(HttpClient httpClient, string bearerToken, byte[] signingKey,
        IPrivacyFirewall firewall, IOptions<PrivacyOptions> options,
        Func<EgressContext, string, ReadOnlyMemory<byte>, CancellationToken, ValueTask<bool>> authorize,
        bool ownsClient = false, TimeSpan? requestTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ValidateConfiguration(bearerToken, signingKey, firewall, options, authorize);
        _httpClient = httpClient;
        _token = bearerToken;
        _key = (byte[])signingKey.Clone();
        _gate = new ModalityEgressGate(firewall);
        _firewall = firewall;
        _options = options;
        _authorize = authorize;
        _ownsClient = ownsClient;
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(130);
        if (_requestTimeout <= TimeSpan.Zero || _requestTimeout > TimeSpan.FromMinutes(10))
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
    }

    private static void ValidateConfiguration(string bearerToken, byte[] signingKey,
        IPrivacyFirewall firewall, IOptions<PrivacyOptions> options,
        Func<EgressContext, string, ReadOnlyMemory<byte>, CancellationToken, ValueTask<bool>> authorize)
    {
        ArgumentNullException.ThrowIfNull(signingKey);
        ArgumentNullException.ThrowIfNull(firewall);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(authorize);
        if (string.IsNullOrWhiteSpace(bearerToken) || bearerToken.Any(char.IsWhiteSpace))
            throw new ArgumentException("A worker bearer token is required.", nameof(bearerToken));
        if (signingKey.Length < 32)
            throw new ArgumentException("A signing key of at least 32 bytes is required.", nameof(signingKey));
    }

    /// <summary>
    /// Sends a document after authorization and local modality policy checks. The caller owns the
    /// returned JSON document. OCR and extracted field candidates still require human or application review.
    /// </summary>
    public async Task<JsonDocument> ExecuteAsync(EgressContext context, string kind,
        ReadOnlyMemory<byte> document, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(context);
        if (context.Tenant.Value == default || context.Principal.Value == default ||
            context.Session.Value == default || context.Destination != EgressDestination.Worker)
            throw new ArgumentException("An authenticated local worker context is required.", nameof(context));
        if (kind is not ("documents.ocr" or "documents.fields" or "documents.layout" or "documents.tables" or "documents.analyze"))
            throw new ArgumentException("The document capability is not supported.", nameof(kind));
        if (document.Length is < 1 or > MaximumDocumentBytes)
            throw new ArgumentException("The document size is invalid.", nameof(document));
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_requestTimeout);
        var operationToken = deadline.Token;
        var content = document.ToArray();
        byte[]? body = null;
        byte[]? chunk = null;
        MemoryStream? buffer = null;
        try
        {
            if (!await _authorize(context, kind, content, operationToken).ConfigureAwait(false))
                throw new UnauthorizedAccessException("Document operation was not authorized.");
            operationToken.ThrowIfCancellationRequested();
            var decision = _gate.EvaluateModality(new ModalityEgressRequest(ContentModality.Document, context),
                _options.Value.Mode);
            if (!decision.Allowed)
                throw new PrivacyBlockedException("Document worker egress was denied by privacy policy.");
            if (!_firewall.Enabled || !_options.Value.Enabled || _options.Value.Mode == FirewallMode.Disabled)
            {
                if (!_firewall.Enabled && _firewall is EvidenceRecordingPrivacyFirewall recording)
                    await recording.CheckReadinessAsync(context, operationToken).ConfigureAwait(false);
                throw new PrivacyBlockedException("Document worker privacy inspection is unavailable.");
            }
            if (_firewall is EvidenceRecordingPrivacyFirewall recorded)
            {
                if (!await recorded.CheckReadinessAsync(context, operationToken).ConfigureAwait(false))
                    throw new PrivacyBlockedException("Document worker privacy inspection is unavailable.");
            }
            else
            {
                var readiness = await _firewall.EvaluateAsync(string.Empty, context, operationToken).ConfigureAwait(false);
                if (readiness is null || !readiness.Allowed || readiness.Action != SecurityPolicyAction.Allow ||
                    readiness.Reason == "privacy_enforcement_disabled" ||
                    readiness.DetectedEntities is null || readiness.DetectedEntities.Count != 0 || readiness.SanitizedContent != string.Empty)
                    throw new PrivacyBlockedException("Document worker privacy inspection is unavailable.");
            }

            operationToken.ThrowIfCancellationRequested();
            if (!_firewall.Enabled || !_options.Value.Enabled || _options.Value.Mode == FirewallMode.Disabled)
                throw new PrivacyBlockedException("Document worker privacy inspection is unavailable.");
            var now = DateTimeOffset.UtcNow;
            var taskId = Guid.NewGuid().ToString("N");
            body = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = "openconnect.v1", taskId, kind, tenantId = context.Tenant.Value.ToString(),
                issuedAt = now.AddSeconds(-5), expiresAt = now.AddMinutes(2),
                payload = new { contentBase64 = Convert.ToBase64String(content) }
            });
            using var request = new HttpRequestMessage(HttpMethod.Post, ExecuteUri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            request.Headers.Add("X-OpenConnect-Signature", Convert.ToHexString(HMACSHA256.HashData(_key, body)).ToLowerInvariant());
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException("The document worker rejected the request.", null, response.StatusCode);
            if (response.Content.Headers.ContentLength > MaximumResponseBytes)
                throw new InvalidOperationException("The document worker response is too large.");
            await using var input = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            buffer = new MemoryStream();
            chunk = new byte[8192];
            int count;
            while ((count = await input.ReadAsync(chunk, deadline.Token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + count > MaximumResponseBytes)
                    throw new InvalidOperationException("The document worker response is too large.");
                buffer.Write(chunk, 0, count);
            }
            buffer.Position = 0;
            var output = JsonDocument.Parse(buffer);
            try
            {
                ValidateEnvelope(output.RootElement, taskId, kind);
                DocumentWorkerResponseValidator.Validate(output.RootElement.GetProperty("result"), kind);
                var outputContext = context with { Destination = EgressDestination.Output };
                await PrivacyJsonInspection.DemandUnchangedAsync(_firewall, output.RootElement, outputContext, deadline.Token).ConfigureAwait(false);
                return output;
            }
            catch
            {
                output.Dispose();
                throw;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(content);
            if (body is not null) CryptographicOperations.ZeroMemory(body);
            if (chunk is not null) CryptographicOperations.ZeroMemory(chunk);
            if (buffer is not null)
            {
                if (buffer.TryGetBuffer(out var bytes)) CryptographicOperations.ZeroMemory(bytes.AsSpan());
                buffer.Dispose();
            }
        }
    }

    private static HttpClient CreateTransport(string socketPath, string bearerToken, byte[] signingKey,
        IPrivacyFirewall firewall, IOptions<PrivacyOptions> options,
        Func<EgressContext, string, ReadOnlyMemory<byte>, CancellationToken, ValueTask<bool>> authorize)
    {
        ValidateConfiguration(bearerToken, signingKey, firewall, options, authorize);
        if (string.IsNullOrWhiteSpace(socketPath) || !Path.IsPathFullyQualified(socketPath))
            throw new ArgumentException("An absolute, protected Unix socket path is required.", nameof(socketPath));
        var endpoint = new UnixDomainSocketEndPoint(socketPath);
        var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false };
        handler.ConnectCallback = async (_, cancellationToken) =>
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(130) };
    }

    private static void ValidateEnvelope(JsonElement root, string taskId, string kind)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("The document worker response is invalid.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name is not ("schemaVersion" or "taskId" or "kind" or "result") || !seen.Add(property.Name))
                throw new InvalidOperationException("The document worker response is invalid.");
        }
        if (seen.Count != 4 || root.GetProperty("schemaVersion").GetString() != "openconnect.v1" ||
            root.GetProperty("taskId").GetString() != taskId || root.GetProperty("kind").GetString() != kind ||
            root.GetProperty("result").ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("The document worker response does not match the request.");
    }

    /// <summary>Disposes the owned transport and clears the signing key.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        CryptographicOperations.ZeroMemory(_key);
        if (_ownsClient)
            _httpClient.Dispose();
    }
}
