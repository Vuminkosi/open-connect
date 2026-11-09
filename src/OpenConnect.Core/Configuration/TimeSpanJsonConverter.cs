using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenConnect.Core.Configuration;

/// <summary>Reads and writes durations using the invariant constant (<c>c</c>) TimeSpan format.</summary>
/// <remarks>Configuration values may use <c>hh:mm:ss</c> or <c>d.hh:mm:ss</c>, including fractional seconds supported by the constant format.</remarks>
internal sealed class TimeSpanJsonConverter : JsonConverter<TimeSpan>
{
    public override TimeSpan Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String ||
            !TimeSpan.TryParseExact(reader.GetString(), "c", CultureInfo.InvariantCulture, out var value))
            throw new JsonException("Duration must use the invariant constant TimeSpan format.");

        return value;
    }

    public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString("c", CultureInfo.InvariantCulture));
}
