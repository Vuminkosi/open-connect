using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
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
        services.AddSingleton<GatewayProviderRegistration>();
        return services;
    }

    /// <summary>Registers the existing firewall-guarded <see cref="Microsoft.Extensions.AI.IChatClient"/> as an optional LiveUI gateway planner.</summary>
    public static IServiceCollection AddFirewallGuardedLiveUIGatewayInference(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<FirewallGuardedLiveUIGatewayInferenceProvider>();
        services.AddScoped<ILiveUIInferenceProvider>(provider => provider.GetRequiredService<FirewallGuardedLiveUIGatewayInferenceProvider>());
        services.AddSingleton<GatewayProviderRegistration>();
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
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, LiveUIGatewayProviderStartupValidator>());
        return services;
    }
}

internal sealed class LiveUIGatewayProviderStartupValidator(
    IEnumerable<GatewayProviderRegistration> registrations,
    IOptionsMonitor<LiveUIInferenceOptions> options) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var settings = options.CurrentValue;
        if (!settings.Gateway.Enabled || settings.Mode == LiveUIInferenceMode.BrowserOnly)
        {
            return Task.CompletedTask;
        }

        var gatewayProviderCount = registrations.Count();

        if (gatewayProviderCount != 1)
        {
            var guidance = gatewayProviderCount == 0
                ? "Register exactly one Gateway provider with AddLiveUIGatewayInferenceProvider<TProvider>() or AddFirewallGuardedLiveUIGatewayInference()."
                : "Only one Gateway provider can be registered. Remove either the guarded chat-client provider or the external Gateway provider.";
            throw new InvalidOperationException(
                $"LiveUI inference mode '{settings.Mode}' requires exactly one Gateway provider, but {gatewayProviderCount} were registered. {guidance}");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

}

internal sealed class GatewayProviderRegistration
{
    public GatewayProviderRegistration() { }
}
