using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Abstractions;
using OpenConnect.Core.Capabilities;
using Xunit;

namespace OpenConnect.Hosting.Tests;

/// <summary>
/// REG-S03 parity lock: the shared <see cref="CapabilityRegistry"/> is the single
/// source of truth projected by <c>GET /capabilities</c>. Stale (explicitly marked)
/// and TTL-expired registrations must never appear in the endpoint payload, and the
/// projected descriptor shape (id/kind/state, kebab-case enums) must agree with the
/// registry content. No second registry is introduced by this test.
/// </summary>
public sealed class CapabilityEndpointParityTests
{
    [Fact]
    public async Task Capabilities_Projects_Only_Live_Ready_Models_With_Registry_Shape()
    {
        var clock = new FakeTimeProvider();
        var registry = new CapabilityRegistry(TimeSpan.FromSeconds(10), clock);
        registry.Register(new CapabilityDescriptor(new CapabilityId("live-model"), CapabilityKind.Model, DetectorState.Ready));
        registry.Register(new CapabilityDescriptor(new CapabilityId("stale-model"), CapabilityKind.Model, DetectorState.Ready));
        registry.Register(new CapabilityDescriptor(new CapabilityId("loading-model"), CapabilityKind.Model, DetectorState.Loading));
        registry.Register(new CapabilityDescriptor(new CapabilityId("live-tool"), CapabilityKind.Tool, DetectorState.Ready));
        registry.MarkStale(new CapabilityId("stale-model"));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ICapabilityRegistry>(registry);
        var provider = services.BuildServiceProvider();

        // Advance past half the TTL: nothing expires yet; stale + non-ready must already be excluded.
        clock.Advance(TimeSpan.FromSeconds(5));
        var first = await ExecuteAsync(OpenConnect.Hosting.AspNetCore.EndpointRouteBuilderExtensions.HandleCapabilities(provider), provider);
        Assert.Equal(200, first.StatusCode);
        using (var firstDoc = JsonDocument.Parse(first.Body))
        {
            Assert.Equal(JsonValueKind.Array, firstDoc.RootElement.ValueKind);
            Assert.Single(firstDoc.RootElement.EnumerateArray());
            var only = firstDoc.RootElement[0];
            Assert.Equal("live-model", only.GetProperty("id").GetProperty("value").GetString());
            Assert.Equal("model", only.GetProperty("kind").GetString());
            Assert.Equal("ready", only.GetProperty("state").GetString());
        }

        // Advance past TTL without re-registration: the lease lapses and the endpoint goes empty.
        clock.Advance(TimeSpan.FromSeconds(5));
        var second = await ExecuteAsync(OpenConnect.Hosting.AspNetCore.EndpointRouteBuilderExtensions.HandleCapabilities(provider), provider);
        Assert.Equal(200, second.StatusCode);
        using var secondDoc = JsonDocument.Parse(second.Body);
        Assert.Equal(0, secondDoc.RootElement.GetArrayLength());
        Assert.Empty(registry.GetReady(CapabilityKind.Model));
    }

    [Fact]
    public async Task Capabilities_ReRegistration_Renews_Lease_And_Reappears()
    {
        var clock = new FakeTimeProvider();
        var registry = new CapabilityRegistry(TimeSpan.FromSeconds(10), clock);
        registry.Register(new CapabilityDescriptor(new CapabilityId("live-model"), CapabilityKind.Model, DetectorState.Ready));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ICapabilityRegistry>(registry);
        var provider = services.BuildServiceProvider();

        clock.Advance(TimeSpan.FromSeconds(9));
        registry.Register(new CapabilityDescriptor(new CapabilityId("live-model"), CapabilityKind.Model, DetectorState.Ready));
        clock.Advance(TimeSpan.FromSeconds(9));

        var result = await ExecuteAsync(OpenConnect.Hosting.AspNetCore.EndpointRouteBuilderExtensions.HandleCapabilities(provider), provider);
        Assert.Equal(200, result.StatusCode);
        Assert.Contains("live-model", Encoding.UTF8.GetString(result.Body));
    }

    private static async Task<Response> ExecuteAsync(IResult result, IServiceProvider? services = null)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = services ?? new ServiceCollection().BuildServiceProvider(),
            Response = { Body = new MemoryStream() },
        };
        await result.ExecuteAsync(context);
        return new Response(context.Response.StatusCode, ((MemoryStream)context.Response.Body).ToArray());
    }

    private sealed record Response(int StatusCode, byte[] Body);

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
