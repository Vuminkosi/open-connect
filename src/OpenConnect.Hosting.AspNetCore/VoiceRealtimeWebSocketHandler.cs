using System.Collections.Concurrent;
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
    private static readonly ConcurrentDictionary<string, byte> CommittedTurns = new(StringComparer.Ordinal);
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
        try { await Task.WhenAll(browserToWorker, workerToBrowser).ConfigureAwait(false); }
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
            if (result.MessageType == WebSocketMessageType.Close) break;
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

    private static async Task RelayWorkerToBrowserAsync(WebSocket worker, WebSocket browser, HttpContext context, string sessionId, string captureId, long speechEpoch, Ulid tenant, Ulid principal, CancellationToken token, ILogger logger)
    {
        var buffer = new byte[MaxFrameBytes];
        string? bestFinalTranscript = null;
        while (worker.State == WebSocketState.Open && browser.State == WebSocketState.Open && !token.IsCancellationRequested)
        {
            var result = await worker.ReceiveAsync(buffer, token).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                logger.LogInformation("Realtime transcription worker closed with status {CloseStatus}.", result.CloseStatus);
                break;
            }
            if (result.MessageType != WebSocketMessageType.Text) continue;
            var provider = Encoding.UTF8.GetString(buffer, 0, result.Count);
            using var document = JsonDocument.Parse(provider);
            var root = document.RootElement;
            var type = root.TryGetProperty("type", out var typeValue) ? typeValue.GetString() : null;
            logger.LogInformation("Realtime transcription event type {EventType}.", type ?? "unknown");
            if (string.Equals(type, "error", StringComparison.OrdinalIgnoreCase))
            {
                var errorCode = root.TryGetProperty("code", out var codeValue) ? codeValue.GetString() : null;
                var errorDetail = root.TryGetProperty("message", out var messageValue) && messageValue.ValueKind == JsonValueKind.String ? messageValue.GetString() : root.TryGetProperty("error", out var errorValue) && errorValue.ValueKind == JsonValueKind.String ? errorValue.GetString() : null;
                logger.LogWarning("Realtime transcription provider error code {ErrorCode} detail {ErrorDetail}.", errorCode ?? "unspecified", string.IsNullOrWhiteSpace(errorDetail) ? "unspecified" : errorDetail[..Math.Min(errorDetail.Length, 120)]);
            }
            var text = root.TryGetProperty("text", out var textValue) ? textValue.GetString() : root.TryGetProperty("transcript", out var transcript) ? transcript.GetString() : null;
            var isFinal = root.TryGetProperty("final", out var finalValue) && finalValue.ValueKind == JsonValueKind.True;
            var speechComplete = string.Equals(type, "speechComplete", StringComparison.OrdinalIgnoreCase);
            var providerError = string.Equals(type, "error", StringComparison.OrdinalIgnoreCase);
            if (speechComplete && !string.IsNullOrWhiteSpace(text)) isFinal = true;
            if (string.IsNullOrWhiteSpace(text) && !speechComplete && !providerError) continue;

            var outgoingType = providerError ? "error" : isFinal ? "transcript-final" : speechComplete ? "speech-complete" : "transcript-partial";
            await SendJsonAsync(browser, new { type = outgoingType, captureId, speechEpoch, text = providerError ? "Voice transcription provider unavailable." : text?.Trim() ?? string.Empty, authoritative = isFinal }, token).ConfigureAwait(false);
            if (isFinal && !string.IsNullOrWhiteSpace(text) && text.Trim().Length > (bestFinalTranscript?.Length ?? 0))
                bestFinalTranscript = text.Trim();
        }

        if (!string.IsNullOrWhiteSpace(bestFinalTranscript))
            await AdmitFinalTranscriptAsync(context, sessionId, captureId, speechEpoch, bestFinalTranscript, tenant, principal, token).ConfigureAwait(false);
    }

    private static async Task AdmitFinalTranscriptAsync(HttpContext context, string sessionId, string captureId, long speechEpoch, string transcript, Ulid tenant, Ulid principal, CancellationToken token)
    {
        var key = $"{tenant}:{principal}:{sessionId}:{captureId}:{speechEpoch}";
        if (!CommittedTurns.TryAdd(key, 0)) return;
        var dispatcher = context.RequestServices.GetService<IHostTaskExecutionDispatcher>();
        if (dispatcher is null || context.RequestServices.GetService<IHostTaskExecutionAuthorizer>() is null || context.RequestServices.GetService<IHostTaskExecutor>() is null)
            return;
        var idempotency = $"voice:{captureId}:{speechEpoch}";
        var taskMessage = NormalizeTodoTranscript(transcript);
        var request = new TaskRequest(Guid.NewGuid().ToString("N"), taskMessage, sessionId);
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{request.ClientTaskId}\n{taskMessage}\n{sessionId}")));
        await dispatcher.AcceptAsync(request, idempotency, fingerprint, new TenantId(tenant), new PrincipalId(principal), token).ConfigureAwait(false);
    }

    private static string NormalizeTodoTranscript(string transcript)
    {
        if (!transcript.Contains("create", StringComparison.OrdinalIgnoreCase)) return transcript;
        return transcript
            .Replace("toto", "Todo", StringComparison.OrdinalIgnoreCase)
            .Replace("waybridge", "weighbridge", StringComparison.OrdinalIgnoreCase)
            .Replace("weybridge", "weighbridge", StringComparison.OrdinalIgnoreCase)
            .Replace("weigh bridge", "weighbridge", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryValidateHandshake(string json, string expectedSession, out string captureId, out long speechEpoch)
    {
        captureId = string.Empty; speechEpoch = 0;
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
        if (string.Equals(text, "endStream", StringComparison.Ordinal)) return true;
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
        if (result.MessageType is not (WebSocketMessageType.Text or WebSocketMessageType.Binary)) return null;
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
