using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace OpenConnect.LiveUI.Inference;

public static class LiveUIInferenceServiceCollectionExtensions
{
    public static IServiceCollection AddLiveUIGatewayInferenceProvider<TProvider>(this IServiceCollection services)
        where TProvider : class, ILiveUIGatewayInferenceProvider
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<TProvider>();
        services.AddScoped<ILiveUIInferenceProvider>(provider => provider.GetRequiredService<TProvider>());
        return services;
    }

    /// <summary>Registers the existing firewall-guarded <see cref="Microsoft.Extensions.AI.IChatClient"/> as an optional LiveUI gateway planner.</summary>
    public static IServiceCollection AddFirewallGuardedLiveUIGatewayInference(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<FirewallGuardedLiveUIGatewayInferenceProvider>();
        services.AddScoped<ILiveUIInferenceProvider>(provider => provider.GetRequiredService<FirewallGuardedLiveUIGatewayInferenceProvider>());
        return services;
    }

    public static IServiceCollection AddOpenConnectLiveUIInference(this IServiceCollection services, Action<LiveUIInferenceOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions<LiveUIInferenceOptions>().Configure(options => configure?.Invoke(options))
            .Validate(options => Enum.IsDefined(options.Mode), "Mode must be a supported LiveUI inference mode.")
            .Validate(options => Enum.IsDefined(options.Browser.Provider), "Browser.Provider must be a supported LiveUI browser provider.")
            .Validate(options => Enum.IsDefined(options.Gateway.FallbackPolicy), "Gateway.FallbackPolicy must be a supported LiveUI fallback policy.")
            .Validate(options => options.Planning.MaxActions is > 0 and <= 32, "Planning.MaxActions must be between 1 and 32.")
            .Validate(options => options.Planning.Timeout > TimeSpan.Zero && options.Planning.Timeout <= TimeSpan.FromMinutes(2), "Planning.Timeout must be between zero and two minutes.")
            .Validate(options => options.Security.RequireValidatedPlans, "LiveUI inference plans must always be validated.")
            .ValidateOnStart();
        services.TryAddSingleton<LiveUIActionPlanValidator>();
        services.TryAddSingleton<LiveUIInferenceActionMapper>();
        services.TryAddScoped<ILiveUIInferenceRouter, LiveUIInferenceRouter>();
        return services;
    }
}
