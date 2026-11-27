using System.Net;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using OpenConnect.Abstractions;
using OpenConnect.Components.Blazor.Voice;
using Xunit;

namespace OpenConnect.Blazor.Tests;

public sealed class OpenConnectVoiceBridgeTests
{
    private sealed class MutableTimeProvider(DateTimeOffset initialUtc) : TimeProvider
    {
        private DateTimeOffset _utcNow = initialUtc;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);
    }

    [Fact]
    public void Options_Validate_EnforcesRollingBudgetBounds()
    {
        var valid = new OpenConnectVoiceBridgeOptions
        {
            GatewayBaseAddress = new Uri("http://127.0.0.1:5000"),
            MaxFrameBytes = 96 * 1024,
            MaxRollingSessionAudioBytes = 64L * 1024 * 1024,
            RollingSessionWindowSeconds = 300
        };
        valid.Validate();

        var tooSmallBudget = new OpenConnectVoiceBridgeOptions
        {
            GatewayBaseAddress = new Uri("http://127.0.0.1:5000"),
            MaxRollingSessionAudioBytes = 512
        };
        Assert.Throws<InvalidOperationException>(() => tooSmallBudget.Validate());

        var tooLargeWindow = new OpenConnectVoiceBridgeOptions
        {
            GatewayBaseAddress = new Uri("http://127.0.0.1:5000"),
            RollingSessionWindowSeconds = 7200
        };
        Assert.Throws<InvalidOperationException>(() => tooLargeWindow.Validate());
    }

    [Fact]
    public void RollingAudioBudget_EvictsExpiredEntriesAfterWindow()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 11, 24, 10, 0, 0, TimeSpan.Zero));
        var budget = new OpenConnectVoiceBridge.RollingAudioBudget(
            maxBytes: 4096,
            window: TimeSpan.FromSeconds(10),
            timeProvider: clock);

        Assert.True(budget.TryConsume(2048));
        Assert.True(budget.TryConsume(2048));
        Assert.Equal(4096, budget.CurrentBytes);
        Assert.False(budget.TryConsume(1));

        // Advance past the 10-second rolling window; budget refreshes cleanly without killing persistent session.
        clock.Advance(TimeSpan.FromSeconds(11));
        Assert.True(budget.TryConsume(3072));
        Assert.Equal(3072, budget.CurrentBytes);
    }

    [Fact]
    public async Task InvokeAsync_RejectsUnsupportedSubProtocolWith403()
    {
        var sessionId = Ulid.NewUlid().ToString();
        var tickets = new OpenConnectVoiceTicketStore();
        var ticket = tickets.Issue(sessionId, "secret-token");
        var bridge = new OpenConnectVoiceBridge(tickets, new OpenConnectVoiceBridgeOptions
        {
            GatewayBaseAddress = new Uri("http://127.0.0.1:5000")
        });

        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("127.0.0.1", 5001);
        context.Request.Path = $"{OpenConnectVoiceBridge.RoutePrefix}{sessionId}";
        context.Request.Headers.Origin = "http://127.0.0.1:5001";
        context.Features.Set<IHttpWebSocketFeature>(new StubWebSocketFeature(["openconnect.voice.v99", $"oc-ticket.{ticket}"]));

        await bridge.InvokeAsync(context, _ => Task.CompletedTask);

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Theory]
    [InlineData(OpenConnectVoiceBridge.VoiceSubProtocol)]
    [InlineData(OpenConnectVoiceBridge.VoiceV2SubProtocol)]
    public async Task InvokeAsync_ForwardsNegotiatedSubProtocolAndRelaysFrames(string requestedSubProtocol)
    {
        string? upstreamObservedSubProtocol = null;
        string? upstreamAuthHeader = null;

        // 1. Start minimal loopback upstream gateway
        var upstreamBuilder = WebApplication.CreateSlimBuilder();
        upstreamBuilder.Logging.ClearProviders();
        upstreamBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var upstreamApp = upstreamBuilder.Build();
        upstreamApp.UseWebSockets();
        upstreamApp.Map($"{OpenConnectVoiceBridge.RoutePrefix}{{sessionId}}", async context =>
        {
            upstreamAuthHeader = context.Request.Headers.Authorization.ToString();
            upstreamObservedSubProtocol = context.WebSockets.WebSocketRequestedProtocols.FirstOrDefault();
            using var ws = await context.WebSockets.AcceptWebSocketAsync(upstreamObservedSubProtocol);
            var buf = new byte[4096];
            while (ws.State == WebSocketState.Open)
            {
                var received = await ws.ReceiveAsync(buf, context.RequestAborted);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", context.RequestAborted);
                    break;
                }

                await ws.SendAsync(buf.AsMemory(0, received.Count), received.MessageType, received.EndOfMessage, context.RequestAborted);
            }
        });
        await upstreamApp.StartAsync();
        var upstreamAddress = new Uri(upstreamApp.Urls.Single());

        // 2. Start minimal loopback Blazor host with OpenConnectVoiceBridge
        var tickets = new OpenConnectVoiceTicketStore();
        var bridgeBuilder = WebApplication.CreateSlimBuilder();
        bridgeBuilder.Logging.ClearProviders();
        bridgeBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var bridgeApp = bridgeBuilder.Build();
        bridgeApp.UseWebSockets();
        var bridge = new OpenConnectVoiceBridge(tickets, new OpenConnectVoiceBridgeOptions
        {
            GatewayBaseAddress = upstreamAddress,
            MaxFrameBytes = 96 * 1024,
            MaxRollingSessionAudioBytes = 4 * 1024 * 1024,
            RollingSessionWindowSeconds = 60
        });
        bridgeApp.Use((ctx, next) => bridge.InvokeAsync(ctx, next));
        await bridgeApp.StartAsync();
        var bridgeAddress = new Uri(bridgeApp.Urls.Single());

        // 3. Connect as browser with ticket + requested subprotocol
        var sessionId = Ulid.NewUlid().ToString();
        var ticket = tickets.Issue(sessionId, "gateway-bearer-token");
        using var client = new ClientWebSocket();
        client.Options.AddSubProtocol(requestedSubProtocol);
        client.Options.AddSubProtocol($"{OpenConnectVoiceBridge.TicketSubProtocolPrefix}{ticket}");
        client.Options.SetRequestHeader("Origin", $"{bridgeAddress.Scheme}://{bridgeAddress.Authority}");

        var wsUri = new Uri($"ws://{bridgeAddress.Authority}{OpenConnectVoiceBridge.RoutePrefix}{sessionId}");
        await client.ConnectAsync(wsUri, CancellationToken.None);

        Assert.Equal(requestedSubProtocol, client.SubProtocol);
        Assert.Equal(requestedSubProtocol, upstreamObservedSubProtocol);
        Assert.Equal("Bearer gateway-bearer-token", upstreamAuthHeader);

        var payload = new byte[] { 0x4F, 0x43, 0x02, 0x01, 1, 2, 3, 4 };
        await client.SendAsync(payload, WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None);
        var reply = new byte[64];
        var result = await client.ReceiveAsync(reply, CancellationToken.None);
        Assert.Equal(WebSocketMessageType.Binary, result.MessageType);
        Assert.Equal(payload, reply[..result.Count]);

        using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        if (client.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", closeCts.Token);
        }

        await bridgeApp.StopAsync(closeCts.Token);
        await upstreamApp.StopAsync(closeCts.Token);
    }

    private sealed class StubWebSocketFeature(IList<string> requestedProtocols) : IHttpWebSocketFeature
    {
        public bool IsWebSocketRequest => true;
        public Task<WebSocket> AcceptAsync(WebSocketAcceptContext context) =>
            throw new NotSupportedException("Should not accept rejected requests.");
        public IList<string> RequestedProtocols => requestedProtocols;
    }
}
