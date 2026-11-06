using Microsoft.Extensions.DependencyInjection;
namespace OpenConnect.Client;
public static class OpenConnectClientServiceCollectionExtensions
{
    public static IServiceCollection AddOpenConnectClient(this IServiceCollection services, Uri baseAddress, Action<IHttpClientBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(baseAddress);
        if (!baseAddress.IsAbsoluteUri || baseAddress.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(baseAddress.Query) || !string.IsNullOrEmpty(baseAddress.Fragment)) throw new ArgumentException("The base address must be an absolute HTTPS URL without a query or fragment.", nameof(baseAddress));
        var builder = services.AddHttpClient<IOpenConnectClient, OpenConnectClient>(client => client.BaseAddress = new Uri(baseAddress.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute));
        configure?.Invoke(builder);
        return services;
    }
}
