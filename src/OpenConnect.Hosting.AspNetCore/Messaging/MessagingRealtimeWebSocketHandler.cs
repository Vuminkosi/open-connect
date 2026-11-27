using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Messaging;
using OpenConnect.Abstractions.Serialization;

namespace OpenConnect.Hosting.AspNetCore.Messaging;

/// <summary>
/// User/device-scoped multiplexed WebSocket handler for <c>WSS /openconnect/v1/messaging/realtime</c>.
/// Independent of active chat/composer state; forbids access tokens in query strings; enforces origin
/// validation, device revocation checks, bounded per-connection send queues, and heartbeats.
/// </summary>
public static class MessagingRealtimeWebSocketHandler
{
    public const string SubProtocol = "openconnect.messaging.v1";
    private const int MaxInboundFrameBytes = 32 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = OpenConnectHttpJson.CreateOptions();

    public static async Task HandleAsync(HttpContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var options = context.RequestServices.GetService<IOptions<MessagingHostingOptions>>()?.Value;
        if (options is null || !options.Enabled)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        // Security gate: NEVER accept access/bearer tokens in query strings (leaks into proxy/server logs).
        if (context.Request.Query.ContainsKey("access_token") ||
            context.Request.Query.ContainsKey("token") ||
            context.Request.Query.ContainsKey("bearer"))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var origin = context.Request.Headers.Origin.FirstOrDefault();
        if (!IsOriginAllowed(context, options, origin))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        TenantId tenantId;
        PrincipalId principalId;
        DeviceId? deviceId = null;

        var ticketParam = context.Request.Query["ticket"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(ticketParam))
        {
            var ticketService = context.RequestServices.GetService<IMessagingWebSocketTicketService>();
            if (ticketService is null)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }

            var ticket = await ticketService.TryConsumeAsync(ticketParam, origin, cancellationToken).ConfigureAwait(false);
            if (ticket is null)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            tenantId = ticket.TenantId;
            principalId = ticket.PrincipalId;
            deviceId = ticket.DeviceId;
        }
        else
        {
            if (context.User.Identity?.IsAuthenticated != true ||
                !TryExtractIdentity(context.User, out tenantId, out principalId, out deviceId))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
        }

        // Verify device is not revoked before accepting the WebSocket upgrade.
        if (deviceId.HasValue)
        {
            var deviceStore = context.RequestServices.GetService<IDeviceEnrollmentStore>();
            if (deviceStore is not null)
            {
                var device = await deviceStore.GetDeviceAsync(tenantId, deviceId.Value, cancellationToken).ConfigureAwait(false);
                if (device is null || device.PrincipalId != principalId || device.Status == DeviceStatus.Revoked)
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }
            }
        }

        var hub = context.RequestServices.GetService<IMessagingRealtimeHub>() as MessagingRealtimeHub;
        var acceptedProtocol = context.WebSockets.WebSocketRequestedProtocols.Contains(SubProtocol, StringComparer.Ordinal)
            ? SubProtocol
            : null;

        using var socket = acceptedProtocol is not null
            ? await context.WebSockets.AcceptWebSocketAsync(acceptedProtocol).ConfigureAwait(false)
            : await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);

        await PumpConnectionAsync(socket, hub, tenantId, principalId, deviceId, options, context.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task PumpConnectionAsync(
        WebSocket socket,
        MessagingRealtimeHub? hub,
        TenantId tenantId,
        PrincipalId principalId,
        DeviceId? deviceId,
        MessagingHostingOptions options,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var registration = hub?.RegisterConnection(tenantId, principalId, deviceId, cancellationToken);
        var connToken = registration?.Token ?? cancellationToken;

        try
        {
            var sendTask = registration is not null
                ? SendLoopAsync(socket, registration, options.WebSocketHeartbeatInterval, timeProvider, connToken)
                : HeartbeatOnlyLoopAsync(socket, options.WebSocketHeartbeatInterval, timeProvider, connToken);
            var receiveTask = ReceiveLoopAsync(socket, connToken);

            await Task.WhenAny(sendTask, receiveTask).ConfigureAwait(false);

            if (registration?.CloseReason is { } closeReason && socket.State == WebSocketState.Open)
            {
                var status = string.Equals(closeReason, "device_revoked", StringComparison.Ordinal)
                    ? WebSocketCloseStatus.PolicyViolation
                    : WebSocketCloseStatus.PolicyViolation;
                await SafeCloseAsync(socket, status, closeReason).ConfigureAwait(false);
            }
            else if (socket.State == WebSocketState.Open)
            {
                await SafeCloseAsync(socket, WebSocketCloseStatus.NormalClosure, "closed").ConfigureAwait(false);
            }
        }
        finally
        {
            if (registration is not null && hub is not null)
                hub.UnregisterConnection(registration.ConnectionId);
        }
    }

    private static async Task SendLoopAsync(
        WebSocket socket,
        MessagingRealtimeHub.RealtimeConnection connection,
        TimeSpan heartbeatInterval,
        TimeProvider timeProvider,
        CancellationToken token)
    {
        var interval = heartbeatInterval <= TimeSpan.Zero ? TimeSpan.FromSeconds(30) : heartbeatInterval;
        while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
        {
            using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            var readTask = connection.Reader.WaitToReadAsync(heartbeatCts.Token).AsTask();
            var delayTask = Task.Delay(interval, timeProvider, heartbeatCts.Token);

            var winner = await Task.WhenAny(readTask, delayTask).ConfigureAwait(false);
            await heartbeatCts.CancelAsync().ConfigureAwait(false);

            if (token.IsCancellationRequested)
                break;

            if (winner == readTask)
            {
                if (!await readTask.ConfigureAwait(false))
                    break;

                while (connection.Reader.TryRead(out var envelope))
                {
                    if (socket.State != WebSocketState.Open || token.IsCancellationRequested)
                        return;

                    var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
                    await socket.SendAsync(bytes, WebSocketMessageType.Text, true, token).ConfigureAwait(false);
                }
            }
            else
            {
                var heartbeat = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    schemaVersion = 1,
                    type = "messaging.heartbeat",
                    occurredAt = timeProvider.GetUtcNow()
                }, JsonOptions);
                await socket.SendAsync(heartbeat, WebSocketMessageType.Text, true, token).ConfigureAwait(false);
            }
        }
    }

    private static async Task HeartbeatOnlyLoopAsync(
        WebSocket socket,
        TimeSpan heartbeatInterval,
        TimeProvider timeProvider,
        CancellationToken token)
    {
        var interval = heartbeatInterval <= TimeSpan.Zero ? TimeSpan.FromSeconds(30) : heartbeatInterval;
        while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
        {
            await Task.Delay(interval, timeProvider, token).ConfigureAwait(false);
            var heartbeat = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1,
                type = "messaging.heartbeat",
                occurredAt = timeProvider.GetUtcNow()
            }, JsonOptions);
            await socket.SendAsync(heartbeat, WebSocketMessageType.Text, true, token).ConfigureAwait(false);
        }
    }

    private static async Task ReceiveLoopAsync(WebSocket socket, CancellationToken token)
    {
        var buffer = new byte[MaxInboundFrameBytes];
        try
        {
            while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;
                if (!result.EndOfMessage || result.Count > MaxInboundFrameBytes)
                {
                    await SafeCloseAsync(socket, WebSocketCloseStatus.MessageTooBig, "frame_too_large").ConfigureAwait(false);
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (WebSocketException) { }
    }

    private static async Task SafeCloseAsync(WebSocket socket, WebSocketCloseStatus status, string description)
    {
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await socket.CloseAsync(status, description, cts.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            // Best-effort close on teardown.
        }
    }

    internal static bool IsOriginAllowed(HttpContext context, MessagingHostingOptions options, string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin))
        {
            // Non-browser native mobile/desktop clients may omit Origin when using Bearer/ticket auth.
            return true;
        }

        if (options.AllowedWebSocketOrigins.Count > 0)
            return options.AllowedWebSocketOrigins.Contains(origin.Trim());

        if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri))
            return false;

        var host = context.Request.Host;
        if (!host.HasValue)
            return false;

        var expectedScheme = context.Request.Scheme;
        var originPort = originUri.IsDefaultPort ? (int?)null : originUri.Port;
        return string.Equals(originUri.Scheme, expectedScheme, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(originUri.Host, host.Host, StringComparison.OrdinalIgnoreCase) &&
               originPort == host.Port;
    }

    private static bool TryExtractIdentity(ClaimsPrincipal user, out TenantId tenantId, out PrincipalId principalId, out DeviceId? deviceId)
    {
        tenantId = default;
        principalId = default;
        deviceId = null;

        var tenantClaim = user.FindFirstValue("tenant_id");
        var principalClaim = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub");
        if (!TryParseUlid(tenantClaim, out var tenantUlid) || !TryParseUlid(principalClaim, out var principalUlid))
            return false;

        tenantId = new TenantId(tenantUlid);
        principalId = new PrincipalId(principalUlid);

        var deviceClaim = user.FindFirstValue("device_id");
        if (!string.IsNullOrWhiteSpace(deviceClaim))
        {
            if (!TryParseUlid(deviceClaim, out var deviceUlid))
                return false;
            deviceId = new DeviceId(deviceUlid);
        }

        return true;
    }

    private static bool TryParseUlid(string? value, out Ulid ulid)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            ulid = default;
            return false;
        }
        try
        {
            ulid = Ulid.Parse(value);
            return true;
        }
        catch (FormatException)
        {
            ulid = default;
            return false;
        }
    }
}
