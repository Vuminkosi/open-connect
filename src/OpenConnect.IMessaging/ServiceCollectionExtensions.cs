using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenConnect.Abstractions.Messaging;
using OpenConnect.Core.Messaging;

namespace OpenConnect.IMessaging;

/// <summary>Dependency injection registration extensions for OpenConnect IMessaging, device enrollment, and agent discovery.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the OpenConnect IMessaging worker client, capability registration, event consumer,
    /// cryptographic envelope service, and domain coordinators. Disabled by default until explicitly enabled.
    /// </summary>
    public static IServiceCollection AddOpenConnectMessaging(
        this IServiceCollection services,
        Action<MessagingWorkerOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.Configure<MessagingWorkerOptions>(options =>
        {
            configure?.Invoke(options);
        });

        services.AddOptions<MessagingWorkerOptions>()
            .Validate(options =>
            {
                options.Validate();
                return true;
            }, "Invalid MessagingWorkerOptions configuration.")
            .ValidateOnStart();

        services.TryAddSingleton<IMessagingCryptoProvider, MessagingCryptoEnvelopeService>();
        services.TryAddSingleton<IMessagingWorkerClient, MessagingWorkerClient>();
        services.TryAddSingleton<CapabilityRegistration>();
        services.TryAddSingleton<IMessagingEventDispatcher, WorkerEventConsumer>();

        services.TryAddScoped<IMessagingCoordinator, ConversationCoordinator>();
        services.TryAddScoped<IMessagingCallCoordinator, MessagingCallCoordinator>();
        services.TryAddScoped<IFollowUpSchedulerCoordinator, FollowUpSchedulerCoordinator>();

        return services;
    }

    /// <summary>Registers desktop QR device enrollment options and coordinator.</summary>
    public static IServiceCollection AddOpenConnectDeviceEnrollment(
        this IServiceCollection services,
        Action<DeviceEnrollmentOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.Configure<DeviceEnrollmentOptions>(options =>
        {
            configure?.Invoke(options);
        });

        services.TryAddSingleton<IMessagingCryptoProvider, MessagingCryptoEnvelopeService>();
        services.TryAddScoped<IDeviceEnrollmentCoordinator, DeviceEnrollmentCoordinator>();
        return services;
    }

    /// <summary>Registers operational agent discovery options.</summary>
    public static IServiceCollection AddOpenConnectAgentDiscovery(
        this IServiceCollection services,
        Action<AgentDiscoveryOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.Configure<AgentDiscoveryOptions>(options =>
        {
            configure?.Invoke(options);
        });

        return services;
    }
}
