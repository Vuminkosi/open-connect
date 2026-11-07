using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace OpenConnect.Core.Configuration;

/// <summary>Binds OpenConnect settings from key-value configuration entries.</summary>
/// <remarks>
/// A host can pass its configuration without adding a configuration-package dependency to Core:
/// <c>OpenConnectConfigurationBinder.Bind(config.AsEnumerable())</c>.
/// Boolean-looking values are emitted as JSON booleans after trimming (case-insensitive).
/// Consequently, a string option whose entire value is exactly "true" or "false" is
/// interpreted as a boolean; OpenConnectOptions has no such string-valued setting by default.
/// </remarks>
public static class OpenConnectConfigurationBinder
{
    /// <summary>Creates options from key-value entries below the specified section prefix.</summary>
    /// <param name="configurationValues">Configuration keys and their string values.</param>
    /// <param name="sectionPrefix">The section prefix to bind, defaulting to <c>OpenConnect</c>.</param>
    /// <returns>Bound options, or secure defaults when no matching values exist.</returns>
    /// <exception cref="ArgumentNullException">The entries or prefix are null.</exception>
    /// <exception cref="InvalidOperationException">A matching entry cannot be represented or deserialized.</exception>
    public static OpenConnectOptions Bind(IEnumerable<KeyValuePair<string, string?>> configurationValues, string sectionPrefix = "OpenConnect")
    {
        ArgumentNullException.ThrowIfNull(configurationValues);
        ArgumentNullException.ThrowIfNull(sectionPrefix);

        try
        {
            var root = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (key, value) in configurationValues)
            {
                if (key.Equals(sectionPrefix, StringComparison.Ordinal))
                {
                    if (value is not null)
                        throw new InvalidOperationException($"Configuration section '{sectionPrefix}' must contain child keys.");
                    continue;
                }

                var prefix = sectionPrefix + ":";
                if (!key.StartsWith(prefix, StringComparison.Ordinal))
                    continue;

                var segments = key[prefix.Length..].Split(':');
                if (segments.Any(string.IsNullOrEmpty))
                    throw new InvalidOperationException($"Configuration section '{sectionPrefix}' contains an invalid key.");

                var current = root;
                for (var i = 0; i < segments.Length - 1; i++)
                {
                    if (!current.TryGetValue(segments[i], out var child))
                        current[segments[i]] = child = new Dictionary<string, object?>(StringComparer.Ordinal);
                    current = child as Dictionary<string, object?>
                        ?? throw new InvalidOperationException($"Configuration section '{sectionPrefix}' has conflicting keys.");
                }

                if (!current.TryAdd(segments[^1], value))
                    throw new InvalidOperationException($"Configuration section '{sectionPrefix}' contains duplicate keys.");
            }

            var json = ToJsonNode(root).ToJsonString();
            return JsonSerializer.Deserialize<OpenConnectOptions>(json, new JsonSerializerOptions
            {
                PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate,
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
            })
                ?? throw new JsonException("Configuration produced a null options value.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"Configuration section '{sectionPrefix}' contains invalid values.", exception);
        }
    }

    private static JsonNode ToJsonNode(Dictionary<string, object?> values)
    {
        if (values.Count > 0 && values.Keys.All(key => int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out _)) &&
            values.Keys.Select(key => int.Parse(key, CultureInfo.InvariantCulture)).Order().SequenceEqual(Enumerable.Range(0, values.Count)))
            return new JsonArray(values.OrderBy(pair => int.Parse(pair.Key, CultureInfo.InvariantCulture)).Select(pair => ToJsonValue(pair.Value)).ToArray());

        var result = new JsonObject();
        foreach (var (key, value) in values)
            result[key] = ToJsonValue(value);
        return result;
    }

    private static JsonNode? ToJsonValue(object? value) => value switch
    {
        Dictionary<string, object?> children => ToJsonNode(children),
        string text => ToJsonScalar(text),
        null => null,
        _ => throw new InvalidOperationException("Unexpected configuration value.")
    };

    private static JsonNode ToJsonScalar(string text)
    {
        var trimmed = text.Trim();
        if (bool.TryParse(trimmed, out var boolean))
            return JsonValue.Create(boolean)!;

        // Preserve configuration numbers as JSON numbers. Besides binding numeric options,
        // this ensures strict enum converters reject numeric enum values rather than seeing strings.
        if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) ||
            decimal.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            return JsonNode.Parse(trimmed)!;

        return JsonValue.Create(text)!;
    }
}
