using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenConnect.Abstractions.Persistence;

namespace OpenConnect.Hosting.AspNetCore.Protection;

/// <summary>Registers native agent session protection using a host-configured Data Protection provider.</summary>
public static class NativeAgentSessionProtectionServiceCollectionExtensions
{
    /// <summary>
    /// Registers the session protector as a singleton. The host must independently register and configure
    /// <see cref="IDataProtectionProvider"/>; this method does not create a key ring or a fallback provider.
    /// </summary>
    /// <param name="services">The host service collection.</param>
    /// <returns>The same service collection.</returns>
    /// <exception cref="ArgumentNullException">The service collection is null.</exception>
    public static IServiceCollection AddOpenConnectNativeAgentSessionProtection(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<INativeAgentSessionProtector, DataProtectionNativeAgentSessionProtector>();
        return services;
    }
}
