using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenConnect.Navigator.AspNetCore.Outbox;
using OpenConnect.Navigator.Operational;
using OpenConnect.Navigator.Operational.Durable;
using OpenConnect.Transport.Nats;

namespace OpenConnect.Navigator.Persistence.Postgres;

/// <summary>Registration for the durable Navigator PostgreSQL adapter.</summary>
public static class NavigatorPostgresServiceCollectionExtensions
{
    /// <summary>
    /// Registers a durable operational handler. Call <see cref="AddNavigatorPostgresStore"/> first.
    /// The host-owned identity, plan, and executor services must be thread-safe singletons.
    /// The handler permits read-only tours only.
    /// </summary>
    public static IServiceCollection AddNavigatorOperationalHandler(
        this IServiceCollection services,
        IOperationalWorkloadIdentityProvider identities,
        IOperationalPlanProvider plans,
        IOperationalStepExecutor executor)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(identities);
        ArgumentNullException.ThrowIfNull(plans);
        ArgumentNullException.ThrowIfNull(executor);

        // AddNavigatorPostgresStore maps the operational store factory through this concrete type.
        services.TryAddScoped<PostgresDurableNavigatorStore>();
        services.TryAddScoped<DurableIdempotencyStore>();
        services.AddScoped<IOperationalHandler>(provider => new OperationalNavigatorHandler(
            provider.GetRequiredService<IOperationalLeaseStore>(),
            identities,
            plans,
            executor,
            provider.GetRequiredService<DurableIdempotencyStore>()));
        return services;
    }

    /// <summary>Registers PostgreSQL-backed Navigator persistence and operational leases against the host-configured DbContext.</summary>
    public static IServiceCollection AddNavigatorPostgresStore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<IDurableNavigatorStore, PostgresDurableNavigatorStore>();
        services.AddScoped<PostgresExpiredLeaseSweeper>();
        services.AddScoped<IOutboxRelaySource, NavigatorPostgresOutboxRelaySource>();
        services.TryAddScoped<INavigatorOutboxRelay, NavigatorOutboxRelayAdapter>();
        services.AddScoped<IDurableOperationalStore>(provider => provider.GetRequiredService<PostgresDurableNavigatorStore>());
        // Single-host default; multi-host deployments require distinct owners as a separately approved follow-up.
        services.AddScoped<IOperationalLeaseStore>(provider => new DurableOperationalLeaseStore(provider.GetRequiredService<IDurableOperationalStore>(), "navigator-operational"));
        return services;
    }
}
