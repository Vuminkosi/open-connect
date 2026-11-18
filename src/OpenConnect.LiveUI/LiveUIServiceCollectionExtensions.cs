using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace OpenConnect.LiveUI;

public static class LiveUIServiceCollectionExtensions
{
    public static IServiceCollection AddOpenConnectLiveUI(
        this IServiceCollection services,
        Action<LiveUIOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IValidateOptions<LiveUIOptions>, LiveUIOptionsValidator>();
        services.AddOptions<LiveUIOptions>().Configure(options => configure?.Invoke(options)).ValidateOnStart();
        services.TryAddSingleton<LiveUIManifestValidator>();
        services.TryAddScoped<LiveUIOrchestrator>();
        return services;
    }
}
