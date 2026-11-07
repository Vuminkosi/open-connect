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
        // The host supplies INavigatorOutboxWriter and INavigatorOutboxRelay. Registering a hosted
        // worker without that relay would make an optional feature prevent application startup.
        // Production hosts should bind these to the durable shared outbox store/relay in Postgres.
        services.AddHostedService<NavigatorOutboxRelayWorker>();
        return services;
    }
}
