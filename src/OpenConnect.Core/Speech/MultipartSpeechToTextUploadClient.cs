using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenConnect.Abstractions.Speech;

namespace OpenConnect.Core.Speech;

/// <summary>Multipart HTTP(S) whole-recording speech-to-text upload client. Sends <c>request</c> (JSON settings)
/// and <c>audio</c> parts with a Bearer Authorization header while enforcing <see cref="SpeechToTextOptions.MaxAudioBytes"/>
/// client-side. API keys are never logged or included in exceptions.</summary>
public sealed class MultipartSpeechToTextUploadClient : ISpeechToTextUploadClient
{
    private static readonly HttpClient SharedHttpClient = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        AllowAutoRedirect = false,
        UseCookies = false
    });

    private readonly HttpClient _http;
    private readonly SpeechToTextOptions _options;

    /// <summary>Initializes a new instance using a caller-supplied <see cref="HttpClient"/>.</summary>
    public MultipartSpeechToTextUploadClient(HttpClient httpClient, IOptions<SpeechToTextOptions> options)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        _http = httpClient;
        _options = options.Value ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>Initializes a new instance using the shared internal <see cref="HttpClient"/>.</summary>
    public MultipartSpeechToTextUploadClient(IOptions<SpeechToTextOptions> options)
        : this(SharedHttpClient, options)
    {
    }

    /// <inheritdoc />
    public bool IsConfigured => _options.HasUploadRoute;

    /// <inheritdoc />
    public async Task<SpeechToTextUploadResult> TranscribeAsync(
        Stream audio,
        string fileName,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);

        if (!_options.TryGetValidUploadUri(out var uploadUri) || string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            throw new InvalidOperationException("Speech-to-text upload route is not configured.");
        }

        var maxBytes = _options.MaxAudioBytes > 0
            ? _options.MaxAudioBytes
            : SpeechToTextOptions.DefaultMaxAudioBytes;

        if (audio.CanSeek && audio.Length > maxBytes)
        {
            throw new ArgumentException($"Audio recording exceeds the maximum allowed size of {maxBytes} bytes.", nameof(audio));
        }

        var settings = JsonSerializer.Serialize(new
        {
            model = _options.Model,
            audioEncoding = "WAV",
            mode = _options.Mode,
            language = _options.Language
        });

        using var boundedAudio = new BoundedReadStream(audio, maxBytes);
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(settings, Encoding.UTF8, "application/json"), "request");
        var audioContent = new StreamContent(boundedAudio);
        audioContent.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        content.Add(audioContent, "audio", fileName);

        using var request = new HttpRequestMessage(HttpMethod.Post, uploadUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        request.Content = content;

        int status;
        string body;
        try
        {
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            status = (int)response.StatusCode;
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException($"Speech-to-text upload transport failed ({ex.GetType().Name}).");
        }

        if (status is < 200 or >= 300)
        {
            throw new InvalidOperationException($"Speech-to-text upload refused the request (HTTP {status}).");
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("Speech-to-text upload returned an unreadable envelope.");
            }

            var sessionId = GetString(root, "sessionId");
            var transcript = GetString(root, "transcript");
            var duration = GetLong(root, "audioDurationMs");
            var turns = new List<SpeechToTextTurn>();
            if (root.TryGetProperty("turns", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in list.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    turns.Add(new SpeechToTextTurn(
                        GetInt(item, "turnId"),
                        GetLong(item, "startMs"),
                        GetLong(item, "endMs"),
                        GetString(item, "transcript"),
                        GetString(item, "speaker")));
                }
            }

            return new SpeechToTextUploadResult(sessionId, transcript, duration, turns);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new InvalidOperationException("Speech-to-text upload returned an unreadable envelope.");
        }
    }

    private static int GetInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;

    private static long GetLong(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

    private static string GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

    private sealed class BoundedReadStream(Stream inner, long maxBytes) : Stream
    {
        private long _totalRead;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            EnforceLimit(read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            EnforceLimit(read);
            return read;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var read = await inner.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
            EnforceLimit(read);
            return read;
        }

        private void EnforceLimit(int read)
        {
            if (read <= 0)
            {
                return;
            }

            _totalRead += read;
            if (_totalRead > maxBytes)
            {
                throw new ArgumentException($"Audio recording exceeds the maximum allowed size of {maxBytes} bytes.");
            }
        }
    }
}
