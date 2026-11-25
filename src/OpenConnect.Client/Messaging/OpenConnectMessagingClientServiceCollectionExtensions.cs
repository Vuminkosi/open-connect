using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace OpenConnect.Client.Messaging;

/// <summary>Additive DI registrations for profile-aware OpenConnect IMessaging clients.</summary>
public static class OpenConnectMessagingClientServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IMessagingProfileStorage"/> (defaulting to in-memory unless a platform store is
    /// registered first), <see cref="IMessagingProfileManager"/>, and <see cref="IOpenConnectMessagingClient"/>.
    /// Does not modify existing singleton <see cref="IOpenConnectClient"/> registrations.
    /// </summary>
    public static IServiceCollection AddOpenConnectMessagingClient(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IMessagingProfileStorage, InMemoryMessagingProfileStorage>();
        services.TryAddSingleton<IMessagingProfileManager, MessagingProfileManager>();
        services.TryAddSingleton<IOpenConnectMessagingClient, OpenConnectMessagingClient>();
        return services;
    }
}
