using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace OpenConnect.Client.Cloud;

public sealed partial class OpenConnectCloudClient : IOpenConnectCloudClient
{
    private const int MaxStandardResponseBytes = 2 * 1024 * 1024;
    private const int MaxVoiceResponseBytes = 12 * 1024 * 1024;
    private const int MaxProblemResponseBytes = 64 * 1024;
    private const int MaxDetailLength = 512;

    private static readonly TimeSpan MaxEnvelopeDeadline = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly Regex W3CTraceparentPattern = new(
        "^00-(?!0{32}$)[0-9a-f]{32}-(?!0{16}$)[0-9a-f]{16}-[0-9a-f]{2}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly HttpClient _httpClient;
    private readonly OpenConnectCloudOptions _options;

    public OpenConnectCloudClient(HttpClient httpClient, IOptions<OpenConnectCloudOptions> options)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
    }

    public Task<CloudReadinessResult> GetHealthReadyAsync(CancellationToken cancellationToken = default) =>
        SendGetAsync<CloudReadinessResult>("cloud/v1/health/ready", MaxStandardResponseBytes, cancellationToken);

    public async Task<CloudCapabilitiesResult> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, "cloud/v1/capabilities");
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        var payload = await ReadBoundedBytesAsync(response.Content, MaxStandardResponseBytes, cancellationToken).ConfigureAwait(false);
        var traceId = GetHeaderValue(response, "x-trace-id");
        return ParseCapabilitiesResult(payload, traceId);
    }

    public Task<CloudClassificationResponse> ClassifyAsync(CloudClassificationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Input);

        var hasText = !string.IsNullOrEmpty(request.Input.Text);
        var hasRef = !string.IsNullOrEmpty(request.Input.InputRef);
        if (hasText == hasRef)
        {
            throw new ArgumentException("Classification input must specify exactly one of Text or InputRef.", nameof(request));
        }

        if (hasText && request.Input.Text!.Length > 20_000)
        {
            throw new ArgumentException("Classification text must be at most 20000 characters.", nameof(request));
        }

        if (hasRef && request.Input.InputRef!.Length > 2_048)
        {
            throw new ArgumentException("Classification inputRef must be at most 2048 characters.", nameof(request));
        }

        var envelope = new
        {
            schemaVersion = "openconnect.cloud.classification/v2",
            requestId = ResolveRequestId(request.RequestId),
            capability = "classification.classify",
            expiresAt = FormatExpiresAt(request.ExpiresAtUtc),
            configuration = request.Configuration,
            input = request.Input,
        };

        return SendPostAsync<CloudClassificationResponse>("cloud/v1/classification", envelope, MaxStandardResponseBytes, cancellationToken);
    }

    public Task<CloudChatResponse> ChatAsync(CloudChatRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Input);

        if (string.IsNullOrWhiteSpace(request.ModelId) || request.ModelId.Length > 256)
        {
            throw new ArgumentException("ModelId must contain 1 to 256 characters.", nameof(request));
        }

        if (request.Input.Messages is null || request.Input.Messages.Count is < 1 or > 64)
        {
            throw new ArgumentException("Chat input must contain between 1 and 64 messages.", nameof(request));
        }

        foreach (var message in request.Input.Messages)
        {
            if (message is null || string.IsNullOrWhiteSpace(message.Role) || message.Content is null || message.Content.Length > 32_768)
            {
                throw new ArgumentException("Each chat message must specify a role and content of at most 32768 characters.", nameof(request));
            }
        }

        if (request.Input.MaxTokens is < 1 or > 4_096)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "MaxTokens must be between 1 and 4096.");
        }

        var envelope = new
        {
            schemaVersion = "openconnect.cloud.inference/v1",
            requestId = ResolveRequestId(request.RequestId),
            capability = "inference.chat",
            expiresAt = FormatExpiresAt(request.ExpiresAtUtc),
            modelId = request.ModelId,
            input = request.Input,
        };

        return SendPostAsync<CloudChatResponse>("cloud/v1/inference/chat", envelope, MaxStandardResponseBytes, cancellationToken);
    }

    public Task<CloudLiveUIPlanResponse> PlanLiveUIAsync(CloudLiveUIPlanRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Input);
        ArgumentNullException.ThrowIfNull(request.Input.Context);
        ArgumentNullException.ThrowIfNull(request.Input.Generation);

        if (string.IsNullOrWhiteSpace(request.Input.SchemaVersion) || request.Input.SchemaVersion.Length > 32)
        {
            throw new ArgumentException("LiveUI input schemaVersion must contain 1 to 32 characters.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.Input.Intent) || request.Input.Intent.Length > 8_000)
        {
            throw new ArgumentException("LiveUI intent must contain 1 to 8000 characters.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.Input.Context.Route) || request.Input.Context.Route.Length > 256)
        {
            throw new ArgumentException("LiveUI context route must contain 1 to 256 characters.", nameof(request));
        }

        if (request.Input.Context.AvailableActions is null || request.Input.Context.AvailableActions.Count > 256)
        {
            throw new ArgumentException("LiveUI availableActions must contain at most 256 actions.", nameof(request));
        }

        if (request.Input.Generation.MaxActions is < 1 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "LiveUI MaxActions must be between 1 and 32.");
        }

        if (request.Input.Generation.MaxOutputTokens is < 16 or > 512)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "LiveUI MaxOutputTokens must be between 16 and 512.");
        }

        var envelope = new
        {
            schemaVersion = "openconnect.cloud.liveui/v1",
            requestId = ResolveRequestId(request.RequestId),
            capability = "inference.liveui",
            expiresAt = FormatExpiresAt(request.ExpiresAtUtc),
            input = request.Input,
        };

        return SendPostAsync<CloudLiveUIPlanResponse>("cloud/v1/liveui/plan", envelope, MaxStandardResponseBytes, cancellationToken);
    }

    public Task<CloudSpeechSynthesisResponse> SynthesizeSpeechAsync(CloudSpeechSynthesisRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Input);

        if (string.IsNullOrWhiteSpace(request.Input.Text) || request.Input.Text.Length > 2_000)
        {
            throw new ArgumentException("Speech synthesis text must contain 1 to 2000 non-whitespace characters.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.Input.VoiceId) || request.Input.VoiceId.Length > 64)
        {
            throw new ArgumentException("Speech synthesis voiceId must contain 1 to 64 characters.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.Input.Locale) || request.Input.Locale.Length > 16)
        {
            throw new ArgumentException("Speech synthesis locale must contain 1 to 16 characters.", nameof(request));
        }

        if (request.Input.Format is not ("wav" or "pcm"))
        {
            throw new ArgumentException("Speech synthesis format must be 'wav' or 'pcm'.", nameof(request));
        }

        var envelope = new
        {
            schemaVersion = "openconnect.cloud.voice-synthesis/v1",
            requestId = ResolveRequestId(request.RequestId),
            capability = "voice.synthesize",
            expiresAt = FormatExpiresAt(request.ExpiresAtUtc),
            input = request.Input,
        };

        return SendPostAsync<CloudSpeechSynthesisResponse>("cloud/v1/voice/synthesis", envelope, MaxVoiceResponseBytes, cancellationToken);
    }

    private async Task<T> SendGetAsync<T>(string relativePath, int maxResponseBytes, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, relativePath);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync<T>(response.Content, maxResponseBytes, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> SendPostAsync<T>(string relativePath, object payload, int maxResponseBytes, CancellationToken cancellationToken)
    {
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        using var request = CreateRequest(HttpMethod.Post, relativePath);
        request.Content = new ByteArrayContent(jsonBytes);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
        {
            CharSet = Encoding.UTF8.WebName,
        };

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync<T>(response.Content, maxResponseBytes, cancellationToken).ConfigureAwait(false);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string relativePath)
    {
        var requestUri = _httpClient.BaseAddress is not null
            ? new Uri(relativePath, UriKind.Relative)
            : new Uri(new Uri(_options.Endpoint!.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute), relativePath);

        var request = new HttpRequestMessage(method, requestUri);
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (_options.Authentication == CloudAuthenticationMode.ApiKey && !string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        }

        request.Headers.TryAddWithoutValidation("traceparent", ResolveTraceparent());
        return request;
    }

    private static string ResolveTraceparent()
    {
        var current = Activity.Current;
        if (current is not null
            && current.IdFormat == ActivityIdFormat.W3C
            && !string.IsNullOrWhiteSpace(current.Id)
            && W3CTraceparentPattern.IsMatch(current.Id))
        {
            return current.Id;
        }

        var traceId = ActivityTraceId.CreateRandom().ToHexString();
        var spanId = ActivitySpanId.CreateRandom().ToHexString();
        return $"00-{traceId}-{spanId}-01";
    }

    private static string ResolveRequestId(string? requestId)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            return Guid.NewGuid().ToString("D");
        }

        if (!Guid.TryParse(requestId, out var parsed) || parsed == Guid.Empty)
        {
            throw new ArgumentException("RequestId must be a non-empty UUID.", nameof(requestId));
        }

        return parsed.ToString("D");
    }

    private string FormatExpiresAt(DateTimeOffset? expiresAtUtc)
    {
        var now = DateTimeOffset.UtcNow;
        var effective = expiresAtUtc?.ToUniversalTime() ?? now.Add(_options.RequestTimeout);
        if (effective <= now)
        {
            throw new ArgumentOutOfRangeException(nameof(expiresAtUtc), "ExpiresAtUtc must be in the future.");
        }

        if (effective - now > MaxEnvelopeDeadline)
        {
            effective = now.Add(MaxEnvelopeDeadline);
        }

        return effective.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var headerTraceId = GetHeaderValue(response, "x-trace-id");
        CloudProblemDetails? problem = null;

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        var isJsonLike = string.Equals(mediaType, "application/problem+json", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase);

        if (isJsonLike)
        {
            try
            {
                var bytes = await ReadBoundedBytesAsync(response.Content, MaxProblemResponseBytes, cancellationToken).ConfigureAwait(false);
                if (bytes.Length > 0)
                {
                    problem = JsonSerializer.Deserialize<CloudProblemDetails>(bytes, JsonOptions);
                }
            }
            catch (JsonException)
            {
                problem = null;
            }
            catch (InvalidDataException)
            {
                problem = null;
            }
        }

        var code = !string.IsNullOrWhiteSpace(problem?.Code)
            ? problem.Code
            : MapFallbackCode(response.StatusCode);
        var title = !string.IsNullOrWhiteSpace(problem?.Title)
            ? problem.Title
            : MapFallbackTitle(response.StatusCode);
        var traceId = !string.IsNullOrWhiteSpace(problem?.TraceId)
            ? problem.TraceId
            : headerTraceId;
        var retryable = problem?.Retryable ?? false;
        var detail = SanitizeOptionalDetail(problem?.Detail);

        throw new OpenConnectCloudException(
            response.StatusCode,
            code,
            title,
            traceId,
            retryable,
            detail,
            problem);
    }

    private static async Task<T> ReadJsonAsync<T>(HttpContent content, int maxBytes, CancellationToken cancellationToken)
    {
        var bytes = await ReadBoundedBytesAsync(content, maxBytes, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(bytes, JsonOptions)
            ?? throw new InvalidDataException("The OpenConnect Cloud service returned an empty JSON response.");
    }

    private static async Task<byte[]> ReadBoundedBytesAsync(HttpContent content, int maxBytes, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is long contentLength && contentLength > maxBytes)
        {
            throw new InvalidDataException($"The OpenConnect Cloud response exceeded the maximum allowed size of {maxBytes} bytes.");
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[Math.Min(16_384, maxBytes)];
        var total = 0;

        while (true)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > maxBytes)
            {
                throw new InvalidDataException($"The OpenConnect Cloud response exceeded the maximum allowed size of {maxBytes} bytes.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static CloudCapabilitiesResult ParseCapabilitiesResult(byte[] payload, string? headerTraceId)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The OpenConnect Cloud capabilities response must be a JSON object.");
        }

        var traceId = root.TryGetProperty("traceId", out var traceIdElement) && traceIdElement.ValueKind == JsonValueKind.String
            ? traceIdElement.GetString()
            : headerTraceId;

        var items = new List<CloudCapabilityDescriptor>();
        if (root.TryGetProperty("capabilities", out var capsElement))
        {
            if (capsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in capsElement.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var capabilityName = TryGetString(entry, "capability") ?? TryGetString(entry, "name");
                    if (string.IsNullOrWhiteSpace(capabilityName))
                    {
                        continue;
                    }

                    var status = TryGetString(entry, "status") ?? "unavailable";
                    var streaming = entry.TryGetProperty("streamingSupported", out var streamEl)
                        && streamEl.ValueKind == JsonValueKind.True;
                    items.Add(new CloudCapabilityDescriptor(capabilityName, status, streaming));
                }
            }
            else if (capsElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in capsElement.EnumerateObject())
                {
                    if (property.Value.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var status = TryGetString(property.Value, "status") ?? "unavailable";
                    var streaming = property.Value.TryGetProperty("streamingSupported", out var streamEl)
                        && streamEl.ValueKind == JsonValueKind.True;
                    items.Add(new CloudCapabilityDescriptor(property.Name, status, streaming));
                }
            }
        }

        return new CloudCapabilitiesResult(items, traceId);
    }

    private static string? TryGetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    private static string? GetHeaderValue(HttpResponseMessage response, string headerName) =>
        response.Headers.TryGetValues(headerName, out var values) ? values.FirstOrDefault() : null;

    private static string? SanitizeOptionalDetail(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return null;
        }

        return detail.Length <= MaxDetailLength ? detail : detail[..MaxDetailLength];
    }

    private static string MapFallbackCode(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.Unauthorized => "unauthorized",
        HttpStatusCode.Forbidden => "forbidden",
        HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity or HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.UnsupportedMediaType => "invalid_request",
        HttpStatusCode.Conflict => "conflict",
        HttpStatusCode.TooManyRequests => "rate_limited",
        HttpStatusCode.GatewayTimeout => "upstream_timeout",
        _ => "capability_unavailable",
    };

    private static string MapFallbackTitle(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.Unauthorized => "Authentication required",
        HttpStatusCode.Forbidden => "Access denied",
        HttpStatusCode.BadRequest => "Invalid request",
        HttpStatusCode.Conflict => "Request conflict",
        HttpStatusCode.TooManyRequests => "Rate limit exceeded",
        HttpStatusCode.GatewayTimeout => "Request deadline exceeded",
        _ => "Cloud capability unavailable",
    };
}
