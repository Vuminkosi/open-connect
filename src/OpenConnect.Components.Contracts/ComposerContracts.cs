using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenConnect.Components.Contracts;

/// <summary>Chat message surfaced by the composer. Plain text only; model output is never interpreted as markup.</summary>
public sealed record ComposerMessage(
    string Role,
    string Text,
    long Sequence = 0,
    ComposerMessageState State = ComposerMessageState.Received);

public enum ComposerMessageState
{
    Sending,
    Streaming,
    Received,
    Cancelled,
    Failed,
}

/// <summary>Connection status for the session event stream.</summary>
public enum ComposerConnectionState
{
    Idle,
    Connecting,
    Streaming,
    Reconnecting,
    Error,
}

public enum ComposerPlacement { BottomRight, BottomLeft, TopRight, TopLeft, Custom, LegacyViewport20Vh, Attached }
public enum ComposerDisplayState { Hidden, Closed, Opening, Open, Dragging, Closing }
public enum ComposerCloseBehavior { MinimizeOnly, CancelActiveTask, EndSession }
public enum ComposerInputAction { LiveVoiceOrSend, SendOnly, RecordedVoiceOrSend }
public enum ComposerTaskPhase { Ready, Connecting, Sending, Thinking, ToolExecuting, AwaitingApproval, Streaming, Reconnecting, Completed, Cancelling, Cancelled, Failed }
public enum ComposerMicrophoneState { Unavailable, Off, PermissionRequested, Recording, ListeningLive, Transcribing, Stopping, Failed }
public enum ComposerSpeechState { Unavailable, Off, On, Buffering, Playing, Paused, Failed }
public enum ComposerHistoryPresentation { LatestOnly, OnDemandHistory }
public enum ComposerUiIntegration { Floating, Attached }

/// <summary>Stable kebab-case JSON serialization for composer contract enums.</summary>
public static class ComposerWireValues
{
    public static string ToWireValue<TEnum>(TEnum value) where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        return System.Text.RegularExpressions.Regex
            .Replace(value.ToString(), "([a-z0-9])([A-Z])", "$1-$2")
            .ToLowerInvariant();
    }

    public static TEnum FromWireValue<TEnum>(string value) where TEnum : struct, Enum
    {
        ArgumentNullException.ThrowIfNull(value);

        foreach (var candidate in Enum.GetValues<TEnum>())
        {
            if (string.Equals(ToWireValue(candidate), value, StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        throw new JsonException($"Invalid {typeof(TEnum).Name} wire value.");
    }

    public static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new ComposerEnumJsonConverterFactory());
        return options;
    }

    public static T ValidateJson<T>(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var result = JsonSerializer.Deserialize<T>(json, CreateJsonOptions());
        return result is null ? throw new JsonException("Composer JSON cannot be null.") : result;
    }

    private sealed class ComposerEnumJsonConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert) =>
            typeToConvert.IsEnum && typeToConvert.Namespace == typeof(ComposerPlacement).Namespace;

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
            (JsonConverter)Activator.CreateInstance(typeof(ComposerEnumJsonConverter<>).MakeGenericType(typeToConvert))!;
    }

    private sealed class ComposerEnumJsonConverter<TEnum> : JsonConverter<TEnum> where TEnum : struct, Enum
    {
        public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.String
                ? FromWireValue<TEnum>(reader.GetString()!)
                : throw new JsonException("Composer enum values must be strings.");

        public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options) =>
            writer.WriteStringValue(ToWireValue(value));
    }
}

/// <summary>Presentation-only state exchanged by composer renderers.</summary>
public sealed record ComposerWireState
{
    public string Schema { get; init; } = "openconnect.composer/v1";
    public ComposerPlacement Placement { get; init; }
    public ComposerDisplayState DisplayState { get; init; }
    public ComposerTaskPhase TaskPhase { get; init; }
    public ComposerMicrophoneState MicrophoneState { get; init; }
    public ComposerSpeechState SpeechState { get; init; }
    public ComposerHistoryPresentation HistoryPresentation { get; init; }
    public ComposerUiIntegration UiIntegration { get; init; }
}
