using System.Net.WebSockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenConnect.Abstractions;
using OpenConnect.Hosting.AspNetCore.Execution;

namespace OpenConnect.Hosting.AspNetCore;

internal static class VoiceRealtimeWebSocketHandler
{
    private const int MaxFrameBytes = 96 * 1024;
    private const long MaxPcmBytes = 32 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task HandleAsync(HttpContext context, string sessionId, CancellationToken cancellationToken)
    {
        if (!context.WebSockets.IsWebSocketRequest || !TryIdentity(context, out var tenant, out var principal) ||
            !UlidTryParse(sessionId, out var sessionValue))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var sessions = context.RequestServices.GetService<InMemorySessionStore>();
        var session = sessions?.Get(sessionValue);
        if (session is null || session.TenantId.Value != tenant || session.PrincipalId.Value != principal)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        using var browser = await context.WebSockets.AcceptWebSocketAsync("openconnect.voice.v1").ConfigureAwait(false);
        using var upstream = new ClientWebSocket();
        var configuration = context.RequestServices.GetRequiredService<IConfiguration>();
        var workerToken = configuration["OPENCONNECT_WORKER_TOKEN"];
        var workerEndpoint = configuration["Workers:Transcription:Endpoint"];
        if (string.IsNullOrWhiteSpace(workerToken) || !Uri.TryCreate(workerEndpoint, UriKind.Absolute, out var endpoint))
        {
            await CloseAsync(browser, WebSocketCloseStatus.PolicyViolation, "voice_unavailable", cancellationToken).ConfigureAwait(false);
            return;
        }

        upstream.Options.SetRequestHeader("Authorization", $"Bearer {workerToken}");
        var realtimeUri = new UriBuilder(endpoint) { Scheme = endpoint.Scheme == "https" ? "wss" : "ws", Path = "/internal/v1/realtime", Query = string.Empty }.Uri;
        try
        {
            await upstream.ConnectAsync(realtimeUri, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            await CloseAsync(browser, WebSocketCloseStatus.EndpointUnavailable, "transcription_unavailable", cancellationToken).ConfigureAwait(false);
            return;
        }

        var handshake = await ReceiveTextAsync(browser, cancellationToken).ConfigureAwait(false);
        if (handshake is null || !TryValidateHandshake(handshake, sessionId, out var captureId, out var speechEpoch))
        {
            await CloseAsync(browser, WebSocketCloseStatus.InvalidPayloadData, "invalid_handshake", cancellationToken).ConfigureAwait(false);
            return;
        }

        // The worker emits the provider session acknowledgement before it starts
        // consuming audio. Wait for that boundary so queued browser PCM cannot
        // reach Meta before the provider handshake is complete.
        var workerHandshake = await ReceiveWorkerMessageAsync(upstream, cancellationToken).ConfigureAwait(false);
        if (workerHandshake is null)
        {
            await CloseAsync(browser, WebSocketCloseStatus.EndpointUnavailable, "transcription_unavailable", cancellationToken).ConfigureAwait(false);
            return;
        }
        await browser.SendAsync(workerHandshake.Value.Payload, workerHandshake.Value.MessageType, true, cancellationToken).ConfigureAwait(false);

        // The worker owns the provider handshake and credentials. The browser handshake is
        // host-owned metadata only and must never be forwarded to Meta.
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("OpenConnect.VoiceRealtime");
        var relayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.RequestAborted);
        var browserToWorker = RelayBrowserToWorkerAsync(browser, upstream, captureId, speechEpoch, relayCancellation.Token);
        var workerToBrowser = RelayWorkerToBrowserAsync(upstream, browser, context, sessionId, captureId, speechEpoch, tenant, principal, relayCancellation.Token, logger);
        await Task.WhenAny(browserToWorker, workerToBrowser).ConfigureAwait(false);
        relayCancellation.Cancel();
        try
        { await Task.WhenAll(browserToWorker, workerToBrowser).ConfigureAwait(false); }
        catch (OperationCanceledException) when (relayCancellation.IsCancellationRequested) { }
        catch (Exception exception) { logger.LogWarning("Realtime voice relay stopped with {ExceptionType}.", exception.GetType().Name); }
        relayCancellation.Dispose();
    }

    private static async Task RelayBrowserToWorkerAsync(WebSocket browser, WebSocket worker, string captureId, long speechEpoch, CancellationToken token)
    {
        var buffer = new byte[MaxFrameBytes];
        long total = 0;
        while (browser.State == WebSocketState.Open && worker.State == WebSocketState.Open && !token.IsCancellationRequested)
        {
            var result = await browser.ReceiveAsync(buffer, token).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                break;
            if (result.MessageType == WebSocketMessageType.Binary)
            {
                if (result.Count == 0 || result.Count > MaxFrameBytes || (result.Count & 1) != 0 || (total += result.Count) > MaxPcmBytes)
                    throw new InvalidDataException("PCM16 frame is invalid or exceeds the capture limit.");
                await worker.SendAsync(buffer.AsMemory(0, result.Count), WebSocketMessageType.Binary, result.EndOfMessage, token).ConfigureAwait(false);
            }
            else if (result.MessageType == WebSocketMessageType.Text)
            {
                var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
                if (IsEndStreamControl(text))
                    await worker.SendAsync(Encoding.UTF8.GetBytes("{\"type\":\"endStream\"}"), WebSocketMessageType.Text, true, token).ConfigureAwait(false);
            }
        }
    }

    internal static async Task RelayWorkerToBrowserAsync(WebSocket worker, WebSocket browser, HttpContext context, string sessionId, string captureId, long speechEpoch, Ulid tenant, Ulid principal, CancellationToken token, ILogger logger)
    {
        var buffer = new byte[MaxFrameBytes];
        string? bestFinalTranscript = null;
        string? accumulatedFinalTranscript = null;
        var admitted = false;
        var providerFailed = false;
        try
        {
            while (worker.State == WebSocketState.Open && browser.State == WebSocketState.Open && !token.IsCancellationRequested)
            {
                var result = await worker.ReceiveAsync(buffer, token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    logger.LogInformation("Realtime transcription worker closed with status {CloseStatus}.", result.CloseStatus);
                    break;
                }
                if (result.MessageType != WebSocketMessageType.Text)
                    continue;
                var provider = Encoding.UTF8.GetString(buffer, 0, result.Count);
                using var document = JsonDocument.Parse(provider);
                var root = document.RootElement;
                var type = root.TryGetProperty("type", out var typeValue) ? typeValue.GetString() : null;
                LogProviderEvent(logger, type);
                var text = root.TryGetProperty("text", out var textValue) ? textValue.GetString() : root.TryGetProperty("transcript", out var transcript) ? transcript.GetString() : null;
                var isFinal = root.TryGetProperty("final", out var finalValue) && finalValue.ValueKind == JsonValueKind.True;
                var speechComplete = string.Equals(type, "speechComplete", StringComparison.OrdinalIgnoreCase);
                var providerError = string.Equals(type, "error", StringComparison.OrdinalIgnoreCase);
                providerFailed |= providerError;
                if (speechComplete && !string.IsNullOrWhiteSpace(text))
                    isFinal = true;
                if (string.IsNullOrWhiteSpace(text) && !speechComplete && !providerError)
                    continue;

                var outgoingText = providerError ? "Voice transcription provider unavailable." : text?.Trim() ?? string.Empty;
                if (isFinal && !string.IsNullOrWhiteSpace(text))
                {
                    accumulatedFinalTranscript = MergeTranscriptSegment(accumulatedFinalTranscript, text.Trim());
                    outgoingText = accumulatedFinalTranscript;
                    if (outgoingText.Length > (bestFinalTranscript?.Length ?? 0))
                        bestFinalTranscript = outgoingText;
                }

                if (!providerFailed && !admitted && speechComplete && !string.IsNullOrWhiteSpace(bestFinalTranscript))
                    admitted = await AdmitFinalTranscriptAsync(context, sessionId, captureId, speechEpoch,
                        bestFinalTranscript, tenant, principal, token).ConfigureAwait(false);

                var outgoingType = providerError ? "error" : isFinal ? "transcript-final" : speechComplete ? "speech-complete" : "transcript-partial";
                await SendJsonAsync(browser, new { type = outgoingType, captureId, speechEpoch, text = outgoingText, authoritative = isFinal }, token).ConfigureAwait(false);
            }
        }
        catch (WebSocketException)
        {
            // Preserve the best authoritative transcript even when the provider closes
            // the socket immediately after its final event.
        }

        if (!providerFailed && !token.IsCancellationRequested && !admitted && !string.IsNullOrWhiteSpace(bestFinalTranscript))
            await AdmitFinalTranscriptAsync(context, sessionId, captureId, speechEpoch, bestFinalTranscript, tenant, principal, token).ConfigureAwait(false);
    }

    internal static void LogProviderEvent(ILogger logger, string? type)
    {
        if (string.Equals(type, "error", StringComparison.OrdinalIgnoreCase))
            logger.LogWarning("Realtime transcription provider reported an error.");
        else
            logger.LogInformation("Realtime transcription event type {EventType}.",
                string.Equals(type, "speechComplete", StringComparison.OrdinalIgnoreCase) ? "speechComplete" : "other");
    }

    private static async Task<bool> AdmitFinalTranscriptAsync(HttpContext context, string sessionId, string captureId, long speechEpoch, string transcript, Ulid tenant, Ulid principal, CancellationToken token)
    {
        var dispatcher = context.RequestServices.GetService<IHostTaskExecutionDispatcher>();
        if (dispatcher is null || context.RequestServices.GetService<IHostTaskExecutionAuthorizer>() is null || context.RequestServices.GetService<IHostTaskExecutor>() is null)
            return false;
        var idempotency = $"voice:{captureId}:{speechEpoch}";
        var request = new TaskRequest(idempotency, transcript, sessionId);
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{request.ClientTaskId}\n{transcript}\n{sessionId}")));
        var result = await dispatcher.AcceptAsync(request, idempotency, fingerprint,
            new TenantId(tenant), new PrincipalId(principal), token).ConfigureAwait(false);
        return result.Status == ExecutionAdmissionStatus.Accepted;
    }

    private static string MergeTranscriptSegment(string? accumulated, string segment)
    {
        if (string.IsNullOrWhiteSpace(accumulated))
            return segment;
        if (segment.StartsWith(accumulated, StringComparison.OrdinalIgnoreCase))
            return segment;
        if (accumulated.EndsWith(segment, StringComparison.OrdinalIgnoreCase))
            return accumulated;
        return $"{accumulated} {segment}";
    }

    private static bool TryValidateHandshake(string json, string expectedSession, out string captureId, out long speechEpoch)
    {
        captureId = string.Empty;
        speechEpoch = 0;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("sessionId", out var session) || !string.Equals(session.GetString(), expectedSession, StringComparison.Ordinal) ||
                !root.TryGetProperty("captureId", out var capture) || !Guid.TryParse(capture.GetString(), out _) ||
                !root.TryGetProperty("speechEpoch", out var epoch) || !epoch.TryGetInt64(out speechEpoch) || speechEpoch <= 0 ||
                !root.TryGetProperty("sampleRate", out var rate) || rate.GetInt32() != 24_000 ||
                !root.TryGetProperty("channels", out var channels) || channels.GetInt32() != 1 ||
                !root.TryGetProperty("bitsPerSample", out var bits) || bits.GetInt32() != 16)
                return false;
            captureId = capture.GetString()!;
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static bool IsEndStreamControl(string text)
    {
        if (string.Equals(text, "endStream", StringComparison.Ordinal))
            return true;
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("type", out var type)
                && string.Equals(type.GetString(), "endStream", StringComparison.Ordinal);
        }
        catch (JsonException) { return false; }
    }

    private static async Task<string?> ReceiveTextAsync(WebSocket socket, CancellationToken token)
    {
        var buffer = new byte[4096];
        var result = await socket.ReceiveAsync(buffer, token).ConfigureAwait(false);
        return result.MessageType == WebSocketMessageType.Text && result.EndOfMessage ? Encoding.UTF8.GetString(buffer, 0, result.Count) : null;
    }

    private static async Task<(WebSocketMessageType MessageType, byte[] Payload)?> ReceiveWorkerMessageAsync(WebSocket socket, CancellationToken token)
    {
        var buffer = new byte[MaxFrameBytes];
        var result = await socket.ReceiveAsync(buffer, token).ConfigureAwait(false);
        if (result.MessageType is not (WebSocketMessageType.Text or WebSocketMessageType.Binary))
            return null;
        return (result.MessageType, buffer[..result.Count]);
    }

    private static Task SendJsonAsync(WebSocket socket, object value, CancellationToken token) => socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, JsonOptions)), WebSocketMessageType.Text, true, token);
    private static Task CloseAsync(WebSocket socket, WebSocketCloseStatus status, string reason, CancellationToken token) => socket.CloseAsync(status, reason, token);
    private static bool TryIdentity(HttpContext context, out Ulid tenant, out Ulid principal)
    {
        tenant = default;
        principal = default;
        return UlidTryParse(context.User.FindFirstValue("tenant_id"), out tenant) && UlidTryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.User.FindFirstValue("sub"), out principal);
    }
    private static bool UlidTryParse(string? text, out Ulid value) { try { value = Ulid.Parse(text ?? string.Empty); return value != default && string.Equals(value.ToString(), text, StringComparison.Ordinal); } catch (FormatException) { value = default; return false; } }
}
