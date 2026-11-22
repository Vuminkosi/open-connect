using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Core.Speech;

namespace OpenConnect.Hosting.AspNetCore;

/// <summary>ASP.NET Core configuration binding extensions for OpenConnect speech-to-text clients.</summary>
public static class SpeechToTextHostingServiceCollectionExtensions
{
    /// <summary>Registers OpenConnect realtime and upload speech-to-text clients bound from <see cref="IConfiguration"/>.</summary>
    public static IServiceCollection AddOpenConnectSpeechToText(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        return OpenConnectSpeechToTextServiceCollectionExtensions.AddOpenConnectSpeechToText(
            services,
            configuration.AsEnumerable());
    }
}
