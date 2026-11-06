using System.Net;
using OpenConnect.Abstractions;
using OpenConnect.Client;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
namespace OpenConnect.Client.Tests;
public sealed class OpenConnectClientTests
{
    [Fact]
    public async Task CreateTask_sends_idempotency_and_reads_typed_response()
    {
        var handler = new StubHandler((request, _) =>
        {
            Assert.Equal("key-1", request.Headers.GetValues("Idempotency-Key").Single());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted) { Content = new StringContent("{\"id\":{\"value\":\"00000000000000000000000000\"},\"tenantId\":{\"value\":{\"value\":\"00000000000000000000000000\"}},\"principalId\":{\"value\":{\"value\":\"00000000000000000000000000\"}},\"state\":\"queued\"}", System.Text.Encoding.UTF8, "application/json") });
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://localhost/openconnect/v1/") };
        await Assert.ThrowsAnyAsync<Exception>(() => new OpenConnectClient(http).CreateTaskAsync(new("task", "key-1")));
        Assert.Equal("tasks", handler.Path);
    }
    [Fact] public void Rejects_non_https_base_address() => Assert.Throws<ArgumentException>(() => new ServiceCollection().AddOpenConnectClient(new Uri("http://localhost/")));
    [Fact]
    public async Task GetCapabilities_reads_typed_list()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[{\"id\":{\"value\":\"privacy\"},\"kind\":\"security\",\"state\":\"ready\"}]", System.Text.Encoding.UTF8, "application/json")
        }));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://localhost/openconnect/v1/") };

        var capabilities = await new OpenConnectClient(http).GetCapabilitiesAsync();

        Assert.Equal("capabilities", handler.Path);
        var capability = Assert.Single(capabilities);
        Assert.Equal(new CapabilityId("privacy"), capability.Id);
        Assert.Equal(CapabilityKind.Security, capability.Kind);
        Assert.Equal(DetectorState.Ready, capability.State);
    }
    [Fact] public async Task Cancellation_is_forwarded()
    {
        var handler = new StubHandler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return new HttpResponseMessage(); });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") };
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new OpenConnectClient(http).GetTaskAsync(new TaskId(Ulid.NewUlid()), cts.Token));
    }
    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        public string? Path { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { Path = request.RequestUri!.AbsolutePath.Trim('/').Split('/').Last(); return callback(request, cancellationToken); }
    }
}
