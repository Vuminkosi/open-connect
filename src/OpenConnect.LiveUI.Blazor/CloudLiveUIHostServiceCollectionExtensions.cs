using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenConnect.LiveUI.Inference;

namespace OpenConnect.LiveUI.Blazor;

/// <summary>Registers the Blazor Cloud LiveUI host authorization boundary.</summary>
public static class CloudLiveUIHostServiceCollectionExtensions
{
    /// <summary>Uses the application-provided session owner resolver and current authorized page.</summary>
    public static IServiceCollection AddCloudLiveUIBlazorHostAuthorizer<TSessionContext>(this IServiceCollection services)
        where TSessionContext : class, ICloudLiveUISessionContextProvider
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<ICloudLiveUISessionContextProvider, TSessionContext>();
        services.TryAddScoped<ICloudLiveUIPageProvider, CloudLiveUIPageProvider>();
        services.TryAddScoped<ICloudLiveUIHostAuthorizer, CloudLiveUIHostAuthorizer>();
        return services;
    }
}
