using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace OpenConnect.Hosting.AspNetCore.Messaging;

/// <summary>DI registration helpers for OpenConnect IMessaging ASP.NET Core gateway hosting.</summary>
public static class MessagingHostingServiceCollectionExtensions
{
    /// <summary>Registers OpenConnect IMessaging gateway hosting services (disabled by default until <see cref="MessagingHostingOptions.Enabled"/> is set to <see langword="true"/>).</summary>
    public static IServiceCollection AddOpenConnectMessagingHosting(
        this IServiceCollection services,
        Action<MessagingHostingOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<MessagingHostingOptions>()
            .Configure(options => configure?.Invoke(options));

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<MessagingCursorCodec>();
        services.TryAddSingleton<IMessagingWebSocketTicketService, InMemoryMessagingWebSocketTicketService>();
        services.TryAddSingleton<MessagingRealtimeHub>();
        services.TryAddSingleton<IMessagingRealtimeHub>(sp => sp.GetRequiredService<MessagingRealtimeHub>());
        services.TryAddSingleton<IMessagingVoiceAdmissionBroker, DefaultMessagingVoiceAdmissionBroker>();

        return services;
    }

    /// <summary>Registers <see cref="MessagingFollowUpSupervisorService"/> as a hosted background service.</summary>
    public static IServiceCollection AddOpenConnectMessagingFollowUpSupervisor(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHostedService<MessagingFollowUpSupervisorService>();
        return services;
    }
}
