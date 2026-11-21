using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenConnect.Privacy;
using OpenConnect.Abstractions;

namespace OpenConnect.Documents;

/// <summary>Submits authorized structured extraction to a trusted local inference worker.</summary>
public sealed class ExtractionWorkerClient : IDisposable
{
    private static readonly Uri ExecuteUri = new("http://localhost/internal/v1/extract");
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly HttpClient _client;
    private readonly Uri _endpoint;
    private readonly IPrivacyFirewall _firewall;
    private readonly Func<EgressContext, string, JsonElement, CancellationToken, ValueTask<bool>> _authorize;
    private readonly byte[] _key;
    private readonly string _token;
    private readonly bool _ownsClient;
    private bool _disposed;

    /// <summary>Creates a client for a protected Unix socket and a host-owned tenant and content authorizer.</summary>
    public ExtractionWorkerClient(string socketPath, string bearerToken, byte[] signingKey, IPrivacyFirewall firewall,
        Func<EgressContext, string, JsonElement, CancellationToken, ValueTask<bool>> authorize)
        : this(CreateTransport(socketPath), bearerToken, signingKey, firewall, authorize, true) { }

    /// <summary>Creates a client for a trusted inference endpoint. Plain HTTP requires a protected network and explicit opt-in.</summary>
    public ExtractionWorkerClient(Uri endpoint, string bearerToken, byte[] signingKey, IPrivacyFirewall firewall,
        Func<EgressContext, string, JsonElement, CancellationToken, ValueTask<bool>> authorize, bool allowInsecureHttp = false)
        : this(CreateTransport(endpoint, allowInsecureHttp), bearerToken, signingKey, firewall, authorize, true, endpoint) { }

    internal ExtractionWorkerClient(HttpClient client, string bearerToken, byte[] signingKey, IPrivacyFirewall firewall,
        Func<EgressContext, string, JsonElement, CancellationToken, ValueTask<bool>> authorize, bool ownsClient = false,
        Uri? endpoint = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(signingKey);
        ArgumentNullException.ThrowIfNull(firewall);
        ArgumentNullException.ThrowIfNull(authorize);
        if (string.IsNullOrWhiteSpace(bearerToken) || bearerToken.Any(char.IsWhiteSpace) || signingKey.Length < 32)
            throw new ArgumentException("Invalid worker credentials.");
        _client = client;
        _endpoint = endpoint is null ? ExecuteUri : new Uri(endpoint, "/internal/v1/extract");
        _token = bearerToken;
        _key = (byte[])signingKey.Clone();
        _firewall = firewall;
        _authorize = authorize;
        _ownsClient = ownsClient;
    }

    /// <summary>Extracts candidates with validated source citations. The caller owns the returned JSON document.</summary>
    public async Task<JsonDocument> ExtractAsync(EgressContext context, string document, JsonElement schema,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(document);
        if (context.Tenant.Value == default || context.Principal.Value == default || context.Session.Value == default ||
            context.Destination != EgressDestination.Worker || document.Length == 0 || document.EnumerateRunes().Count() > 100_000 ||
            schema.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Invalid extraction request.");
        try { _ = StrictUtf8.GetByteCount(document); }
        catch (EncoderFallbackException) { throw new ArgumentException("Invalid extraction request."); }
        var stableSchema = schema.Clone();
        var schemaText = stableSchema.GetRawText();
        if (StrictUtf8.GetByteCount(schemaText) > 16_384)
            throw new ArgumentException("Invalid extraction request.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        var operationToken = deadline.Token;
        if (!await _authorize(context, document, stableSchema, operationToken).ConfigureAwait(false))
            throw new UnauthorizedAccessException("Extraction was not authorized.");
        if (!_firewall.Enabled && _firewall is not EvidenceRecordingPrivacyFirewall)
            throw new PrivacyBlockedException("Extraction was blocked by privacy policy.");
        await DemandUnchangedAsync(document, context, operationToken).ConfigureAwait(false);
        await PrivacyJsonInspection.DemandUnchangedAsync(_firewall, stableSchema, context, operationToken).ConfigureAwait(false);
        operationToken.ThrowIfCancellationRequested();
        if (!_firewall.Enabled) throw new PrivacyBlockedException("Extraction was blocked by privacy policy.");
        var now = DateTimeOffset.UtcNow;
        var taskId = Guid.NewGuid().ToString("N");
        var body = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = "openconnect.v1", taskId,
            kind = "documents.extract", tenantId = context.Tenant.Value.ToString(), issuedAt = now.AddSeconds(-5),
            expiresAt = now.AddMinutes(2), payload = new { document, extractionSchema = stableSchema } });
        try
        {
            if (body.Length > 512 * 1024) throw new ArgumentException("Invalid extraction request.");
            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            request.Headers.Add("X-OpenConnect-Signature", Convert.ToHexString(HMACSHA256.HashData(_key, body)).ToLowerInvariant());
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException("The extraction worker rejected the request.", null, response.StatusCode);
            if (response.Content.Headers.ContentLength > 128 * 1024)
                throw new InvalidOperationException("The extraction worker response is too large.");
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            try
            {
                int count;
                while ((count = await stream.ReadAsync(chunk, deadline.Token).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + count > 128 * 1024) throw new InvalidOperationException("The extraction worker response is too large.");
                    buffer.Write(chunk, 0, count);
                }
                buffer.Position = 0;
                var result = JsonDocument.Parse(buffer);
                try
                {
                    var root = result.RootElement;
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    if (root.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Invalid extraction response.");
                    foreach (var property in root.EnumerateObject())
                        if (property.Name is not ("schemaVersion" or "taskId" or "kind" or "result") || !names.Add(property.Name))
                            throw new InvalidOperationException("Invalid extraction response.");
                    if (names.Count != 4 || root.GetProperty("schemaVersion").GetString() != "openconnect.v1" ||
                        root.GetProperty("taskId").GetString() != taskId || root.GetProperty("kind").GetString() != "documents.extract")
                        throw new InvalidOperationException("Invalid extraction response.");
                    ExtractionResponseValidator.Validate(root.GetProperty("result"), stableSchema, document);
                    var outputContext = context with { Destination = EgressDestination.Output };
                    await PrivacyJsonInspection.DemandUnchangedAsync(_firewall, root, outputContext, deadline.Token).ConfigureAwait(false);
                    return result;
                }
                catch { result.Dispose(); throw; }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(chunk);
                if (buffer.TryGetBuffer(out var bytes)) CryptographicOperations.ZeroMemory(bytes.AsSpan());
            }
        }
        finally { CryptographicOperations.ZeroMemory(body); }
    }

    private async Task DemandUnchangedAsync(string content, EgressContext context, CancellationToken cancellationToken)
    {
        var decision = await _firewall.EvaluateAsync(content, context, cancellationToken).ConfigureAwait(false);
        if (decision is null || !decision.Allowed || !Enum.IsDefined(decision.Action) ||
            decision.Action is SecurityPolicyAction.Block or SecurityPolicyAction.Review || decision.Reason == "privacy_enforcement_disabled" || decision.DetectedEntities is null ||
            decision.DetectedEntities.Count != 0 || decision.SanitizedContent != content)
            throw new PrivacyBlockedException("Extraction was blocked by privacy policy.");
        if (content.Contains('%'))
            await PrivacyJsonInspection.DemandUnchangedAsync(_firewall, JsonSerializer.SerializeToElement(content), context, cancellationToken).ConfigureAwait(false);
    }

    private static HttpClient CreateTransport(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("An absolute protected Unix socket path is required.", nameof(path));
        var endpoint = new UnixDomainSocketEndPoint(path);
        var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false };
        handler.ConnectCallback = async (_, cancellationToken) =>
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try { await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false); return new NetworkStream(socket, true); }
            catch { socket.Dispose(); throw; }
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(2) };
    }

    private static HttpClient CreateTransport(Uri endpoint, bool allowInsecureHttp)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri || !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !(endpoint.Scheme == Uri.UriSchemeHttps || allowInsecureHttp && endpoint.Scheme == Uri.UriSchemeHttp))
            throw new ArgumentException("A trusted worker endpoint is required.", nameof(endpoint));
        return new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false })
            { Timeout = TimeSpan.FromMinutes(2) };
    }

    /// <summary>Clears the signing key and disposes the owned transport.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CryptographicOperations.ZeroMemory(_key);
        if (_ownsClient) _client.Dispose();
    }
}
