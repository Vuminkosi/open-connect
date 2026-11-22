using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenConnect.Abstractions.Speech;
using OpenConnect.Core.Speech;
using OpenConnect.Hosting.AspNetCore.Voice;

namespace OpenConnect.Hosting.AspNetCore;

/// <summary>ASP.NET Core configuration binding extensions for OpenConnect speech-to-text clients and gateway Silero ONNX VAD.</summary>
public static class SpeechToTextHostingServiceCollectionExtensions
{
    /// <summary>Registers OpenConnect realtime and upload speech-to-text clients bound from <see cref="IConfiguration"/>, along with the shared Silero ONNX <see cref="IVoiceActivityDetector"/>.</summary>
    public static IServiceCollection AddOpenConnectSpeechToText(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        OpenConnectSpeechToTextServiceCollectionExtensions.AddOpenConnectSpeechToText(
            services,
            configuration.AsEnumerable());
        services.TryAddSingleton<IVoiceActivityDetector>(_ =>
            new SileroOnnxVoiceActivityDetector(configuration["OpenConnect:Voice:Vad:ModelPath"]));
        return services;
    }

    /// <summary>Registers the shared Silero ONNX <see cref="IVoiceActivityDetector"/> for gateway-side voice activity detection.</summary>
    public static IServiceCollection AddOpenConnectSileroVad(
        this IServiceCollection services,
        string? modelPath = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IVoiceActivityDetector>(_ => new SileroOnnxVoiceActivityDetector(modelPath));
        return services;
    }
}
