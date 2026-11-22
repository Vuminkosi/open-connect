using System.Net.Http.Headers;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Hosting;

namespace OpenConnect.Docs.Services.Voice;

/// <summary>Relays a single visitor-owned voice session to the application gateway.</summary>
internal sealed class DocumentationVoiceBridge(DocumentationVoiceTickets tickets, Uri gateway)
{
    private const string Prefix = "/openconnect/v1/voice/realtime/";
    private const string Protocol = "openconnect.voice.v1";
    private const string TicketPrefix = "oc-ticket.";
    private const int MaxFrame = 96 * 1024;
    private const long MaxAudio = 32 * 1024 * 1024;

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var path = context.Request.Path.Value;
        if (path is null || !path.StartsWith(Prefix, StringComparison.Ordinal))
        {
            await next(context);
            return;
        }
        var sessionId = path[Prefix.Length..];
        var protocols = context.WebSockets.WebSocketRequestedProtocols;
        var origin = context.Request.Headers.Origin.ToString();
        var expectedOrigin = $"{context.Request.Scheme}://{context.Request.Host}";
        var isLoopbackHost = string.Equals(context.Request.Host.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            (System.Net.IPAddress.TryParse(context.Request.Host.Host, out var hostIp) && System.Net.IPAddress.IsLoopback(hostIp));
        if ((!context.Request.IsHttps && !isLoopbackHost) || !context.WebSockets.IsWebSocketRequest ||
            !string.Equals(origin, expectedOrigin, StringComparison.OrdinalIgnoreCase) ||
            protocols.Count != 2 || protocols[0] != Protocol || !protocols[1].StartsWith(TicketPrefix, StringComparison.Ordinal) ||
            !tickets.TryConsume(protocols[1][TicketPrefix.Length..], sessionId, out var visitorToken))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        using var upstream = new ClientWebSocket();
        upstream.Options.AddSubProtocol(Protocol);
        upstream.Options.SetRequestHeader("Authorization", new AuthenticationHeaderValue("Bearer", visitorToken).ToString());
        var target = new UriBuilder(gateway)
        {
            Scheme = gateway.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
            Path = $"/openconnect/v1/voice/realtime/{sessionId}"
        }.Uri;
        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        connectTimeout.CancelAfter(TimeSpan.FromSeconds(10));
        try { await upstream.ConnectAsync(target, connectTimeout.Token); }
        catch (Exception) when (!context.RequestAborted.IsCancellationRequested)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }
        using var browser = await context.WebSockets.AcceptWebSocketAsync(Protocol);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        var toGateway = RelayAsync(browser, upstream, true, lifetime.Token);
        var toBrowser = RelayAsync(upstream, browser, false, lifetime.Token);
        await Task.WhenAny(toGateway, toBrowser);
        lifetime.Cancel();
        try { await Task.WhenAll(toGateway, toBrowser); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (WebSocketException) { }
    }

    private static async Task RelayAsync(WebSocket source, WebSocket destination, bool limitAudio, CancellationToken ct)
    {
        var buffer = new byte[MaxFrame];
        long audioBytes = 0;
        while (source.State == WebSocketState.Open && destination.State == WebSocketState.Open)
        {
            var result = await source.ReceiveAsync(buffer.AsMemory(), ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await destination.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, ct);
                return;
            }
            if (limitAudio && result.MessageType == WebSocketMessageType.Binary &&
                (audioBytes += result.Count) > MaxAudio) return;
            await destination.SendAsync(buffer.AsMemory(0, result.Count), result.MessageType, result.EndOfMessage, ct);
        }
    }
}

internal sealed class DocumentationVoiceStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.UseWebSockets();
        var bridge = app.ApplicationServices.GetRequiredService<DocumentationVoiceBridge>();
        app.Use((context, following) => bridge.InvokeAsync(context, _ => following()));
        next(app);
    };
}
