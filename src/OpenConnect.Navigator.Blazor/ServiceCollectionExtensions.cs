using Microsoft.Extensions.DependencyInjection;

namespace OpenConnect.Navigator.Blazor;

/// <summary>
/// DI registration for the Blazor navigator adapter. Registers only the
/// scoped <see cref="NavigatorTourService"/> (tour state + target registry).
/// The host keeps owning its existing providers (including
/// <c>OpenConnectProvider</c> and <c>MiniComposer</c>); this adapter never
/// registers or replaces them.
/// </summary>
public static class NavigatorBlazorServiceCollectionExtensions
{
    /// <summary>
    /// Registers the scoped navigator tour state for Blazor Server, WASM,
    /// and static SSR (SSR-safe: no JS interop is registered or required).
    /// </summary>
    public static IServiceCollection AddOpenConnectNavigator(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<NavigatorTourService>();
        return services;
    }
}
