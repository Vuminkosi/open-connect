using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenConnect.LiveUI.Inference;
using Xunit;

namespace OpenConnect.LiveUI.Blazor.Tests;

public sealed class LiveUIUnavailableTransportTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConnectionLossAfterReadinessUsesConfiguredFallback(bool viaGateway)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        var readiness = viaGateway
            ? JsonSerializer.Serialize(new LiveUIInferenceAvailability(
                LiveUIInferenceProviderKind.LocalWorker, LiveUIInferenceAvailabilityStatus.Available),
                new JsonSerializerOptions(JsonSerializerDefaults.Web))
            : "{}";
        var server = RespondOnceAndStopAsync(listener, readiness, timeout.Token);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{endpoint.Port}/") };
        ILiveUIInferenceProvider worker = viaGateway
            ? new GatewayWorkerLiveUIInferenceProvider(client)
            : new LocalWorkerLiveUIInferenceProvider(client, Options.Create(new LiveUIWorkerClientOptions
            {
                Endpoint = client.BaseAddress, BearerToken = "acceptance-token"
            }));
        var fallback = new RecordingFallback();
        var options = new LiveUIInferenceOptions { Mode = LiveUIInferenceMode.BrowserFirst };
        options.Worker.Enabled = true;
        options.Gateway.Enabled = true;
        options.Gateway.FallbackPolicy = LiveUIFallbackPolicy.OnUnavailable;
        var router = new LiveUIInferenceRouter([worker, fallback], new LiveUIActionPlanValidator(), Options.Create(options));
        var request = new LiveUIInferenceRequest("Show completed tasks", "session", "todo", "openconnect.liveui/v1",
            [new("todo.completed", ["activate"])]);

        try
        {
            var result = await router.GeneratePlanAsync(request, timeout.Token);
            await server;
            Assert.Equal("completed", result.Outcome);
            Assert.Equal(LiveUIInferenceProviderKind.Gateway, result.Provider);
            Assert.Equal(1, fallback.Calls);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task RespondOnceAndStopAsync(TcpListener listener, string body, CancellationToken cancellationToken)
    {
        using var connection = await listener.AcceptTcpClientAsync(cancellationToken);
        await using var stream = connection.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        while (await reader.ReadLineAsync(cancellationToken) is { Length: > 0 }) { }
        listener.Stop();
        var payload = Encoding.UTF8.GetBytes(body);
        var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(headers, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private sealed class RecordingFallback : ILiveUIInferenceProvider
    {
        public int Calls { get; private set; }
        public LiveUIInferenceProviderKind Kind => LiveUIInferenceProviderKind.Gateway;
        public Task<LiveUIInferenceAvailability> GetAvailabilityAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new LiveUIInferenceAvailability(Kind, LiveUIInferenceAvailabilityStatus.Available));
        public Task<LiveUIInferenceResult> GeneratePlanAsync(LiveUIInferenceRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new LiveUIInferenceResult(Kind,
                new LiveUIActionPlan(request.SchemaVersion, [new("activate", "todo.completed")]), "completed"));
        }
    }
}
