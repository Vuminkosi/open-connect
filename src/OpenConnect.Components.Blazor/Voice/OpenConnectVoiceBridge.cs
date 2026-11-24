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
    public const string VoiceV2SubProtocol = "openconnect.voice.v2";
    public const string TicketSubProtocolPrefix = "oc-ticket.";

    private readonly OpenConnectVoiceTicketStore _tickets;
    private readonly OpenConnectVoiceBridgeOptions _options;
    private readonly TimeProvider _timeProvider;

    public OpenConnectVoiceBridge(OpenConnectVoiceTicketStore tickets, IOptions<OpenConnectVoiceBridgeOptions> options)
        : this(tickets, options?.Value ?? throw new ArgumentNullException(nameof(options)), TimeProvider.System)
    {
    }

    public OpenConnectVoiceBridge(OpenConnectVoiceTicketStore tickets, OpenConnectVoiceBridgeOptions options)
        : this(tickets, options, TimeProvider.System)
    {
    }

    internal OpenConnectVoiceBridge(OpenConnectVoiceTicketStore tickets, OpenConnectVoiceBridgeOptions options, TimeProvider? timeProvider)
    {
        ArgumentNullException.ThrowIfNull(tickets);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _tickets = tickets;
        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;
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
        var requestedProtocol = protocols.Count == 2 ? protocols[0] : null;
        var isSupportedProtocol = string.Equals(requestedProtocol, VoiceSubProtocol, StringComparison.Ordinal) ||
            string.Equals(requestedProtocol, VoiceV2SubProtocol, StringComparison.Ordinal);

        if ((!context.Request.IsHttps && !isLoopbackHost) ||
            !context.WebSockets.IsWebSocketRequest ||
            !string.Equals(origin, expectedOrigin, StringComparison.OrdinalIgnoreCase) ||
            !isSupportedProtocol ||
            !protocols[1].StartsWith(TicketSubProtocolPrefix, StringComparison.Ordinal) ||
            !_tickets.TryConsume(protocols[1][TicketSubProtocolPrefix.Length..], sessionId, out var bearerToken))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var negotiatedProtocol = requestedProtocol!;
        var isDuplexV2 = string.Equals(negotiatedProtocol, VoiceV2SubProtocol, StringComparison.Ordinal);

        using var upstream = new ClientWebSocket();
        upstream.Options.AddSubProtocol(negotiatedProtocol);
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

        var acceptedProtocol = string.IsNullOrEmpty(upstream.SubProtocol) ? negotiatedProtocol : upstream.SubProtocol;
        using var browser = await context.WebSockets.AcceptWebSocketAsync(acceptedProtocol).ConfigureAwait(false);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        var toGateway = RelayAsync(browser, upstream, limitAudio: true, isDuplexV2, lifetime.Token);
        var toBrowser = RelayAsync(upstream, browser, limitAudio: false, isDuplexV2, lifetime.Token);
        await Task.WhenAny(toGateway, toBrowser).ConfigureAwait(false);
        lifetime.Cancel();
        try
        {
            await Task.WhenAll(toGateway, toBrowser).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (WebSocketException) { }
    }

    private async Task RelayAsync(
        WebSocket source,
        WebSocket destination,
        bool limitAudio,
        bool isDuplexV2,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[_options.MaxFrameBytes];
        long audioBytes = 0;
        long currentMessageBytes = 0;
        var rollingBudget = isDuplexV2 && limitAudio
            ? new RollingAudioBudget(_options.MaxRollingSessionAudioBytes, TimeSpan.FromSeconds(_options.RollingSessionWindowSeconds), _timeProvider)
            : null;

        while (source.State == WebSocketState.Open && destination.State == WebSocketState.Open)
        {
            var result = await source.ReceiveAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await destination.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (isDuplexV2)
            {
                currentMessageBytes += result.Count;
                if (currentMessageBytes > _options.MaxFrameBytes || (!result.EndOfMessage && currentMessageBytes >= _options.MaxFrameBytes))
                {
                    if (source.State == WebSocketState.Open)
                        await source.CloseOutputAsync(WebSocketCloseStatus.MessageTooBig, "Voice frame exceeds limit.", cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (result.EndOfMessage)
                    currentMessageBytes = 0;
            }

            if (limitAudio && result.MessageType == WebSocketMessageType.Binary)
            {
                if (rollingBudget is not null)
                {
                    if (!rollingBudget.TryConsume(result.Count))
                    {
                        if (source.State == WebSocketState.Open)
                            await source.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "Rolling voice audio budget exceeded.", cancellationToken).ConfigureAwait(false);
                        return;
                    }
                }
                else if ((audioBytes += result.Count) > _options.MaxAudioBytes)
                {
                    return;
                }
            }

            await destination.SendAsync(buffer.AsMemory(0, result.Count), result.MessageType, result.EndOfMessage, cancellationToken).ConfigureAwait(false);
        }
    }

    internal sealed class RollingAudioBudget
    {
        private readonly long _maxBytes;
        private readonly TimeSpan _window;
        private readonly TimeProvider _timeProvider;
        private readonly Queue<(DateTimeOffset Timestamp, int Bytes)> _entries = new();
        private long _totalBytes;

        public RollingAudioBudget(long maxBytes, TimeSpan window, TimeProvider? timeProvider = null)
        {
            _maxBytes = maxBytes;
            _window = window;
            _timeProvider = timeProvider ?? TimeProvider.System;
        }

        public long CurrentBytes => _totalBytes;

        public bool TryConsume(int bytes)
        {
            if (bytes < 0)
                return false;

            var now = _timeProvider.GetUtcNow();
            var cutoff = now - _window;
            while (_entries.Count > 0 && _entries.Peek().Timestamp <= cutoff)
            {
                var expired = _entries.Dequeue();
                _totalBytes -= expired.Bytes;
            }

            if (_totalBytes + bytes > _maxBytes)
                return false;

            if (bytes > 0)
            {
                _entries.Enqueue((now, bytes));
                _totalBytes += bytes;
            }

            return true;
        }
    }

    private static bool IsLoopbackHost(string? host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));
}
