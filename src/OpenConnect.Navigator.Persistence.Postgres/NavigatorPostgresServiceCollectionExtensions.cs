using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Navigator.Operational;
using OpenConnect.Navigator.Operational.Durable;
using OpenConnect.Transport.Nats;

namespace OpenConnect.Navigator.Persistence.Postgres;

/// <summary>Registration for the durable Navigator PostgreSQL adapter.</summary>
public static class NavigatorPostgresServiceCollectionExtensions
{
    /// <summary>Registers PostgreSQL-backed Navigator persistence and operational leases against the host-configured DbContext.</summary>
    public static IServiceCollection AddNavigatorPostgresStore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<IDurableNavigatorStore, PostgresDurableNavigatorStore>();
        services.AddScoped<IOutboxRelaySource, NavigatorPostgresOutboxRelaySource>();
        services.AddScoped<IDurableOperationalStore>(provider => provider.GetRequiredService<PostgresDurableNavigatorStore>());
        // Single-host default; multi-host deployments require distinct owners as a separately approved follow-up.
        services.AddScoped<IOperationalLeaseStore>(provider => new DurableOperationalLeaseStore(provider.GetRequiredService<IDurableOperationalStore>(), "navigator-operational"));
        return services;
    }
}
