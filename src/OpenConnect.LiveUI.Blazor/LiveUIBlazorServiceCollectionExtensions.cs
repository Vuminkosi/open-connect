using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenConnect.Client;
using OpenConnect.LiveUI;
using OpenConnect.LiveUI.Agents;
using OpenConnect.LiveUI.Inference;

namespace OpenConnect.LiveUI.Blazor;

public static class LiveUIBlazorServiceCollectionExtensions
{
    public static IServiceCollection AddOpenConnectLiveUIBlazor(
        this IServiceCollection services,
        Action<LiveUIOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOpenConnectLiveUI(configure);
        services.AddOpenConnectLiveUIAgentTools();
        services.AddOpenConnectLiveUIInference();
        services.TryAddScoped<BrowserPromptApiInferenceProvider>();
        services.TryAddScoped<ILiveUIInferenceProvider>(provider => provider.GetRequiredService<BrowserPromptApiInferenceProvider>());
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(GatewayWorkerLiveUIInferenceProvider)))
        {
            services.AddHttpClient();
            services.AddScoped(provider =>
            {
                var clientOptions = provider.GetService<OpenConnectClientOptions>();
                if (clientOptions is null)
                    return new GatewayWorkerLiveUIInferenceProvider(new HttpClient(new UnavailableGatewayHandler())
                    {
                        BaseAddress = new Uri("http://openconnect.invalid/")
                    });
                var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(IOpenConnectClient));
                client.BaseAddress = clientOptions.ApiBaseAddress;
                client.Timeout = Timeout.InfiniteTimeSpan;
                foreach (var header in clientOptions.WebSocketHeaders)
                {
                    if (!client.DefaultRequestHeaders.Contains(header.Key))
                        client.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
                }
                return new GatewayWorkerLiveUIInferenceProvider(client);
            });
            services.AddScoped<ILiveUIInferenceProvider>(provider => provider.GetRequiredService<GatewayWorkerLiveUIInferenceProvider>());
        }
        services.TryAddScoped<LiveUIManifestState>();
        services.TryAddScoped<LiveUIInteractionReplayFence>();
        services.TryAddScoped<ILiveUIManifestProvider>(provider => provider.GetRequiredService<LiveUIManifestState>());
        services.TryAddScoped<ILiveUIRuntime, LiveUIBrowserRuntime>();
        services.TryAddScoped<ILiveUIActionAuthorizer, DenyLiveUIActionAuthorizer>();
        services.TryAddScoped<ILiveUIActionConfirmation, BrowserLiveUIActionConfirmation>();
        services.TryAddScoped<ILiveUIAgentContextAccessor, BlazorLiveUIAgentContextAccessor>();
        services.TryAddScoped<ILiveUIRuntimeTargetDiscovery, LiveUIRuntimeTargetDiscovery>();
        return services;
    }

    private sealed class UnavailableGatewayHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
        }
    }
}
