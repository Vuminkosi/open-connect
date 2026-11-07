using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Navigator.Operational.Durable;

namespace OpenConnect.Navigator.Persistence.Postgres;

/// <summary>Registration for the durable Navigator PostgreSQL adapter.</summary>
public static class NavigatorPostgresServiceCollectionExtensions
{
    /// <summary>Registers PostgreSQL-backed Navigator persistence against the host-configured DbContext.</summary>
    public static IServiceCollection AddNavigatorPostgresStore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<IDurableNavigatorStore, PostgresDurableNavigatorStore>();
        services.AddScoped<IDurableOperationalStore>(provider => provider.GetRequiredService<PostgresDurableNavigatorStore>());
        return services;
    }
}
