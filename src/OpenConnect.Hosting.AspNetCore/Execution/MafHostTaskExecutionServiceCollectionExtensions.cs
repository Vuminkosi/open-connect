using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace OpenConnect.Hosting.AspNetCore.Execution;

/// <summary>Opt-in registration for the native MAF host-task executor.</summary>
public static class MafHostTaskExecutionServiceCollectionExtensions
{
    /// <summary>Registers the executor without supplying defaults for host authority or persistence dependencies.</summary>
    /// <param name="services">Service collection to configure.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddMafHostTaskExecutor(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<IHostTaskExecutor, MafHostTaskExecutor>();
        return services;
    }
}
