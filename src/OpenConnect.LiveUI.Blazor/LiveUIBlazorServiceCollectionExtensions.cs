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
        if (services.Any(descriptor => descriptor.ServiceType == typeof(OpenConnectClientOptions)) &&
            !services.Any(descriptor => descriptor.ServiceType == typeof(GatewayWorkerLiveUIInferenceProvider)))
        {
            services.AddScoped(provider =>
            {
                var clientOptions = provider.GetRequiredService<OpenConnectClientOptions>();
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
}
