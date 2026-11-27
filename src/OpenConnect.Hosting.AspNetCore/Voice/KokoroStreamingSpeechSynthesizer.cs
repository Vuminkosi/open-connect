using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenConnect.Abstractions.Speech;

namespace OpenConnect.Hosting.AspNetCore.Voice;

/// <summary>Streams validated 24 kHz mono PCM16LE speech chunks from the Kokoro worker <c>WS /internal/v2/speech/stream</c> route.</summary>
public sealed class KokoroStreamingSpeechSynthesizer : IStreamingSpeechSynthesizer
{
    private const int MaxControlMessageBytes = 32 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly Uri? _streamUri;
    private readonly string? _workerToken;
    private readonly ILogger<KokoroStreamingSpeechSynthesizer> _logger;

    /// <summary>Creates a streaming synthesizer configured from host <see cref="IConfiguration"/> (<c>Workers:Kokoro:Endpoint</c> and <c>OPENCONNECT_WORKER_TOKEN</c>).</summary>
    public KokoroStreamingSpeechSynthesizer(
        IConfiguration configuration,
        ILogger<KokoroStreamingSpeechSynthesizer>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _logger = logger ?? NullLogger<KokoroStreamingSpeechSynthesizer>.Instance;
        _workerToken = configuration["OPENCONNECT_WORKER_TOKEN"];
        if (TryCreateStreamUri(configuration["Workers:Kokoro:Endpoint"], out var uri))
        {
            _streamUri = uri;
        }
    }

    /// <inheritdoc />
    public bool IsConfigured => _streamUri is not null && !string.IsNullOrWhiteSpace(_workerToken);

    /// <inheritdoc />
    public IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizePcmStreamAsync(
        string text,
        string? voice = null,
        string? locale = null,
        float speed = 1.0f,
        CancellationToken cancellationToken = default) =>
        SynthesizePcmStreamAsync(
            text,
            voice,
            locale,
            speed,
            responseId: string.Empty,
            segmentId: string.Empty,
            speechEpoch: 0,
            cancellationToken);

    internal async IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizePcmStreamAsync(
        string text,
        string? voice,
        string? locale,
        float speed,
        string responseId,
        string segmentId,
        long speechEpoch,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!IsConfigured || _streamUri is null || string.IsNullOrWhiteSpace(_workerToken))
        {
            throw new InvalidOperationException("Streaming Kokoro speech synthesis is not configured.");
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        var normalizedVoice = string.IsNullOrWhiteSpace(voice) ? "af_heart" : voice.Trim();
        var normalizedLocale = string.IsNullOrWhiteSpace(locale) ? "en-US" : locale.Trim();
        var normalizedSpeed = float.IsFinite(speed) && speed > 0f ? speed : 0.95f;

        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", $"Bearer {_workerToken}");
        await socket.ConnectAsync(_streamUri, cancellationToken).ConfigureAwait(false);

        var requestPayload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "synthesize",
            text = text.Trim(),
            voice = normalizedVoice,
            locale = normalizedLocale,
            speed = normalizedSpeed,
            responseId = responseId ?? string.Empty,
            segmentId = segmentId ?? string.Empty,
            speechEpoch = Math.Max(0, speechEpoch)
        }, JsonOptions);

        await socket.SendAsync(requestPayload, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);

        using var cancelRegistration = cancellationToken.Register(static state =>
        {
            var (ws, respId, segId, epoch) = ((ClientWebSocket, string, string, long))state!;
            if (ws.State == WebSocketState.Open)
            {
                _ = SendBestEffortCancelAsync(ws, respId, segId, epoch);
            }
        }, (socket, responseId ?? string.Empty, segmentId ?? string.Empty, Math.Max(0, speechEpoch)));

        var receiveBuffer = new byte[VoiceBinaryFrameCodec.MaxFrameBytes];
        try
        {
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                ValueWebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(receiveBuffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        yield break;
                    }

                    var maxBytes = result.MessageType == WebSocketMessageType.Binary
                        ? VoiceBinaryFrameCodec.MaxFrameBytes
                        : MaxControlMessageBytes;
                    if (message.Length + result.Count > maxBytes)
                    {
                        throw new InvalidDataException("Kokoro streaming frame exceeded maximum allowed size.");
                    }

                    if (result.Count > 0)
                    {
                        message.Write(receiveBuffer, 0, result.Count);
                    }
                }
                while (!result.EndOfMessage);

                var length = (int)message.Length;
                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    if (length == 0 || (length & 1) != 0)
                    {
                        throw new InvalidDataException("Kokoro streaming PCM16 frame has invalid byte length.");
                    }

                    var chunk = new byte[length];
                    Buffer.BlockCopy(message.GetBuffer(), 0, chunk, 0, length);
                    yield return chunk;
                    continue;
                }

                if (result.MessageType != WebSocketMessageType.Text || length == 0)
                {
                    continue;
                }

                var controlJson = Encoding.UTF8.GetString(message.GetBuffer(), 0, length);
                var controlKind = ParseControlMessage(controlJson, out var errorCode);
                if (controlKind is "stream.completed" or "stream.cancelled")
                {
                    yield break;
                }

                if (controlKind == "stream.error")
                {
                    if (string.Equals(errorCode, "overloaded", StringComparison.OrdinalIgnoreCase))
                    {
                        VoiceDuplexMetrics.RecordOverload("tts_worker");
                    }

                    _logger.LogWarning("Kokoro streaming synthesis reported error code {ErrorCode}.", errorCode ?? "unknown");
                    throw new InvalidOperationException($"Kokoro streaming synthesis failed ({errorCode ?? "unknown"}).");
                }
            }
        }
        finally
        {
            Array.Clear(receiveBuffer);
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    using var closeCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "stream_done", closeCts.Token).ConfigureAwait(false);
                }
                catch
                {
                    // Ignore transport teardown errors.
                }
            }
        }
    }

    internal static bool TryCreateStreamUri(string? value, out Uri? result)
    {
        result = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var endpoint)
            || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(endpoint.UserInfo)
            || !string.IsNullOrEmpty(endpoint.Query)
            || !string.IsNullOrEmpty(endpoint.Fragment))
        {
            return false;
        }

        result = new UriBuilder(endpoint)
        {
            Scheme = endpoint.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
            Path = "/internal/v2/speech/stream"
        }.Uri;
        return true;
    }

    private static string? ParseControlMessage(string json, out string? errorCode)
    {
        errorCode = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var type = doc.RootElement.TryGetProperty("type", out var typeProp) && typeProp.ValueKind == JsonValueKind.String
                ? typeProp.GetString()
                : null;
            if (doc.RootElement.TryGetProperty("code", out var codeProp) && codeProp.ValueKind == JsonValueKind.String)
            {
                errorCode = codeProp.GetString();
            }

            return type;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task SendBestEffortCancelAsync(ClientWebSocket socket, string responseId, string segmentId, long speechEpoch)
    {
        try
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                type = "cancel",
                responseId,
                segmentId,
                speechEpoch
            }, JsonOptions);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            await socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort cancellation notification; socket teardown will also cancel the worker.
        }
    }
}
