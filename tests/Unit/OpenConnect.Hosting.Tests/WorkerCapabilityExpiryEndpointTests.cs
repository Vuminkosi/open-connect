using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Abstractions;
using OpenConnect.Core;
using Xunit;

namespace OpenConnect.Hosting.AspNetCore.Tests;

public class WorkerCapabilityExpiryEndpointTests
{
    private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public void Advance(TimeSpan by) => _now += by;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class TestRouteBuilder(IServiceProvider serviceProvider) : IEndpointRouteBuilder
    {
        public IServiceProvider ServiceProvider { get; } = serviceProvider;
        public ICollection<EndpointDataSource> DataSources { get; } = new List<EndpointDataSource>();
        public IApplicationBuilder CreateApplicationBuilder() => throw new NotImplementedException();
    }

    [Fact]
    public async Task NavigatorStatusStaysReadyAfterOneHour()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddRouting();
        services.AddAuthorization();
        services.AddLogging();
        services.AddOpenConnect();
        services.AddOpenConnectPlaywrightWorker(o => { o.Enabled = true; o.Endpoint = new Uri("https://playwright.internal"); });
        using var sp = services.BuildServiceProvider();
        var builder = new TestRouteBuilder(sp);
        builder.MapOpenConnectNavigator();
        var endpoint = builder.DataSources.SelectMany(s => s.Endpoints).OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == "/openconnect/v1/navigator/status");

        var before = await InvokeAsync(endpoint, sp);
        clock.Advance(TimeSpan.FromMinutes(61));
        var after = await InvokeAsync(endpoint, sp);

        Assert.True(before.GetProperty("ready").GetBoolean());
        Assert.Equal(1, before.GetProperty("count").GetInt32());
        Assert.True(after.GetProperty("ready").GetBoolean());
        Assert.Equal(1, after.GetProperty("count").GetInt32());
    }

    private static async Task<JsonElement> InvokeAsync(Endpoint endpoint, IServiceProvider services)
    {
        var context = new DefaultHttpContext { RequestServices = services };
        context.Response.Body = new MemoryStream();
        await endpoint.RequestDelegate!(context);
        context.Response.Body.Position = 0;
        using var doc = await JsonDocument.ParseAsync(context.Response.Body);
        return doc.RootElement.Clone();
    }
}
