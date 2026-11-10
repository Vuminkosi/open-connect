using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenConnect.Abstractions;
using OpenConnect.Privacy;

namespace OpenConnect.Agents.Execution;

/// <summary>Registers firewall-guarded decoration for the model-bound chat client.</summary>
public static class FirewallGuardedChatClientExtensions
{
    /// <summary>Adds a fixed egress context and decorates the registered <see cref="IChatClient"/> with firewall guarding.</summary>
    /// <param name="services">Service collection to configure.</param>
    /// <param name="context">Egress context applied to model-bound content.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddFirewallGuardedChatClient(this IServiceCollection services, EgressContext context)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(context);

        services.TryAddSingleton<IEgressContextProvider>(new FixedEgressContextProvider(context));

        var original = services.LastOrDefault(descriptor => descriptor.ServiceType == typeof(IChatClient));
        if (original is null) return services;

        services.Replace(ServiceDescriptor.Describe(typeof(IChatClient), provider =>
        {
            var gate = provider.GetRequiredService<IEgressGate>();
            var contextProvider = provider.GetRequiredService<IEgressContextProvider>();
            var inner = ResolveInner(provider, original);
            if (inner is FirewallGuardedChatClient) return inner;
            return new FirewallGuardedChatClient(inner, gate, contextProvider);
        }, original.Lifetime));

        return services;
    }

    private static IChatClient ResolveInner(IServiceProvider provider, ServiceDescriptor original)
    {
        if (original.ImplementationInstance is IChatClient instance) return instance;
        if (original.ImplementationFactory is not null)
        {
            if (original.ImplementationFactory(provider) is IChatClient created) return created;
            throw new InvalidOperationException("The registered IChatClient factory did not return an IChatClient.");
        }
        if (original.ImplementationType is not null)
            return (IChatClient)ActivatorUtilities.GetServiceOrCreateInstance(provider, original.ImplementationType);
        throw new InvalidOperationException("The registered IChatClient descriptor cannot be decorated.");
    }
}
