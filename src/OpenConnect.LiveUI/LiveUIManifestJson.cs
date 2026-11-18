using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenConnect.LiveUI;

/// <summary>Creates the shared JSON settings required to read and write LiveUI manifests.</summary>
public static class LiveUIManifestJson
{
    public static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        options.Converters.Add(new ReadOnlySetConverterFactory());
        return options;
    }

    private sealed class ReadOnlySetConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert) =>
            typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(IReadOnlySet<>);

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
        {
            var elementType = typeToConvert.GetGenericArguments()[0];
            return (JsonConverter)Activator.CreateInstance(typeof(ReadOnlySetConverter<>).MakeGenericType(elementType))!;
        }
    }

    private sealed class ReadOnlySetConverter<T> : JsonConverter<IReadOnlySet<T>> where T : notnull
    {
        public override IReadOnlySet<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            JsonSerializer.Deserialize<HashSet<T>>(ref reader, options) ?? new HashSet<T>();

        public override void Write(Utf8JsonWriter writer, IReadOnlySet<T> value, JsonSerializerOptions options)
        {
            var ordered = value.OrderBy(item => item.ToString(), StringComparer.Ordinal).ToArray();
            JsonSerializer.Serialize(writer, ordered, options);
        }
    }
}
