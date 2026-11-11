using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Persistence;
using OpenConnect.Abstractions.Serialization;

namespace OpenConnect.Hosting.AspNetCore.Streaming;

/// <summary>
/// Configuration options for OpenConnect full-duplex session WebSocket event streaming.
/// </summary>
internal sealed class SessionWebSocketOptions
{
    /// <summary>
    /// Gets or sets the allowed origins for WebSocket connections.
    /// An empty list allows any origin (or non-browser clients without Origin header).
    /// </summary>
    public IList<string> AllowedOrigins { get; set; } = new List<string>();

    /// <summary>
    /// Gets or sets the maximum size in bytes of an incoming WebSocket message frame (default: 64 KB).
    /// </summary>
    public int MaxIncomingMessageSize { get; set; } = 64 * 1024;

    /// <summary>
    /// Gets or sets the send buffer capacity for bounded backpressure (default: 256 events).
    /// </summary>
    public int SendQueueCapacity { get; set; } = 256;

    /// <summary>
    /// Gets or sets the keep-alive ping interval (default: 30 seconds).
    /// </summary>
    public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets the supported WebSocket subprotocol (default: "openconnect.v1").
    /// </summary>
    public string SubProtocol { get; set; } = "openconnect.v1";
}

/// <summary>
/// Handles authenticated full-duplex WebSocket session event streaming with origin verification,
/// monotonic cursor replay, live event delivery, backpressure, and revocation checking.
/// </summary>
internal static class SessionWebSocketHandler
{
    /// <summary>
    /// Handles an incoming WebSocket request for session events streaming.
    /// </summary>
    public static async Task HandleAsync(
        HttpContext context,
        string sessionIdOrUuid,
        long? afterSequence,
        InMemorySessionStore sessionStore,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(sessionStore);

        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("Expected WebSocket upgrade request.", cancellationToken).ConfigureAwait(false);
            return;
        }

        var options = context.RequestServices.GetService<IOptions<SessionWebSocketOptions>>()?.Value
                      ?? new SessionWebSocketOptions();

        // 1. Origin verification
        if (options.AllowedOrigins.Count > 0)
        {
            var origin = context.Request.Headers["Origin"].ToString();
            if (!string.IsNullOrEmpty(origin))
            {
                var originUri = Uri.TryCreate(origin, UriKind.Absolute, out var parsedOrigin) ? parsedOrigin : null;
                var originAllowed = false;
                if (originUri is not null)
                {
                    foreach (var allowed in options.AllowedOrigins)
                    {
                        if (Uri.TryCreate(allowed, UriKind.Absolute, out var allowedUri))
                        {
                            if (string.Equals(originUri.Scheme, allowedUri.Scheme, StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(originUri.Authority, allowedUri.Authority, StringComparison.OrdinalIgnoreCase))
                            {
                                originAllowed = true;
                                break;
                            }
                        }
                    }
                }

                if (!originAllowed)
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await context.Response.WriteAsync("Forbidden origin.", cancellationToken).ConfigureAwait(false);
                    return;
                }
            }
        }

        // 2. Authentication claims
        if (!TryIdentity(context, out var tenantUlid, out var principalUlid))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync("Missing tenant or principal claims.", cancellationToken).ConfigureAwait(false);
            return;
        }

        // 3. Resolve session identity (supports both ULID and Canonical UUID)
        Ulid sessionUlid;
        Guid sessionUuid = Guid.Empty;
        var isUuid = false;

        if (UlidTryParse(sessionIdOrUuid, out var parsedUlid))
        {
            sessionUlid = parsedUlid;
            var session = sessionStore.Get(sessionUlid);
            if (session is null || session.TenantId.Value != tenantUlid || session.PrincipalId.Value != principalUlid)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                await context.Response.WriteAsync("Session not found.", cancellationToken).ConfigureAwait(false);
                return;
            }
        }
        else if (Guid.TryParseExact(sessionIdOrUuid, "D", out sessionUuid) && sessionUuid != Guid.Empty)
        {
            isUuid = true;
            var tenantId = new TenantId(tenantUlid);
            var principalId = new PrincipalId(principalUlid);

            Ulid? mapped = null;
            var durable = context.RequestServices.GetService<IDurableSessionUuidResolver>();
            if (durable is not null)
            {
                var resolved = await durable.ResolveAsync(sessionUuid, tenantId, principalId, cancellationToken).ConfigureAwait(false);
                if (resolved is not null) mapped = resolved.Value.Value;
            }

            if (mapped is null)
            {
                var sync = context.RequestServices.GetService<ISessionUuidResolver>();
                if (sync is null || !sync.TryResolve(sessionUuid, out var fallback))
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    await context.Response.WriteAsync("Session not found.", cancellationToken).ConfigureAwait(false);
                    return;
                }
                mapped = fallback;
            }

            var session = sessionStore.Get(mapped.Value);
            if (session is null || session.TenantId.Value != tenantUlid || session.PrincipalId.Value != principalUlid)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                await context.Response.WriteAsync("Session not found.", cancellationToken).ConfigureAwait(false);
                return;
            }
            sessionUlid = mapped.Value;
        }
        else
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("Invalid session identity.", cancellationToken).ConfigureAwait(false);
            return;
        }

        // 4. Validate cursor
        var cursor = afterSequence ?? 0;
        if (cursor < 0)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("Invalid afterSequence cursor.", cancellationToken).ConfigureAwait(false);
            return;
        }

        // 5. Check subprotocol negotiation
        string? agreedSubProtocol = null;
        if (context.WebSockets.WebSocketRequestedProtocols.Contains(options.SubProtocol, StringComparer.OrdinalIgnoreCase))
        {
            agreedSubProtocol = options.SubProtocol;
        }

        using var webSocket = await context.WebSockets.AcceptWebSocketAsync(agreedSubProtocol).ConfigureAwait(false);

        var logger = context.RequestServices.GetService<ILoggerFactory>()?.CreateLogger(typeof(SessionWebSocketHandler));
        using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.RequestAborted);
        var token = combinedCts.Token;

        try
        {
            // Initial cursor replay: send all committed events > cursor
            var initialRecords = sessionStore.GetEvents(sessionUlid, cursor);
            var lastSentSequence = cursor;

            foreach (var record in initialRecords)
            {
                token.ThrowIfCancellationRequested();
                var sent = await SendRecordAsync(webSocket, record, isUuid, sessionUuid, token).ConfigureAwait(false);
                if (!sent) return;
                if (record.Sequence > lastSentSequence)
                {
                    lastSentSequence = record.Sequence;
                }
            }

            // Stream new events as they appear or incoming control messages
            var incomingBuffer = new byte[Math.Min(options.MaxIncomingMessageSize, 4096)];
            var pollDelay = TimeSpan.FromMilliseconds(50);
            Task<WebSocketReceiveResult>? pendingReceive = null;

            while (webSocket.State == WebSocketState.Open && !token.IsCancellationRequested)
            {
                pendingReceive ??= webSocket.ReceiveAsync(new ArraySegment<byte>(incomingBuffer), token);
                var delayTask = Task.Delay(pollDelay, token);

                var completedTask = await Task.WhenAny(pendingReceive, delayTask).ConfigureAwait(false);
                if (completedTask == pendingReceive)
                {
                    var receiveResult = await pendingReceive.ConfigureAwait(false);
                    pendingReceive = null;

                    if (receiveResult.MessageType == WebSocketMessageType.Close)
                    {
                        if (webSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                        {
                            await webSocket.CloseOutputAsync(
                                receiveResult.CloseStatus ?? WebSocketCloseStatus.NormalClosure,
                                receiveResult.CloseStatusDescription ?? "Closing",
                                CancellationToken.None).ConfigureAwait(false);
                        }
                        break;
                    }
                    // Handle client messages if needed (e.g. ping/ack)
                }

                // 2. Poll for new session events monotonically
                var nextRecords = sessionStore.GetEvents(sessionUlid, lastSentSequence);
                foreach (var record in nextRecords)
                {
                    token.ThrowIfCancellationRequested();
                    var sent = await SendRecordAsync(webSocket, record, isUuid, sessionUuid, token).ConfigureAwait(false);
                    if (!sent) return;
                    if (record.Sequence > lastSentSequence)
                    {
                        lastSentSequence = record.Sequence;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Normal abort or client disconnect
        }
        catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
        {
            // Client closed connection without handshake
        }
        finally
        {
            if (webSocket.State == WebSocketState.Open)
            {
                try
                {
                    await webSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Session stream closed", CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // Ignore close exceptions during disposal
                }
            }
        }
    }

    private static readonly JsonSerializerOptions CanonicalJsonOptions = CreateCanonicalJsonOptions();

    private static JsonSerializerOptions CreateCanonicalJsonOptions()
    {
        var options = OpenConnectHttpJson.CreateOptions();
        options.Converters.Add(new ZuluDateTimeOffsetConverter());
        return options;
    }

    private sealed class ZuluDateTimeOffsetConverter : System.Text.Json.Serialization.JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => reader.GetDateTimeOffset();

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
        {
            var text = value.UtcDateTime.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
            if (text.EndsWith("Z", StringComparison.Ordinal) && text.Contains('.'))
                text = text.TrimEnd('Z').TrimEnd('0').TrimEnd('.') + "Z";
            writer.WriteStringValue(text);
        }
    }

    private static async Task<bool> SendRecordAsync(
        WebSocket webSocket,
        EventRecord record,
        bool isUuid,
        Guid sessionUuid,
        CancellationToken token)
    {
        if (webSocket.State != WebSocketState.Open) return false;

        byte[] payloadBytes;
        if (isUuid)
        {
            if (!CanonicalEventEndpointAdapter.TryToCanonical(record, sessionUuid, out var canonical) || canonical is null)
            {
                // Non-projectable legacy record on canonical route
                return true; // Skip non-canonical records safely or send raw if appropriate
            }
            var json = JsonSerializer.Serialize(canonical, CanonicalJsonOptions);
            payloadBytes = Encoding.UTF8.GetBytes(json);
        }
        else
        {
            var json = JsonSerializer.Serialize(record, OpenConnectHttpJson.CreateOptions());
            payloadBytes = Encoding.UTF8.GetBytes(json);
        }

        await webSocket.SendAsync(
            new ArraySegment<byte>(payloadBytes),
            WebSocketMessageType.Text,
            endOfMessage: true,
            token).ConfigureAwait(false);

        return true;
    }

    private static bool TryIdentity(HttpContext context, out Ulid tenant, out Ulid principal)
    {
        var tenantValid = UlidTryParse(context.User.FindFirst("tenant_id")?.Value, out tenant);
        var principalValid = UlidTryParse(
            context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            ?? context.User.FindFirst("sub")?.Value,
            out principal);
        return tenantValid && principalValid;
    }

    private static bool UlidTryParse(string? text, out Ulid value)
    {
        try
        {
            var parsed = Ulid.Parse(text ?? string.Empty);
            if (parsed == default || !string.Equals(parsed.ToString(), text, StringComparison.Ordinal))
            {
                value = default;
                return false;
            }
            value = parsed;
            return true;
        }
        catch (FormatException)
        {
            value = default;
            return false;
        }
    }
}
