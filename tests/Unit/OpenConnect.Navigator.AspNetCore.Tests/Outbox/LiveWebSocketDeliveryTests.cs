using System.Net;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Navigator;
using OpenConnect.Navigator.AspNetCore.Outbox;
using Xunit;

namespace OpenConnect.Navigator.AspNetCore.Tests.Outbox;

public sealed class LiveWebSocketDeliveryTests
{
    private const string RawSecret = "raw-secret-value-123";

    [Fact]
    public async Task SendsByteIdenticalAuthorizedProjectionOverLocalWebSocket()
    {
        var delivery = new NavigatorWsDeliveryAdapter(new NavigatorWsProjector());
        var context = NavigatorServerAuthContext.FromServerPrincipal(AuthenticatedPrincipal(), "session-1");
        var committedEvent = Event();
        var projected = await delivery.ProjectAsync(committedEvent, context, "consumer-1");
        Assert.NotNull(projected);
        var expected = JsonSerializer.SerializeToUtf8Bytes(projected);

        await using var host = await StartServer(async socket =>
        {
            await socket.SendAsync(expected, WebSocketMessageType.Text, true, CancellationToken.None);
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "complete", CancellationToken.None);
        });
        using var client = await Connect(host.Address);
        var received = await ReceiveFrame(client);

        Assert.Equal(expected, received);
        var wireText = Encoding.UTF8.GetString(received);
        Assert.DoesNotContain(RawSecret, wireText, StringComparison.Ordinal);
        Assert.DoesNotContain("password", wireText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private", wireText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeniesSecretBearingEventBeforeAnySocketWrite()
    {
        var delivery = new NavigatorWsDeliveryAdapter(new NavigatorWsProjector());
        var context = NavigatorServerAuthContext.FromServerPrincipal(AuthenticatedPrincipal(), "session-1");
        var receivedBytes = 0;
        await using var host = await StartServer(async socket =>
        {
            try
            {
                var safeProjection = await delivery.ProjectAsync(
                    Event() with { Metadata = new Dictionary<string, string> { ["status"] = RawSecret } },
                    context,
                    "consumer-adversarial");
                if (safeProjection is not null)
                    await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(safeProjection), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch (ArgumentException)
            {
                // The projector denies unsafe content; deliberately do not write a frame.
            }
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "denied", CancellationToken.None);
        });
        using var client = await Connect(host.Address);
        var buffer = new byte[1024];
        var result = await client.ReceiveAsync(buffer, CancellationToken.None);
        if (result.MessageType != WebSocketMessageType.Close)
            receivedBytes += result.Count;

        Assert.Equal(WebSocketMessageType.Close, result.MessageType);
        Assert.Equal(0, receivedBytes);
        await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "acknowledged", CancellationToken.None);
        Assert.Equal(WebSocketState.Closed, client.State);
    }

    private static ClaimsPrincipal AuthenticatedPrincipal() => new(new ClaimsIdentity(
    [
        new Claim("tenant_id", "tenant-1"),
        new Claim(ClaimTypes.NameIdentifier, "principal-1"),
        new Claim("sub", "principal-1")
    ], "test"));

    private static NavigatorEvent Event() => new(
        "event-1", "command.completed", "session-1", "task-1", 1, "correlation-1",
        DateTimeOffset.Parse("2026-11-08T12:00:00Z"),
        new Dictionary<string, string> { ["status"] = "complete", ["note"] = "safe metadata" });

    private static async Task<RunningWebSocketHost> StartServer(Func<WebSocket, Task> send)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http1));
        var app = builder.Build();
        app.UseWebSockets();
        app.Map("/ws", async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await send(socket);
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!.Addresses.Single();
        return new RunningWebSocketHost(app, new Uri(address.Replace("http://", "ws://", StringComparison.Ordinal) + "/ws"));
    }

    private static async Task<ClientWebSocket> Connect(Uri address)
    {
        var client = new ClientWebSocket();
        await client.ConnectAsync(address, CancellationToken.None);
        return client;
    }

    private static async Task<byte[]> ReceiveFrame(ClientWebSocket client)
    {
        using var frame = new MemoryStream();
        var buffer = new byte[1024];
        WebSocketReceiveResult result;
        do
        {
            result = await client.ReceiveAsync(buffer, CancellationToken.None);
            Assert.Equal(WebSocketMessageType.Text, result.MessageType);
            frame.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return frame.ToArray();
    }

    private sealed class RunningWebSocketHost(WebApplication app, Uri address) : IAsyncDisposable
    {
        public Uri Address { get; } = address;
        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
