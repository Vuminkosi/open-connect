using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace OpenConnect.Client;

public static class OpenConnectClientServiceCollectionExtensions
{
    public static IServiceCollection AddOpenConnectClient(
        this IServiceCollection services,
        Uri baseAddress,
        Action<IHttpClientBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(baseAddress);
        if (!baseAddress.IsAbsoluteUri
            || baseAddress.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(baseAddress.Query)
            || !string.IsNullOrEmpty(baseAddress.Fragment))
        {
            throw new ArgumentException("The base address must be an absolute HTTPS URL without a query or fragment.", nameof(baseAddress));
        }

        var normalizedBaseAddress = new Uri(baseAddress.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
        services.TryAddSingleton(new OpenConnectClientOptions { ApiBaseAddress = normalizedBaseAddress });
        var builder = services.AddHttpClient<IOpenConnectClient, OpenConnectClient>(client => client.BaseAddress = normalizedBaseAddress);
        configure?.Invoke(builder);
        return services;
    }

    public static IServiceCollection AddOpenConnectClient(
        this IServiceCollection services,
        Action<OpenConnectClientOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new OpenConnectClientOptions();
        configure(options);
        options.Validate();

        services.RemoveAll<OpenConnectClientOptions>();
        services.AddSingleton(options);
        services.Configure(configure);

        var normalizedBaseAddress = new Uri(options.ApiBaseAddress!.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
        services.AddHttpClient<IOpenConnectClient, OpenConnectClient>(client => client.BaseAddress = normalizedBaseAddress);
        return services;
    }

    public static IServiceCollection AddOpenConnectClientVad(
        this IServiceCollection services,
        Action<ClientVadOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new ClientVadOptions();
        configure?.Invoke(options);
        options.Validate();

        services.RemoveAll<ClientVadOptions>();
        services.AddSingleton(options);
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.RemoveAll<IClientVadService>();
        services.AddSingleton<IClientVadService>(new ClientVadService(options));
        return services;
    }

    public static IServiceCollection AddOpenConnectVoiceClient(
        this IServiceCollection services,
        Action<VoiceClientOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new VoiceClientOptions();
        configure?.Invoke(options);
        options.Validate();

        services.RemoveAll<VoiceClientOptions>();
        services.AddSingleton(options);
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.RemoveAll<IVoiceClientService>();
        services.AddScoped<IVoiceClientService>(_ => new VoiceClientService(options));
        return services;
    }

    public static IServiceCollection AddOpenConnectMauiAudio(
        this IServiceCollection services,
        Action<MauiAudioOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new MauiAudioOptions();
        configure?.Invoke(options);
        options.Validate();

        services.RemoveAll<MauiAudioOptions>();
        services.AddSingleton(options);
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.TryAddSingleton(new ClientVadOptions());
        services.TryAddSingleton<IClientVadService>(sp => new ClientVadService(sp.GetRequiredService<ClientVadOptions>()));
        services.RemoveAll<IMauiAudioCaptureService>();
        services.AddSingleton<IMauiAudioCaptureService>(sp =>
            new MauiAudioCaptureService(options, sp.GetRequiredService<IClientVadService>()));
        return services;
    }

    public static IServiceCollection AddOpenConnectNavigatorClient(
        this IServiceCollection services,
        Action<NavigatorClientOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new NavigatorClientOptions();
        configure?.Invoke(options);
        options.Validate();

        services.RemoveAll<NavigatorClientOptions>();
        services.AddSingleton(options);
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.RemoveAll<INavigatorClientService>();
        services.AddScoped<INavigatorClientService>(_ => new NavigatorClientService(options));
        return services;
    }
}
