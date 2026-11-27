using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Components.Blazor.Voice;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class DocumentationVoiceBridgeTests
{
    private const string Session = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
    private const string Other = "01ARZ3NDEKTSV4RRFFQ69G5FAW";
    private static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task RelaysOnlyOnceWithSessionBoundVisitorCredential()
    {
        await using var fixture = await Fixture.StartAsync();
        using var exchange = new CancellationTokenSource(ExchangeTimeout);
        var ticket = fixture.Tickets.Issue(Session, "visitor-credential");
        using (var socket = fixture.Socket(ticket))
        {
            await socket.ConnectAsync(fixture.VoiceUri(Session), exchange.Token);
            await socket.SendAsync(Encoding.UTF8.GetBytes("hello"), WebSocketMessageType.Text, true, exchange.Token);
            var buffer = new byte[32];
            var result = await socket.ReceiveAsync(buffer, exchange.Token);
            Assert.Equal("hello", Encoding.UTF8.GetString(buffer, 0, result.Count));
            await fixture.CompleteEchoAsync(exchange.Token);
        }
        Assert.Equal("Bearer visitor-credential", await fixture.Authorization.Task.WaitAsync(TimeSpan.FromSeconds(5), exchange.Token));
        using var replay = fixture.Socket(ticket);
        await Assert.ThrowsAsync<WebSocketException>(() => replay.ConnectAsync(fixture.VoiceUri(Session), exchange.Token));
    }

    [Fact]
    public async Task RejectsWrongOriginAndWrongSession()
    {
        await using var fixture = await Fixture.StartAsync();
        var first = fixture.Tickets.Issue(Session, "visitor-credential");
        using (var socket = fixture.Socket(first, "https://attacker.invalid"))
            await Assert.ThrowsAsync<WebSocketException>(() => socket.ConnectAsync(fixture.VoiceUri(Session), CancellationToken.None));
        using (var socket = fixture.Socket(first))
            await Assert.ThrowsAsync<WebSocketException>(() => socket.ConnectAsync(fixture.VoiceUri(Other), CancellationToken.None));
        using var replay = fixture.Socket(first);
        await Assert.ThrowsAsync<WebSocketException>(() => replay.ConnectAsync(fixture.VoiceUri(Session), CancellationToken.None));
        Assert.False(fixture.Authorization.Task.IsCompleted);
    }

    private sealed class Fixture(WebApplication gateway, WebApplication site, X509Certificate2 certificate,
        string siteAddress, OpenConnectVoiceTicketStore tickets, TaskCompletionSource<string> authorization,
        TaskCompletionSource echoObserved, TaskCompletionSource gatewayCloseSent) : IAsyncDisposable
    {
        public OpenConnectVoiceTicketStore Tickets { get; } = tickets;
        public TaskCompletionSource<string> Authorization { get; } = authorization;
        public Uri VoiceUri(string session) => new($"{siteAddress.Replace("https://", "wss://", StringComparison.Ordinal)}/openconnect/v1/voice/realtime/{session}");

        public ClientWebSocket Socket(string ticket, string? origin = null)
        {
            var socket = new ClientWebSocket();
            socket.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
            socket.Options.SetRequestHeader("Origin", origin ?? siteAddress);
            socket.Options.AddSubProtocol("openconnect.voice.v1");
            socket.Options.AddSubProtocol($"oc-ticket.{ticket}");
            return socket;
        }

        public async Task CompleteEchoAsync(CancellationToken cancellationToken)
        {
            echoObserved.TrySetResult();
            await gatewayCloseSent.Task.WaitAsync(cancellationToken);
        }

        public static async Task<Fixture> StartAsync()
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
            var certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null);
            var gatewayBuilder = WebApplication.CreateSlimBuilder();
            gatewayBuilder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            var gateway = gatewayBuilder.Build();
            gateway.UseWebSockets();
            var authorization = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var echoObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var gatewayCloseSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            gateway.Run(async context =>
            {
                authorization.TrySetResult(context.Request.Headers.Authorization.ToString());
                using var socket = await context.WebSockets.AcceptWebSocketAsync("openconnect.voice.v1");
                using var exchange = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
                exchange.CancelAfter(ExchangeTimeout);
                var buffer = new byte[32];
                var result = await socket.ReceiveAsync(buffer, exchange.Token);
                await socket.SendAsync(buffer.AsMemory(0, result.Count), result.MessageType, result.EndOfMessage, exchange.Token);

                // Keep the fixture open until the browser has asserted the echo; then
                // initiate normal close before the browser disposes its endpoint.
                await echoObserved.Task.WaitAsync(exchange.Token);
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, exchange.Token);
                gatewayCloseSent.TrySetResult();
            });
            var siteBuilder = WebApplication.CreateSlimBuilder();
            siteBuilder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
            await gateway.StartAsync();
            var gatewayAddress = gateway.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            siteBuilder.Services.AddOpenConnectVoiceBridge(options =>
            {
                options.GatewayBaseAddress = new Uri(gatewayAddress);
                options.AllowLoopbackHttp = true;
            });
            var site = siteBuilder.Build();
            site.Run(_ => Task.CompletedTask);
            await site.StartAsync();
            var tickets = site.Services.GetRequiredService<OpenConnectVoiceTicketStore>();
            var siteAddress = site.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            return new Fixture(gateway, site, certificate, siteAddress, tickets, authorization, echoObserved, gatewayCloseSent);
        }

        public async ValueTask DisposeAsync()
        {
            await site.DisposeAsync();
            await gateway.DisposeAsync();
            certificate.Dispose();
        }
    }
}
