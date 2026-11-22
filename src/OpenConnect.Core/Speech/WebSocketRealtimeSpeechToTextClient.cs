using System.Diagnostics;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenConnect.Abstractions.Speech;

namespace OpenConnect.Core.Speech;

/// <summary>WebSocket realtime speech-to-text client. Connects over <c>wss://</c> (or loopback <c>ws://</c>),
/// sends the provider handshake JSON text frame within the configured timeout, streams 24 kHz mono 16-bit PCM
/// in 7680-byte binary frames paced at realtime (48,000 B/s), sends <c>{"type":"endStream"}</c>, and collects
/// finalized transcript turns until socket close. API keys are never logged or included in exceptions.</summary>
public sealed class WebSocketRealtimeSpeechToTextClient : IRealtimeSpeechToTextClient
{
    private const int PcmFrameBytes = 7680;
    private const double PcmBytesPerSecond = 48_000d;
    private readonly SpeechToTextOptions _options;

    /// <summary>Initializes a new instance of <see cref="WebSocketRealtimeSpeechToTextClient"/>.</summary>
    public WebSocketRealtimeSpeechToTextClient(IOptions<SpeechToTextOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public bool IsConfigured => _options.HasRealtimeRoute;

    /// <summary>Builds the JSON handshake text frame for the configured realtime speech-to-text route.</summary>
    public string BuildHandshake()
    {
        if (!_options.HasRealtimeRoute)
        {
            throw new InvalidOperationException("Realtime speech-to-text route is not configured.");
        }

        return JsonSerializer.Serialize(new
        {
            authorization = new { accessToken = "Bearer " + _options.ApiKey },
            audioEncoding = _options.AudioEncoding,
            model = _options.Model,
            mode = _options.Mode
        });
    }

    /// <inheritdoc />
    public async Task<RealtimeTranscriptionResult> TranscribePcmAsync(
        Stream pcm24kMono16Bit,
        CancellationToken cancellationToken = default,
        Func<RealtimePartialTranscript, CancellationToken, ValueTask>? onPartialTranscript = null,
        Func<RealtimeTranscriptTurn, CancellationToken, ValueTask>? onFinalTranscript = null)
    {
        ArgumentNullException.ThrowIfNull(pcm24kMono16Bit);
        if (!_options.TryGetValidRealtimeUri(out var realtimeUri) || string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            throw new InvalidOperationException("Realtime speech-to-text route is not configured.");
        }

        var handshake = BuildHandshake();
        var handshakeTimeoutSeconds = Math.Clamp(_options.HandshakeTimeoutSeconds, 1, 120);

        using var socket = new ClientWebSocket();
        // On Windows SChannel, TLS 1.2 avoids intermittent WebSocket stream receive failures
        // with providers whose TLS 1.3 handshake/record framing conflicts with SChannel defaults.
        using var windowsTransport = OperatingSystem.IsWindows()
            && realtimeUri!.Scheme.Equals("wss", StringComparison.OrdinalIgnoreCase)
            ? new HttpMessageInvoker(new SocketsHttpHandler
            {
                SslOptions = new SslClientAuthenticationOptions
                {
                    EnabledSslProtocols = SslProtocols.Tls12
                }
            })
            : null;

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(9));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        using var handshakeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(handshakeTimeoutSeconds));
        using var handshakeLinked = CancellationTokenSource.CreateLinkedTokenSource(linked.Token, handshakeTimeout.Token);

        try
        {
            if (windowsTransport is null)
            {
                await socket.ConnectAsync(realtimeUri!, handshakeLinked.Token).ConfigureAwait(false);
            }
            else
            {
                await socket.ConnectAsync(realtimeUri!, windowsTransport, handshakeLinked.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException($"Realtime speech-to-text connect failed ({ex.GetType().Name}).");
        }

        await SendTextAsync(socket, handshake, handshakeLinked.Token).ConfigureAwait(false);
        var sessionId = await ReadHandshakeAckAsync(socket, handshakeLinked.Token).ConfigureAwait(false);

        var transcripts = new RealtimeTranscriptCollector();
        var receiver = ReceiveUntilCloseAsync(
            socket, transcripts, onPartialTranscript, onFinalTranscript, linked.Token);

        var inputCompleted = false;
        async Task SendAudioAsync()
        {
            var buffer = new byte[PcmFrameBytes];
            var audioClock = Stopwatch.StartNew();
            long sentBytes = 0;
            try
            {
                int read;
                while ((read = await pcm24kMono16Bit.ReadAsync(buffer, linked.Token).ConfigureAwait(false)) > 0)
                {
                    await socket.SendAsync(
                        new ArraySegment<byte>(buffer, 0, read),
                        WebSocketMessageType.Binary,
                        endOfMessage: true,
                        linked.Token).ConfigureAwait(false);

                    // Pace buffered microphone frames and prerecorded streams at realtime (48,000 B/s)
                    // so we do not burst faster than bounded streaming provider buffers accept.
                    sentBytes += read;
                    var remaining = TimeSpan.FromSeconds(sentBytes / PcmBytesPerSecond) - audioClock.Elapsed;
                    if (remaining > TimeSpan.Zero)
                    {
                        await Task.Delay(remaining, linked.Token).ConfigureAwait(false);
                    }
                }

                inputCompleted = true;
                await SendTextAsync(socket, """{"type":"endStream"}""", linked.Token).ConfigureAwait(false);
            }
            finally
            {
                Array.Clear(buffer);
            }
        }

        var sender = SendAudioAsync();
        try
        {
            var first = await Task.WhenAny(sender, receiver).ConfigureAwait(false);
            await first.ConfigureAwait(false);
            if (first == receiver && !inputCompleted)
            {
                throw new InvalidOperationException("Realtime speech-to-text closed before the transcription request completed.");
            }

            await Task.WhenAll(sender, receiver).ConfigureAwait(false);
        }
        finally
        {
            await linked.CancelAsync().ConfigureAwait(false);
            try
            {
                await Task.WhenAll(sender, receiver).ConfigureAwait(false);
            }
            catch
            {
                // Both tasks are observed; preserve the primary failure above.
            }
        }

        return new RealtimeTranscriptionResult(sessionId, transcripts.Turns);
    }

    private static Task SendTextAsync(ClientWebSocket socket, string text, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, endOfMessage: true, ct);
    }

    private static async Task<string> ReadHandshakeAckAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var frame = await ReceiveTextAsync(socket, ct).ConfigureAwait(false);
        if (frame is null)
        {
            throw new InvalidOperationException("Realtime speech-to-text closed before acknowledging.");
        }

        try
        {
            using var doc = JsonDocument.Parse(frame);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("sessionId", out var id)
                && id.ValueKind == JsonValueKind.String)
            {
                return id.GetString() ?? string.Empty;
            }
        }
        catch (JsonException)
        {
        }

        throw new InvalidOperationException("Realtime speech-to-text handshake was not acknowledged.");
    }

    private static async Task ReceiveUntilCloseAsync(
        ClientWebSocket socket,
        RealtimeTranscriptCollector transcripts,
        Func<RealtimePartialTranscript, CancellationToken, ValueTask>? onPartialTranscript,
        Func<RealtimeTranscriptTurn, CancellationToken, ValueTask>? onFinalTranscript,
        CancellationToken ct)
    {
        while (socket.State == WebSocketState.Open)
        {
            var text = await ReceiveTextAsync(socket, ct).ConfigureAwait(false);
            if (text is null)
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("type", out var type)
                    && type.ValueKind == JsonValueKind.String
                    && string.Equals(type.GetString(), "error", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Realtime speech-to-text rejected the transcription stream.");
                }
            }
            catch (JsonException)
            {
            }

            if (onPartialTranscript is not null
                && transcripts.TryReadPartial(text, out var partial)
                && partial is not null)
            {
                try
                {
                    await onPartialTranscript(partial, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // Display-only partials must not interrupt provider audio or final transcription.
                }
            }
            else if (transcripts.TryAppend(text, out var finalTranscript)
                && finalTranscript is not null)
            {
                if (onFinalTranscript is not null)
                {
                    try
                    {
                        await onFinalTranscript(finalTranscript, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch
                    {
                        // Turn notification is advisory to the caller; keep consuming provider output.
                    }
                }
            }

            ct.ThrowIfCancellationRequested();
        }
    }

    private static async Task<string?> ReceiveTextAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var collected = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                try
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None).ConfigureAwait(false);
                }
                catch (WebSocketException)
                {
                }

                return null;
            }

            if (result.MessageType == WebSocketMessageType.Binary)
            {
                return null;
            }

            collected.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return Encoding.UTF8.GetString(collected.ToArray());
    }
}
