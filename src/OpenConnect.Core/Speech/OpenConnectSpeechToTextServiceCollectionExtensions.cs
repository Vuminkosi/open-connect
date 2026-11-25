using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OpenConnect.Abstractions.Speech;

namespace OpenConnect.Core.Speech;

/// <summary>Dependency injection registration extensions for OpenConnect speech-to-text clients.</summary>
public static class OpenConnectSpeechToTextServiceCollectionExtensions
{
    /// <summary>Registers OpenConnect realtime and upload speech-to-text clients with default options.</summary>
    public static IServiceCollection AddOpenConnectSpeechToText(this IServiceCollection services)
        => services.AddOpenConnectSpeechToText(_ => { });

    /// <summary>Registers OpenConnect realtime and upload speech-to-text clients with code-based configuration.</summary>
    public static IServiceCollection AddOpenConnectSpeechToText(
        this IServiceCollection services,
        Action<SpeechToTextOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<SpeechToTextOptions>()
            .Configure(configure);

        services.TryAddSingleton<IRealtimeSpeechToTextClient, WebSocketRealtimeSpeechToTextClient>();
        services.TryAddSingleton<ISpeechToTextUploadClient>(sp =>
            new MultipartSpeechToTextUploadClient(sp.GetRequiredService<IOptions<SpeechToTextOptions>>()));

        return services;
    }

    /// <summary>Registers OpenConnect speech-to-text clients bound from key-value configuration entries
    /// (for example <c>configuration.AsEnumerable()</c>), reading <c>OpenConnect:SpeechToText:*</c> with
    /// fallback to legacy <c>OpenConnect:ASR_*</c> and <c>OpenConnect:ApiKey</c> keys.</summary>
    public static IServiceCollection AddOpenConnectSpeechToText(
        this IServiceCollection services,
        IEnumerable<KeyValuePair<string, string?>> configurationValues)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configurationValues);

        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in configurationValues)
        {
            if (!string.IsNullOrEmpty(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
            {
                values[pair.Key] = pair.Value.Trim();
            }
        }

        return services.AddOpenConnectSpeechToText(options =>
        {
            if (TryGetFirst(values, out var realtimeUrl,
                "OpenConnect:SpeechToText:RealtimeWebSocketUrl",
                "OpenConnect:ASR_WSS"))
            {
                options.RealtimeWebSocketUrl = realtimeUrl;
            }

            if (TryGetFirst(values, out var uploadUrl,
                "OpenConnect:SpeechToText:UploadUrl",
                "OpenConnect:ASR_Transcription:URL",
                "OpenConnect:TranscriptionApiUrl"))
            {
                options.UploadUrl = uploadUrl;
            }

            if (TryGetFirst(values, out var apiKey,
                "OpenConnect:SpeechToText:ApiKey",
                "OpenConnect:ApiKey",
                "OpenConnect:MetaApiKey"))
            {
                options.ApiKey = apiKey;
            }

            if (TryGetFirst(values, out var model,
                "OpenConnect:SpeechToText:Model",
                "OpenConnect:ASR_MODEL",
                "OpenConnect:TranscriptionModel"))
            {
                options.Model = model;
            }

            if (TryGetFirst(values, out var mode,
                "OpenConnect:SpeechToText:Mode",
                "OpenConnect:ASRMode"))
            {
                options.Mode = mode;
            }

            if (TryGetFirst(values, out var language,
                "OpenConnect:SpeechToText:Language",
                "OpenConnect:Asr:Language"))
            {
                options.Language = language;
            }

            if (TryGetFirst(values, out var encoding,
                "OpenConnect:SpeechToText:AudioEncoding"))
            {
                options.AudioEncoding = encoding;
            }

            if (TryGetFirst(values, out var maxBytesText,
                "OpenConnect:SpeechToText:MaxAudioBytes")
                && long.TryParse(maxBytesText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxBytes)
                && maxBytes > 0)
            {
                options.MaxAudioBytes = maxBytes;
            }

            if (TryGetFirst(values, out var timeoutText,
                "OpenConnect:SpeechToText:HandshakeTimeoutSeconds")
                && int.TryParse(timeoutText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var timeoutSeconds)
                && timeoutSeconds > 0)
            {
                options.HandshakeTimeoutSeconds = timeoutSeconds;
            }
        });
    }

    private static bool TryGetFirst(
        Dictionary<string, string?> values,
        out string value,
        params string[] keys)
    {
        foreach (var key in keys)
        {
            if (values.TryGetValue(key, out var candidate) && !string.IsNullOrWhiteSpace(candidate))
            {
                value = candidate;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }
}
