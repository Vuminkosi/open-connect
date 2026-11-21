using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace OpenConnect.LiveUI.Inference;

/// <summary>Server-only connection settings for Cloud LiveUI.</summary>
public sealed class CloudLiveUIOptions
{
    public Uri? Origin { get; set; }
    public string? Credential { get; set; }
}

/// <summary>Registers the opt-in Cloud LiveUI provider.</summary>
public static class CloudLiveUIServiceCollectionExtensions
{
    /// <summary>Registers Cloud transport and requires a host-provided session and route authorizer.</summary>
    /// <remarks>The credential must be supplied by server-side secret configuration and never exposed to a browser.</remarks>
    public static IServiceCollection AddCloudLiveUIInferenceProvider(this IServiceCollection services, Action<CloudLiveUIOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        services.AddOptions<CloudLiveUIOptions>().Configure(configure)
            .Validate(options => options.Origin is { IsAbsoluteUri: true } origin && origin.Scheme == Uri.UriSchemeHttps &&
                origin.AbsolutePath == "/" && string.IsNullOrEmpty(origin.UserInfo) && string.IsNullOrEmpty(origin.Query) && string.IsNullOrEmpty(origin.Fragment),
                "The Cloud LiveUI origin must be an HTTPS origin.")
            .Validate(options => options.Credential is { Length: > 0 and <= 160 } credential &&
                credential.All(character => character > ' ' && character <= '~' && character != ','),
                "A Cloud LiveUI server credential is required.")
            .ValidateOnStart();
        services.TryAddSingleton<ICloudLiveUITransport>(provider =>
        {
            var settings = provider.GetRequiredService<IOptions<CloudLiveUIOptions>>().Value;
            return new CloudLiveUIHttpTransport(settings.Origin!, settings.Credential!);
        });
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, CloudLiveUIHostStartupValidator>());
        services.AddLiveUIGatewayInferenceProvider<CloudLiveUIInferenceProvider>();
        return services;
    }
}

internal sealed class CloudLiveUIHostStartupValidator(IServiceProviderIsService services) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!services.IsService(typeof(ICloudLiveUIHostAuthorizer)) || !services.IsService(typeof(OpenConnect.Privacy.IEgressGate)))
            throw new InvalidOperationException("Cloud LiveUI requires host session authorization and an egress gate.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
