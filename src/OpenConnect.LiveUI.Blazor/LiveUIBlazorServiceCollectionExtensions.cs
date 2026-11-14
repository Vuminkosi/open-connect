using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenConnect.LiveUI;

namespace OpenConnect.LiveUI.Blazor;

public static class LiveUIBlazorServiceCollectionExtensions
{
    public static IServiceCollection AddOpenConnectLiveUIBlazor(
        this IServiceCollection services,
        Action<LiveUIOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOpenConnectLiveUI(configure);
        services.TryAddScoped<LiveUIManifestState>();
        services.TryAddScoped<ILiveUIManifestProvider>(provider => provider.GetRequiredService<LiveUIManifestState>());
        services.TryAddScoped<ILiveUIRuntime, LiveUIBrowserRuntime>();
        services.TryAddScoped<ILiveUIActionAuthorizer, DenyLiveUIActionAuthorizer>();
        return services;
    }
}
