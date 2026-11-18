using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenConnect.LiveUI.Inference;
using Xunit;

namespace OpenConnect.LiveUI.Blazor.Tests;

public sealed class LiveUIInferenceProviderRegistrationTests
{
    [Fact]
    public async Task GatewayPrimary_WithoutProvider_FailsAtHostStartup()
    {
        var services = new ServiceCollection();
        services.AddOpenConnectLiveUIInference(options =>
        {
            options.Mode = LiveUIInferenceMode.GatewayFirst;
            options.Gateway.Enabled = true;
        });

        using var host = new HostBuilder().ConfigureServices(collection =>
        {
            foreach (var service in services)
                collection.Add(service);
        }).Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());
        Assert.Contains("requires exactly one Gateway provider", exception.Message);
        Assert.Contains("AddLiveUIGatewayInferenceProvider", exception.Message);
    }

    [Fact]
    public async Task GatewayPrimary_WithOneProvider_StartsWithoutConstructingProvider()
    {
        Provider.Constructions = 0;
        var services = new ServiceCollection();
        services.AddOpenConnectLiveUIInference(options =>
        {
            options.Mode = LiveUIInferenceMode.GatewayFirst;
            options.Gateway.Enabled = true;
        });
        services.AddLiveUIGatewayInferenceProvider<Provider>();

        using var host = new HostBuilder().ConfigureServices(collection =>
        {
            foreach (var service in services)
                collection.Add(service);
        }).Build();

        await host.StartAsync();

        Assert.Equal(0, Provider.Constructions);
        await host.StopAsync();
    }

    [Fact]
    public async Task DefaultRegistration_StartsWithoutOptionalProviders()
    {
        using var host = new HostBuilder().ConfigureServices(services => services.AddOpenConnectLiveUIInference()).Build();
        await host.StartAsync();
        await host.StopAsync();
    }

    [Fact]
    public async Task BrowserFirst_WithEnabledButMissingGateway_FailsAtStartup()
    {
        using var host = new HostBuilder().ConfigureServices(services => services.AddOpenConnectLiveUIInference(options =>
            options.Gateway.Enabled = true)).Build();
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());
    }

    [Fact]
    public async Task BrowserOnly_DoesNotRequireGatewayProvider()
    {
        var services = new ServiceCollection();
        services.AddOpenConnectLiveUIInference(options => options.Mode = LiveUIInferenceMode.BrowserOnly);

        using var host = new HostBuilder().ConfigureServices(collection =>
        {
            foreach (var service in services)
                collection.Add(service);
        }).Build();

        await host.StartAsync();
        await host.StopAsync();
    }

    public sealed class Provider : ILiveUIGatewayInferenceProvider
    {
        public static int Constructions;

        public Provider() => Interlocked.Increment(ref Constructions);

        public LiveUIInferenceProviderKind Kind => LiveUIInferenceProviderKind.Gateway;

        public Task<LiveUIInferenceAvailability> GetAvailabilityAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<LiveUIInferenceResult> GeneratePlanAsync(
            LiveUIInferenceRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
