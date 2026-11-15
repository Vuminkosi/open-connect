using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
        services.TryAddScoped<LiveUIManifestState>();
        services.TryAddScoped<ILiveUIManifestProvider>(provider => provider.GetRequiredService<LiveUIManifestState>());
        services.TryAddScoped<ILiveUIRuntime, LiveUIBrowserRuntime>();
        services.TryAddScoped<ILiveUIActionAuthorizer, DenyLiveUIActionAuthorizer>();
        services.TryAddScoped<ILiveUIAgentContextAccessor, BlazorLiveUIAgentContextAccessor>();
        return services;
    }
}
