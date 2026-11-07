using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;

namespace OpenConnect.Navigator.AspNetCore;

public static class NavigatorServiceCollectionExtensions
{
    public static IServiceCollection AddOpenConnectNavigator(this IServiceCollection services, Action<NavigatorOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (!services.Any(d => d.ServiceType == typeof(IAgentDefinitionRegistry)))
            throw new InvalidOperationException("Register OpenConnect with AddOpenConnect() before AddOpenConnectNavigator().");

        services.TryAddSingleton<IValidateOptions<NavigatorOptions>, NavigatorOptionsValidator>();
        services.AddOptions<NavigatorOptions>().Configure(options => configure?.Invoke(options)).ValidateOnStart();
        services.TryAddSingleton<NavigatorTargetRegistry>();
        services.TryAddSingleton<NavigatorTourValidator>();
        services.TryAddSingleton<NavigatorMemoryStore>();
        return services;
    }
}
