using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenConnect.Abstractions.Serialization;

/// <summary>Creates JSON options for OpenConnect task and session HTTP contracts.</summary>
public static class OpenConnectHttpJson
{
    /// <summary>Creates options that serialize ULID identifiers as canonical JSON strings.</summary>
    /// <param name="defaults">The JSON defaults to preserve; web defaults are used when omitted.</param>
    /// <returns>Independent serializer options configured for OpenConnect HTTP identifiers.</returns>
    public static JsonSerializerOptions CreateOptions(JsonSerializerDefaults defaults = JsonSerializerDefaults.Web)
    {
        var options = new JsonSerializerOptions(defaults);
        options.Converters.Add(new UlidJsonConverter());
        options.Converters.Add(new UlidIdentifierJsonConverterFactory());
        return options;
    }
}

/// <summary>Reads and writes canonical ULID JSON strings for OpenConnect HTTP identifiers.</summary>
public sealed class UlidJsonConverter : JsonConverter<Ulid>
{
    /// <inheritdoc />
    public override Ulid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("A ULID identifier must be a JSON string.");

        var text = reader.GetString();
        try
        {
            var value = Ulid.Parse(text!);
            if (value == default || !string.Equals(value.ToString(), text, StringComparison.Ordinal))
                throw new JsonException("A ULID identifier must be a nonzero canonical ULID string.");
            return value;
        }
        catch (FormatException exception)
        {
            throw new JsonException("The ULID identifier is malformed or out of range.", exception);
        }
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, Ulid value, JsonSerializerOptions options)
    {
        if (value == default)
            throw new JsonException("A ULID identifier must not be zero.");
        writer.WriteStringValue(value.ToString());
    }
}

/// <summary>Creates converters only for OpenConnect's approved ULID identifier wrappers.</summary>
public sealed class UlidIdentifierJsonConverterFactory : JsonConverterFactory
{
    /// <inheritdoc />
    public override bool CanConvert(Type typeToConvert) => typeToConvert == typeof(TenantId)
        || typeToConvert == typeof(PrincipalId)
        || typeToConvert == typeof(SessionId)
        || typeToConvert == typeof(TaskId)
        || typeToConvert == typeof(ExecutionId);

    /// <inheritdoc />
    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        if (!CanConvert(typeToConvert))
            throw new InvalidOperationException($"No ULID identifier converter is defined for {typeToConvert}.");

        return (JsonConverter)Activator.CreateInstance(typeof(IdentifierConverter<>).MakeGenericType(typeToConvert), nonPublic: true)!;
    }

    private sealed class IdentifierConverter<TIdentifier> : JsonConverter<TIdentifier>
    {
        public override TIdentifier Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var value = new UlidJsonConverter().Read(ref reader, typeof(Ulid), options);
            return (TIdentifier)Activator.CreateInstance(typeof(TIdentifier), value)!;
        }

        public override void Write(Utf8JsonWriter writer, TIdentifier value, JsonSerializerOptions options)
        {
            var identifier = (Ulid)typeof(TIdentifier).GetProperty(nameof(TenantId.Value))!.GetValue(value)!;
            new UlidJsonConverter().Write(writer, identifier, options);
        }
    }
}
