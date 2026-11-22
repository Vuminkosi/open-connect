using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using OpenConnect.Privacy;

namespace OpenConnect.Analytics;

/// <summary>Submits authorized analytics inputs to a trusted worker.</summary>
public sealed class AnalyticsWorkerClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly string _token;
    private readonly byte[] _key;
    private readonly IPrivacyFirewall _firewall;
    private readonly Func<EgressContext, JsonElement, CancellationToken, ValueTask<bool>> _authorize;
    private readonly bool _ownsClient;
    private bool _disposed;

    /// <summary>Creates a client for a trusted worker; plaintext HTTP requires explicit protected-network opt-in.</summary>
    public AnalyticsWorkerClient(Uri endpoint, string token, byte[] key, IPrivacyFirewall firewall,
        Func<EgressContext, JsonElement, CancellationToken, ValueTask<bool>> authorize, bool allowInsecureHttp = false)
        : this(CreateHttp(endpoint, allowInsecureHttp), endpoint, token, key, firewall, authorize, true) { }

    internal AnalyticsWorkerClient(HttpClient http, Uri endpoint, string token, byte[] key, IPrivacyFirewall firewall,
        Func<EgressContext, JsonElement, CancellationToken, ValueTask<bool>> authorize, bool ownsClient = false)
    {
        ArgumentNullException.ThrowIfNull(http); ArgumentNullException.ThrowIfNull(endpoint); ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(firewall); ArgumentNullException.ThrowIfNull(authorize);
        if (!endpoint.IsAbsoluteUri || !string.IsNullOrEmpty(endpoint.UserInfo) ||
            endpoint.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(token) ||
            token.Any(char.IsWhiteSpace) || key.Length < 32) throw new ArgumentException("Invalid analytics worker configuration.");
        _http = http; _endpoint = new Uri(endpoint, "/internal/v1/execute"); _token = token;
        _key = (byte[])key.Clone(); _firewall = firewall; _authorize = authorize; _ownsClient = ownsClient;
    }

    /// <summary>Analyzes an authorized payload; the caller owns the returned JSON document.</summary>
    public async Task<JsonDocument> AnalyzeAsync(EgressContext context, JsonElement payload, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(context);
        if (context.Tenant.Value == default || context.Principal.Value == default || context.Session.Value == default ||
            context.Destination != EgressDestination.Worker || payload.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Invalid analytics request.");
        var input = payload.Clone();
        AnalyticsValidation.ValidateInput(input);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        if (!await _authorize(context, input, deadline.Token).ConfigureAwait(false))
            throw new UnauthorizedAccessException("Analytics request was not authorized.");
        await PrivacyJsonInspection.DemandUnchangedAsync(_firewall, input, context, deadline.Token).ConfigureAwait(false);
        deadline.Token.ThrowIfCancellationRequested();
        if (!_firewall.Enabled) throw new PrivacyBlockedException("Analytics was blocked by privacy policy.");
        var now = DateTimeOffset.UtcNow;
        var taskId = Guid.NewGuid().ToString("N");
        var body = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = "openconnect.v1", taskId,
            kind = "analytics.analyze", tenantId = context.Tenant.Value.ToString(), issuedAt = now.AddSeconds(-5),
            expiresAt = now.AddMinutes(2), payload = input });
        byte[]? chunk = null;
        MemoryStream? buffer = null;
        try
        {
            if (body.Length > 256 * 1024) throw new ArgumentException("Invalid analytics request.");
            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            request.Headers.Add("X-OpenConnect-Signature", Convert.ToHexString(HMACSHA256.HashData(_key, body)).ToLowerInvariant());
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException("The analytics worker rejected the request.", null, response.StatusCode);
            if (response.Content.Headers.ContentLength > 2 * 1024 * 1024) throw new InvalidOperationException("Analytics response is too large.");
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            buffer = new MemoryStream();
            chunk = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(chunk, deadline.Token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + count > 2 * 1024 * 1024) throw new InvalidOperationException("Analytics response is too large.");
                buffer.Write(chunk, 0, count);
            }
            buffer.Position = 0;
            var output = JsonDocument.Parse(buffer);
            try
            {
                AnalyticsValidation.ValidateOutput(output.RootElement, input, taskId);
                await PrivacyJsonInspection.DemandUnchangedAsync(_firewall, output.RootElement, context with { Destination = EgressDestination.Output }, deadline.Token).ConfigureAwait(false);
                return output;
            }
            catch { output.Dispose(); throw; }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(body);
            if (chunk is not null) CryptographicOperations.ZeroMemory(chunk);
            if (buffer is not null)
            {
                if (buffer.TryGetBuffer(out var bytes)) CryptographicOperations.ZeroMemory(bytes.AsSpan());
                buffer.Dispose();
            }
        }
    }

    private static HttpClient CreateHttp(Uri endpoint, bool allowInsecureHttp)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri || !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !(endpoint.Scheme == Uri.UriSchemeHttps || allowInsecureHttp && endpoint.Scheme == Uri.UriSchemeHttp))
            throw new ArgumentException("A trusted analytics endpoint is required.", nameof(endpoint));
        return new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false })
            { Timeout = TimeSpan.FromMinutes(2) };
    }

    /// <summary>Clears the signing key and disposes the owned transport.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CryptographicOperations.ZeroMemory(_key);
        if (_ownsClient) _http.Dispose();
    }
}
