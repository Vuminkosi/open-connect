using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;

namespace OpenConnect.Hosting.AspNetCore.Fencing;

/// <summary>Registers SDK-mediated default-deny fenced HttpClient instances (FW-070).</summary>
public static class ServiceFencingServiceCollectionExtensions
{
    public static IServiceCollection AddOpenConnectServiceFencing(
        this IServiceCollection services,
        Action<ServiceFencingOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions<ServiceFencingOptions>();
        if (configure is not null)
            services.Configure(configure);

        services.TryAddSingleton<IServiceFencingContextAccessor, AsyncLocalServiceFencingContextAccessor>();
        services.TryAddSingleton<IFencedHostAddressResolver>(SystemFencedHostAddressResolver.Instance);
        services.TryAddSingleton<IServiceFencingEvidenceSink>(NullServiceFencingEvidenceSink.Instance);
        return services;
    }

    public static IHttpClientBuilder AddOpenConnectFencedHttpClient(
        this IServiceCollection services,
        string serviceId,
        Action<FencedServicePolicy> configurePolicy)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceId);
        ArgumentNullException.ThrowIfNull(configurePolicy);

        services.AddOpenConnectServiceFencing(options =>
        {
            var policy = new FencedServicePolicy { ServiceId = serviceId };
            configurePolicy(policy);
            policy.ServiceId = serviceId;
            options.RegisterService(policy);
        });

        return services.AddHttpClient(serviceId)
            .ConfigurePrimaryHttpMessageHandler(sp => FencedSocketsHttpHandlerFactory.Create(
                serviceId,
                sp.GetRequiredService<IOptionsMonitor<ServiceFencingOptions>>(),
                sp.GetService<IFencedHostAddressResolver>()))
            .AddHttpMessageHandler(sp => new FencedHttpMessageHandler(
                serviceId,
                sp.GetRequiredService<IOptionsMonitor<ServiceFencingOptions>>(),
                sp.GetRequiredService<IServiceFencingContextAccessor>(),
                sp.GetService<IFencedHostAddressResolver>(),
                sp.GetService<IServiceFencingEvidenceSink>(),
                sp.GetService<TimeProvider>(),
                sp.GetService<ISecurityEvidenceSink>()));
    }
}
