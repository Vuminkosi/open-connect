using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace OpenConnect.Samples.Blazor;

/// <summary>
/// Keeps the browser voice socket same-origin while the server connects to the
/// separately deployed OpenConnect gateway with its trusted development identity.
/// Browser JavaScript never receives gateway or worker credentials.
/// </summary>
public static class ExternalGatewayVoiceProxy
{
    public static void Map(WebApplication app, TodoGatewayOptions options, IConfiguration configuration)
    {
        app.Map("/openconnect/v1/voice/realtime/{sessionId}", async (HttpContext context, string sessionId, CancellationToken cancellationToken) =>
        {
            if (!context.WebSockets.IsWebSocketRequest || !IsSameOrigin(context))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            if (!Uri.TryCreate(configuration["GATEWAY_URL"] ?? configuration["OpenConnect:Gateway:Url"], UriKind.Absolute, out var baseUri))
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }

            using var browser = await context.WebSockets.AcceptWebSocketAsync("openconnect.voice.v1").ConfigureAwait(false);
            using var gateway = new ClientWebSocket();
            gateway.Options.AddSubProtocol("openconnect.voice.v1");
            gateway.Options.SetRequestHeader("X-OpenConnect-Test-Tenant", options.TenantId);
            gateway.Options.SetRequestHeader("X-OpenConnect-Test-Principal", options.PrincipalId);
            var gatewayUri = new UriBuilder(baseUri)
            {
                Scheme = baseUri.Scheme == "https" ? "wss" : "ws",
                Path = $"/openconnect/v1/voice/realtime/{Uri.EscapeDataString(sessionId)}",
                Query = string.Empty
            }.Uri;

            try
            {
                await gateway.ConnectAsync(gatewayUri, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await CloseAsync(browser, WebSocketCloseStatus.EndpointUnavailable, "gateway_unavailable", cancellationToken).ConfigureAwait(false);
                return;
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.RequestAborted);
            var browserToGateway = RelayAsync(browser, gateway, linked.Token);
            var gatewayToBrowser = RelayAsync(gateway, browser, linked.Token);
            await Task.WhenAny(browserToGateway, gatewayToBrowser).ConfigureAwait(false);
            linked.Cancel();
            try { await Task.WhenAll(browserToGateway, gatewayToBrowser).ConfigureAwait(false); }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
            catch (WebSocketException) when (linked.IsCancellationRequested || context.RequestAborted.IsCancellationRequested) { }
        });
    }

    private static async Task RelayAsync(WebSocket source, WebSocket destination, CancellationToken token)
    {
        var buffer = new byte[96 * 1024];
        while (source.State == WebSocketState.Open && destination.State == WebSocketState.Open && !token.IsCancellationRequested)
        {
            var result = await source.ReceiveAsync(buffer, token).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await CloseAsync(destination, source.CloseStatus ?? WebSocketCloseStatus.NormalClosure, source.CloseStatusDescription ?? "closed", token).ConfigureAwait(false);
                return;
            }

            await destination.SendAsync(buffer.AsMemory(0, result.Count), result.MessageType, result.EndOfMessage, token).ConfigureAwait(false);
        }
    }

    private static bool IsSameOrigin(HttpContext context)
    {
        var origin = context.Request.Headers.Origin.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(origin)) return false;
        return Uri.TryCreate(origin, UriKind.Absolute, out var originUri)
            && string.Equals(originUri.Scheme, context.Request.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(originUri.Host, context.Request.Host.Host, StringComparison.OrdinalIgnoreCase)
            && originUri.Port == context.Request.Host.Port.GetValueOrDefault(originUri.IsDefaultPort ? -1 : originUri.Port);
    }

    private static Task CloseAsync(WebSocket socket, WebSocketCloseStatus status, string reason, CancellationToken token)
        => socket.State is WebSocketState.Open or WebSocketState.CloseReceived
            ? socket.CloseAsync(status, reason, token)
            : Task.CompletedTask;
}
