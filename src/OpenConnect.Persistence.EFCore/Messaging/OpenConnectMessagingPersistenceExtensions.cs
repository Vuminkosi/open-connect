using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenConnect.Abstractions.Messaging;
using OpenConnect.Persistence.EFCore.Context;

namespace OpenConnect.Persistence.EFCore.Messaging;

public static class OpenConnectMessagingPersistenceExtensions
{
    public static IServiceCollection AddOpenConnectMessagingPersistence(this IServiceCollection services)
        => services.AddOpenConnectMessagingPersistence<OpenConnectDbContext>();

    public static IServiceCollection AddOpenConnectMessagingPersistence<TContext>(this IServiceCollection services)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<EfMessagingConversationStore<TContext>>();
        services.TryAddScoped<IMessagingConversationStore>(sp => sp.GetRequiredService<EfMessagingConversationStore<TContext>>());

        services.TryAddScoped<EfDeviceEnrollmentStore<TContext>>();
        services.TryAddScoped<IDeviceEnrollmentStore>(sp => sp.GetRequiredService<EfDeviceEnrollmentStore<TContext>>());

        services.TryAddScoped<EfMessagingCallStore<TContext>>();
        services.TryAddScoped<IMessagingCallStore>(sp => sp.GetRequiredService<EfMessagingCallStore<TContext>>());

        services.TryAddScoped<EfFollowUpStore<TContext>>();
        services.TryAddScoped<IFollowUpStore>(sp => sp.GetRequiredService<EfFollowUpStore<TContext>>());

        return services;
    }
}
