using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenConnect.Abstractions;
using OpenConnect.Core.Capabilities;
using OpenConnect.Hosting.AspNetCore;
using Xunit;

namespace OpenConnect.Hosting.Tests;

public sealed class WorkerCapabilityMonitorTests
{
    private const string Ready = """{"capabilities":[{"schemaVersion":"openconnect.v1","id":"knowledge.embed","kind":"embedding","state":"ready","version":"1.0.0","localOnly":true}]}""";

    [Fact]
    public async Task PublishesOnlyReadyAndWithdrawsOnFailureThenRecovers()
    {
        var registry = new CapabilityRegistry();
        var response = Ready;
        var handler = new Handler(request =>
        {
            Assert.Equal("/internal/v1/capabilities", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("worker-token", request.Headers.Authorization.Parameter);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        });
        var source = new WorkerCapabilitySource("https://worker.test", "worker-token", ["knowledge.embed", "knowledge.index"]);
        using var monitor = new WorkerCapabilityMonitor(registry, [source], null,
            _ => new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("https://worker.test") });
        await monitor.RefreshAsync();
        Assert.Equal(DetectorState.Ready, registry.Get(new CapabilityId("knowledge.embed"))!.State);
        Assert.Null(registry.Get(new CapabilityId("knowledge.index")));
        response = "{invalid";
        await monitor.RefreshAsync();
        Assert.Null(registry.Get(new CapabilityId("knowledge.embed")));
        response = Ready;
        await monitor.RefreshAsync();
        Assert.NotNull(registry.Get(new CapabilityId("knowledge.embed")));
        monitor.Dispose();
        Assert.Null(registry.Get(new CapabilityId("knowledge.embed")));
    }

    [Fact]
    public async Task ReadyEndpointReflectsSuccessfulPollAndWithdrawal()
    {
        var registry = new CapabilityRegistry();
        var healthy = true;
        var source = new WorkerCapabilitySource("https://worker.test", "token", ["knowledge.embed"]);
        using var monitor = new WorkerCapabilityMonitor(registry, [source], null, _ =>
            new HttpClient(new Handler(_ => healthy
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Ready) }
                : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))) { BaseAddress = new Uri("https://worker.test") });
        var services = new ServiceCollection(); services.AddLogging(); services.AddSingleton<ICapabilityRegistry>(registry);
        using var provider = services.BuildServiceProvider();
        async Task<JsonDocument> ReadEndpoint()
        {
            var context = new DefaultHttpContext { RequestServices = provider, Response = { Body = new MemoryStream() } };
            await EndpointRouteBuilderExtensions.HandleReadyCapabilities(provider).ExecuteAsync(context);
            Assert.Equal(200, context.Response.StatusCode);
            return JsonDocument.Parse(((MemoryStream)context.Response.Body).ToArray());
        }
        await monitor.RefreshAsync();
        using (var ready = await ReadEndpoint())
        {
            var descriptor = Assert.Single(ready.RootElement.EnumerateArray());
            Assert.Equal("knowledge.embed", descriptor.GetProperty("id").GetProperty("value").GetString());
            Assert.Equal("ready", descriptor.GetProperty("state").GetString());
        }
        healthy = false;
        await monitor.RefreshAsync();
        using var unavailable = await ReadEndpoint();
        Assert.Empty(unavailable.RootElement.EnumerateArray());
    }

    [Fact]
    public async Task OneUnavailableSourceDoesNotWithdrawAnother()
    {
        var registry = new CapabilityRegistry();
        var source1 = new WorkerCapabilitySource("https://one.test", "one-token", ["knowledge.embed"]);
        var source2 = new WorkerCapabilitySource("https://two.test", "two-token", ["analytics.analyze"]);
        using var monitor = new WorkerCapabilityMonitor(registry, [source1, source2], null, source =>
        {
            var handler = new Handler(_ => source == source1
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Ready) }
                : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            return new HttpClient(handler) { BaseAddress = new Uri("https://worker.test") };
        });
        await monitor.RefreshAsync();
        Assert.NotNull(registry.Get(new CapabilityId("knowledge.embed")));
        Assert.Null(registry.Get(new CapabilityId("analytics.analyze")));
    }

    [Fact]
    public async Task OversizedResponseWithdrawsPreviouslyReadyCapability()
    {
        var registry = new CapabilityRegistry();
        var oversized = false;
        var source = new WorkerCapabilitySource("https://worker.test", "token", ["knowledge.embed"]);
        using var monitor = new WorkerCapabilityMonitor(registry, [source], null, _ =>
            new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(oversized ? new string('x', 65537) : Ready)
            })) { BaseAddress = new Uri("https://worker.test") });
        await monitor.RefreshAsync();
        Assert.NotNull(registry.Get(new CapabilityId("knowledge.embed")));
        oversized = true;
        await monitor.RefreshAsync();
        Assert.Null(registry.Get(new CapabilityId("knowledge.embed")));
    }

    [Fact]
    public async Task CancellationDuringRefreshWithdrawsAllOwnedIds()
    {
        const string analytics = """{"capabilities":[{"schemaVersion":"openconnect.v1","id":"analytics.analyze","kind":"tool","state":"ready","version":"1.0.0","localOnly":true}]}""";
        var registry = new CapabilityRegistry();
        var first = new WorkerCapabilitySource("https://one.test", "token", ["knowledge.embed"]);
        var second = new WorkerCapabilitySource("https://two.test", "token", ["analytics.analyze"]);
        using var cancellation = new CancellationTokenSource();
        var cancelOnPoll = false;
        using var monitor = new WorkerCapabilityMonitor(registry, [first, second], null, source =>
        {
            var handler = new Handler(_ =>
            {
                if (source == first && cancelOnPoll) cancellation.Cancel();
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(source == first ? Ready : analytics) };
            });
            return new HttpClient(handler) { BaseAddress = new Uri("https://worker.test") };
        });
        await monitor.RefreshAsync();
        Assert.NotNull(registry.Get(new CapabilityId("knowledge.embed")));
        Assert.NotNull(registry.Get(new CapabilityId("analytics.analyze")));
        cancelOnPoll = true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => monitor.RefreshAsync(cancellation.Token));
        Assert.Null(registry.Get(new CapabilityId("knowledge.embed")));
        Assert.Null(registry.Get(new CapabilityId("analytics.analyze")));
    }

    [Fact]
    public async Task DisposedMonitorCannotPublishALateResponse()
    {
        var registry = new CapabilityRegistry();
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new AsyncHandler(async () =>
        {
            sent.SetResult();
            await release.Task;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Ready) };
        });
        var source = new WorkerCapabilitySource("https://worker.test", "token", ["knowledge.embed"]);
        using var monitor = new WorkerCapabilityMonitor(registry, [source], null,
            _ => new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("https://worker.test") });
        var refresh = monitor.RefreshAsync();
        await sent.Task;
        monitor.Dispose();
        release.SetResult();
        await refresh;
        Assert.Null(registry.Get(new CapabilityId("knowledge.embed")));
    }

    private sealed class AsyncHandler(Func<Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => response();
    }

    [Fact]
    public void OptInRegistrationCreatesOneHostedMonitor()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICapabilityRegistry>(new CapabilityRegistry());
        var source = new WorkerCapabilitySource("https://worker.test", "token", ["knowledge.embed"]);
        services.AddOpenConnectWorkerCapabilityMonitor([source]);
        Assert.Throws<InvalidOperationException>(() => services.AddOpenConnectWorkerCapabilityMonitor([source]));
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        Assert.IsType<WorkerCapabilityMonitor>(Assert.Single(provider.GetServices<IHostedService>()));
    }

    [Fact]
    public void ConfigurationRequiresProtectedEndpointsAndExclusiveKnownIds()
    {
        Assert.Throws<ArgumentException>(() => new WorkerCapabilitySource("http://worker.test", "token", ["knowledge.embed"]));
        Assert.Throws<ArgumentException>(() => new WorkerCapabilitySource("https://worker.test", "token", ["unknown"]));
        Assert.Throws<ArgumentException>(() => new WorkerCapabilitySource("https://worker.test", "token", ["knowledge.embed"], inference: true));
        var source = new WorkerCapabilitySource("https://worker.test", "token", ["knowledge.embed"]);
        Assert.Throws<ArgumentException>(() => new WorkerCapabilityMonitor(new CapabilityRegistry(), [source, source]));
        var registry = new CapabilityRegistry();
        registry.Register(new CapabilityDescriptor(new CapabilityId("knowledge.embed"), CapabilityKind.Model, DetectorState.Ready));
        Assert.Throws<ArgumentException>(() => new WorkerCapabilityMonitor(registry, [source]));
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }
}
