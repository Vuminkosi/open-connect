using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Navigator.Operational.Durable;

namespace OpenConnect.Navigator.AspNetCore.Outbox;

/// <summary>Registers commit-hook forwarding for the host's durable Navigator store.</summary>
public static class CommittingDurableNavigatorStoreExtensions
{
    /// <summary>
    /// Registers <see cref="CommittingDurableNavigatorStore"/> as the durable store.
    /// </summary>
    /// <remarks>
    /// Registration order is required: first register exactly one inner
    /// <see cref="IDurableNavigatorStore"/> (for example, a PostgreSQL store or a host-owned
    /// in-memory store), then register <c>INavigatorCommitHook</c> (for example with
    /// <c>AddNavigatorOutboxRelay</c>), and call this method last. Calling this before the inner
    /// store registers the decorator leaves the raw inner store as the resolved service, so commit
    /// hooks are not forwarded. Hook failures propagate after the durable commit has succeeded.
    /// </remarks>
    public static IServiceCollection AddCommittingNavigatorStore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<IDurableNavigatorStore, CommittingDurableNavigatorStore>();
        return services;
    }
}
