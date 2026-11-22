using System.Globalization;
using System.Text;
using System.Text.Json;

namespace OpenConnect.Documents;

/// <summary>Checks extraction output against the original text and the requested flat schema.</summary>
internal static class ExtractionResponseValidator
{
    internal static void Validate(JsonElement result, JsonElement schema, string source)
    {
        try { ValidateCore(result, schema, source); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        { throw new InvalidOperationException("The extraction worker result is invalid."); }
    }

    private static void ValidateCore(JsonElement result, JsonElement schema, string source)
    {
        var resultProperties = Object(result, ["fields", "warnings", "schema_valid", "confidence_method"]);
        if (resultProperties["confidence_method"].GetString() != "not_estimated") Invalid();
        var warnings = resultProperties["warnings"];
        if (warnings.ValueKind != JsonValueKind.Array || warnings.GetArrayLength() > 32) Invalid();
        var warningSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in warnings.EnumerateArray())
        {
            var warning = item.GetString();
            if (warning is not ("source_support_not_semantic_validation" or "required_field_missing" or
                "instruction_like_line_excluded" or "subtotal_plus_tax_differs_from_total") || !warningSet.Add(warning)) Invalid();
        }
        if (!warningSet.Contains("source_support_not_semantic_validation")) Invalid();
        var schemaProperties = schema.GetProperty("properties");
        if (schemaProperties.ValueKind != JsonValueKind.Object) Invalid();
        var definitions = schemaProperties.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
        if (definitions.Count is < 1 or > 32) Invalid();
        var required = schema.TryGetProperty("required", out var requiredElement)
            ? requiredElement.EnumerateArray().Select(item => item.GetString()!).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        var fieldProperties = Object(resultProperties["fields"], definitions.Keys.ToArray());
        var offsets = new List<int> { 0 };
        var offset = 0;
        foreach (var rune in source.EnumerateRunes()) { offset += rune.Utf16SequenceLength; offsets.Add(offset); }
        var missingRequired = false;
        var schemaValid = true;
        foreach (var (name, definition) in definitions)
        {
            var field = Object(fieldProperties[name], ["value", "start", "end"]);
            var value = field["value"];
            var type = definition.GetProperty("type");
            var types = type.ValueKind == JsonValueKind.String ? [type.GetString()] :
                type.EnumerateArray().Select(element => element.GetString()).ToArray();
            var nullable = types.Contains("null", StringComparer.Ordinal);
            var kind = types.Single(item => item != "null");
            if (value.ValueKind == JsonValueKind.Null)
            {
                if (field["start"].ValueKind != JsonValueKind.Null || field["end"].ValueKind != JsonValueKind.Null) Invalid();
                missingRequired |= required.Contains(name);
                if (!nullable || definition.TryGetProperty("enum", out var choices) && choices.ValueKind != JsonValueKind.Null &&
                    !choices.EnumerateArray().Any(choice => choice.ValueKind == JsonValueKind.Null)) schemaValid = false;
                continue;
            }
            if (!field["start"].TryGetInt32(out _) || !field["end"].TryGetInt32(out _)) Invalid();
            var start = field["start"].GetInt32();
            var end = field["end"].GetInt32();
            if (start < 0 || end <= start || end >= offsets.Count) Invalid();
            var cited = source[offsets[start]..offsets[end]];
            if (!Matches(kind, value, cited)) Invalid();
            if (kind == "string")
            {
                var length = cited.EnumerateRunes().Count();
                if (definition.TryGetProperty("minLength", out var min) && min.ValueKind != JsonValueKind.Null && length < min.GetInt32() ||
                    definition.TryGetProperty("maxLength", out var max) && max.ValueKind != JsonValueKind.Null && length > max.GetInt32()) Invalid();
            }
            if (kind is "number" or "integer")
            {
                var number = value.GetDecimal();
                if (definition.TryGetProperty("minimum", out var min) && min.ValueKind != JsonValueKind.Null && number < min.GetDecimal() ||
                    definition.TryGetProperty("maximum", out var max) && max.ValueKind != JsonValueKind.Null && number > max.GetDecimal()) Invalid();
            }
            if (definition.TryGetProperty("enum", out var values) && values.ValueKind != JsonValueKind.Null &&
                !values.EnumerateArray().Any(choice => Equal(choice, value))) Invalid();
        }
        if (resultProperties["schema_valid"].GetBoolean() != schemaValid ||
            warningSet.Contains("required_field_missing") != missingRequired) Invalid();
    }

    private static bool Matches(string? kind, JsonElement value, string cited) => kind switch
    {
        "string" => value.ValueKind == JsonValueKind.String && value.GetString() == cited,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False &&
            string.Equals(cited, value.GetBoolean() ? "true" : "false", StringComparison.OrdinalIgnoreCase),
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var integer) &&
            decimal.Truncate(integer) == integer && decimal.TryParse(cited, NumberStyles.Float, CultureInfo.InvariantCulture, out var sourceInteger) && integer == sourceInteger,
        "number" => value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) &&
            decimal.TryParse(cited, NumberStyles.Float, CultureInfo.InvariantCulture, out var sourceNumber) && number == sourceNumber,
        _ => false
    };

    private static bool Equal(JsonElement a, JsonElement b) => a.ValueKind == b.ValueKind && a.ValueKind switch
    {
        JsonValueKind.String => a.GetString() == b.GetString(),
        JsonValueKind.Number => a.TryGetDecimal(out var left) && b.TryGetDecimal(out var right) && left == right,
        JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => true,
        _ => false
    };

    private static Dictionary<string, JsonElement> Object(JsonElement element, string[] expected)
    {
        if (element.ValueKind != JsonValueKind.Object) Invalid();
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!expected.Contains(property.Name, StringComparer.Ordinal) || !result.TryAdd(property.Name, property.Value)) Invalid();
        if (result.Count != expected.Length) Invalid();
        return result;
    }

    private static void Invalid() => throw new InvalidOperationException();
}
