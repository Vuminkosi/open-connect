using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace OpenConnect.Navigator.AspNetCore.Outbox;

/// <summary>Registers Navigator outbox integration while leaving persistence and broker ownership to the host.</summary>
public static class NavigatorOutboxServiceCollectionExtensions
{
    public static IServiceCollection AddNavigatorOutboxRelay(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<NavigatorWsProjector>();
        services.TryAddSingleton<NavigatorWsDeliveryAdapter>();
        services.TryAddSingleton<NavigatorOutboxWriter>();
        services.TryAddSingleton<INavigatorCommitHook, NavigatorCommitHook>();
        // The worker resolves this optional host-owned dependency for each pass, so registration
        // order does not determine whether the relay is active.
        services.AddHostedService<NavigatorOutboxRelayWorker>();
        return services;
    }
}
