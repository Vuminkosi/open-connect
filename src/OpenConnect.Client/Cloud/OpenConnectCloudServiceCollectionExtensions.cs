using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace OpenConnect.Client.Cloud;

public static class OpenConnectCloudServiceCollectionExtensions
{
    public static IServiceCollection AddOpenConnectCloud(
        this IServiceCollection services,
        Action<OpenConnectCloudOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<OpenConnectCloudOptions>()
            .Configure(configure)
            .ValidateOnStart();

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<OpenConnectCloudOptions>, OpenConnectCloudOptionsValidator>());

        services.AddHttpClient<IOpenConnectCloudClient, OpenConnectCloudClient>((serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<OpenConnectCloudOptions>>().Value;
            client.BaseAddress = new Uri(options.Endpoint!.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
            client.Timeout = options.RequestTimeout;
        });

        return services;
    }

    private sealed class OpenConnectCloudOptionsValidator : IValidateOptions<OpenConnectCloudOptions>
    {
        public ValidateOptionsResult Validate(string? name, OpenConnectCloudOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            try
            {
                options.Validate();
                return ValidateOptionsResult.Success;
            }
            catch (Exception ex) when (ex is ArgumentException)
            {
                return ValidateOptionsResult.Fail(ex.Message);
            }
        }
    }
}
