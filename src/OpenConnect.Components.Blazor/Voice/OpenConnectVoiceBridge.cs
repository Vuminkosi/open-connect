using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace OpenConnect.Components.Blazor.Voice;

/// <summary>
/// Relays a single-use, ticket-authenticated browser voice WebSocket from a Blazor Server host
/// to the configured OpenConnect application gateway without exposing bearer tokens to the browser.
/// </summary>
public sealed class OpenConnectVoiceBridge
{
    public const string RoutePrefix = "/openconnect/v1/voice/realtime/";
    public const string VoiceSubProtocol = "openconnect.voice.v1";
    public const string TicketSubProtocolPrefix = "oc-ticket.";

    private readonly OpenConnectVoiceTicketStore _tickets;
    private readonly OpenConnectVoiceBridgeOptions _options;

    public OpenConnectVoiceBridge(OpenConnectVoiceTicketStore tickets, IOptions<OpenConnectVoiceBridgeOptions> options)
        : this(tickets, options?.Value ?? throw new ArgumentNullException(nameof(options)))
    {
    }

    public OpenConnectVoiceBridge(OpenConnectVoiceTicketStore tickets, OpenConnectVoiceBridgeOptions options)
    {
        ArgumentNullException.ThrowIfNull(tickets);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _tickets = tickets;
        _options = options;
    }

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var path = context.Request.Path.Value;
        if (path is null || !path.StartsWith(RoutePrefix, StringComparison.Ordinal))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var sessionId = path[RoutePrefix.Length..];
        var protocols = context.WebSockets.WebSocketRequestedProtocols;
        var origin = context.Request.Headers.Origin.ToString();
        var expectedOrigin = $"{context.Request.Scheme}://{context.Request.Host}";
        var isLoopbackHost = _options.AllowLoopbackHttp && IsLoopbackHost(context.Request.Host.Host);

        if ((!context.Request.IsHttps && !isLoopbackHost) ||
            !context.WebSockets.IsWebSocketRequest ||
            !string.Equals(origin, expectedOrigin, StringComparison.OrdinalIgnoreCase) ||
            protocols.Count != 2 ||
            !string.Equals(protocols[0], VoiceSubProtocol, StringComparison.Ordinal) ||
            !protocols[1].StartsWith(TicketSubProtocolPrefix, StringComparison.Ordinal) ||
            !_tickets.TryConsume(protocols[1][TicketSubProtocolPrefix.Length..], sessionId, out var bearerToken))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        using var upstream = new ClientWebSocket();
        upstream.Options.AddSubProtocol(VoiceSubProtocol);
        upstream.Options.SetRequestHeader("Authorization", new AuthenticationHeaderValue("Bearer", bearerToken).ToString());

        var gateway = _options.GatewayBaseAddress!;
        var target = new UriBuilder(gateway)
        {
            Scheme = gateway.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
            Path = $"{RoutePrefix}{sessionId}"
        }.Uri;

        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        connectTimeout.CancelAfter(_options.ConnectTimeout);
        try
        {
            await upstream.ConnectAsync(target, connectTimeout.Token).ConfigureAwait(false);
        }
        catch (Exception) when (!context.RequestAborted.IsCancellationRequested)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        using var browser = await context.WebSockets.AcceptWebSocketAsync(VoiceSubProtocol).ConfigureAwait(false);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        var toGateway = RelayAsync(browser, upstream, limitAudio: true, lifetime.Token);
        var toBrowser = RelayAsync(upstream, browser, limitAudio: false, lifetime.Token);
        await Task.WhenAny(toGateway, toBrowser).ConfigureAwait(false);
        lifetime.Cancel();
        try
        {
            await Task.WhenAll(toGateway, toBrowser).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (WebSocketException) { }
    }

    private async Task RelayAsync(WebSocket source, WebSocket destination, bool limitAudio, CancellationToken cancellationToken)
    {
        var buffer = new byte[_options.MaxFrameBytes];
        long audioBytes = 0;
        while (source.State == WebSocketState.Open && destination.State == WebSocketState.Open)
        {
            var result = await source.ReceiveAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await destination.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (limitAudio && result.MessageType == WebSocketMessageType.Binary &&
                (audioBytes += result.Count) > _options.MaxAudioBytes)
            {
                return;
            }

            await destination.SendAsync(buffer.AsMemory(0, result.Count), result.MessageType, result.EndOfMessage, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsLoopbackHost(string? host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));
}
