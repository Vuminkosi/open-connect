using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenConnect.Navigator.AspNetCore.Outbox;
using Xunit;

namespace OpenConnect.Navigator.AspNetCore.Tests.Outbox;

public sealed class NavigatorOutboxDiTests
{
    [Fact]
    public async Task AddNavigatorOutboxRelay_without_relay_starts_host_and_worker_idles()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Services.AddNavigatorOutboxRelay();

        using var host = builder.Build();

        await host.StartAsync();
        await host.StopAsync();

        Assert.Contains(host.Services.GetServices<IHostedService>(),
            service => service is NavigatorOutboxRelayWorker);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AddNavigatorOutboxRelay_delivers_independent_of_registration_order(bool relayFirst)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        var relay = new FakeNavigatorOutboxRelay();
        if (relayFirst)
            builder.Services.AddSingleton<INavigatorOutboxRelay>(relay);
        builder.Services.AddNavigatorOutboxRelay();
        if (!relayFirst)
            builder.Services.AddSingleton<INavigatorOutboxRelay>(relay);

        using var host = builder.Build();

        await host.StartAsync();
        await relay.Delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await host.StopAsync();
        Assert.Equal(1, relay.Attempts);
    }

    private sealed class FakeNavigatorOutboxRelay : INavigatorOutboxRelay
    {
        public TaskCompletionSource Delivered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Attempts { get; private set; }

        public Task<int> RelayOnceAsync(int limit, Guid leaseId, CancellationToken cancellationToken) =>
            DeliverAsync();

        private Task<int> DeliverAsync()
        {
            Attempts++;
            Delivered.TrySetResult();
            return Task.FromResult(1);
        }
    }
}
