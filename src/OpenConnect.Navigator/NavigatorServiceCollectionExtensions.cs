using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OpenConnect.Core;
using OpenConnect.Core.Configuration;

namespace OpenConnect.Navigator;

public static class NavigatorServiceCollectionExtensions
{
    public static IServiceCollection AddOpenConnectNavigator(this IServiceCollection services, Action<NavigatorOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (!services.Any(x => x.ServiceType == typeof(IValidateOptions<OpenConnectOptions>)))
            throw new InvalidOperationException("Register OpenConnect with AddOpenConnect() before AddOpenConnectNavigator().");
        services.TryAddSingleton<IValidateOptions<NavigatorOptions>, NavigatorOptionsValidator>();
        services.AddOptions<NavigatorOptions>().Configure(options => configure?.Invoke(options)).ValidateOnStart();
        services.TryAddSingleton<NavigatorTargetRegistry>();
        services.TryAddSingleton<NavigatorTourValidator>();
        return services;
    }
}
