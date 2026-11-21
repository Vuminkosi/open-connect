using System.Net.Http.Headers;
using System.Text.Json;

namespace OpenConnect.LiveUI.Inference;

/// <summary>HTTPS Cloud transport that does not follow redirects or retain cookies.</summary>
public sealed class CloudLiveUIHttpTransport : ICloudLiveUITransport, IDisposable
{
    private const int ResponseLimit = 262144;
    private readonly HttpClient _client;
    private readonly string _credential;

    /// <summary>Creates a transport for a configured Cloud origin and server-side credential.</summary>
    public CloudLiveUIHttpTransport(Uri origin, string credential)
        : this(origin, credential, new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(5) })
    {
    }

    internal CloudLiveUIHttpTransport(Uri origin, string credential, HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(handler);
        if (!origin.IsAbsoluteUri || origin.Scheme != Uri.UriSchemeHttps || origin.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(origin.UserInfo) || !string.IsNullOrEmpty(origin.Query) || !string.IsNullOrEmpty(origin.Fragment))
            throw new ArgumentException("A Cloud HTTPS origin is required.", nameof(origin));
        if (string.IsNullOrEmpty(credential) || credential.Length > 160 || credential.Any(character => character <= ' ' || character > '~' || character == ','))
            throw new ArgumentException("A Cloud server credential is required.", nameof(credential));
        _credential = credential;
        _client = new HttpClient(handler, disposeHandler: true) { BaseAddress = origin, Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <inheritdoc />
    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using var response = await _client.GetAsync("/health/ready", HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentEncoding.Count != 0) return false;
            var bytes = await ReadBoundedAsync(response.Content, 8192, timeout.Token).ConfigureAwait(false);
            if (bytes is null) return false;
            try
            {
                using var document = JsonDocument.Parse(bytes);
                var root = document.RootElement;
                return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("status", out var status) && status.GetString() == "ready" &&
                    root.TryGetProperty("capabilities", out var capabilities) && capabilities.ValueKind == JsonValueKind.Array &&
                    capabilities.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.String && item.GetString() == "inference.liveui");
            }
            finally { Array.Clear(bytes); }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidOperationException or IOException) { return false; }
    }

    /// <inheritdoc />
    public async Task<CloudLiveUITransportResult> SendAsync(ReadOnlyMemory<byte> request, CancellationToken cancellationToken = default)
    {
        if (request.Length is < 1 or > 131072) return new(503, []);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(120));
        var body = request.ToArray();
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, "/cloud/v1/liveui/plan");
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _credential);
            message.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
            message.Content = new ByteArrayContent(body);
            message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return new((int)response.StatusCode, []);
            if (response.Content.Headers.ContentEncoding.Count != 0) return new(503, []);
            var bytes = await ReadBoundedAsync(response.Content, ResponseLimit, timeout.Token).ConfigureAwait(false);
            return bytes is null ? new(503, []) : new((int)response.StatusCode, bytes);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new(503, []); }
        catch (Exception exception) when (exception is HttpRequestException or IOException) { return new(503, []); }
        finally { Array.Clear(body); }
    }

    private static async Task<byte[]?> ReadBoundedAsync(HttpContent content, int limit, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > limit) return null;
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var result = new MemoryStream(limit);
        var buffer = new byte[8192];
        try
        {
            int count;
            while ((count = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) != 0)
            {
                if (result.Length + count > limit) return null;
                await result.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            }
            return result.ToArray();
        }
        finally { Array.Clear(buffer); if (result.TryGetBuffer(out var stored)) Array.Clear(stored.Array!); }
    }

    /// <inheritdoc />
    public void Dispose() => _client.Dispose();
}
