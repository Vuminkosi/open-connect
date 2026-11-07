using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace OpenConnect.Navigator.AspNetCore.Tests;

public sealed class SchemaValidationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("session.json", "{\"sessionId\":\"01ARZ3NDEKTSV4RRFFQ69G5FAV\",\"tenantId\":\"tenant\",\"principalId\":\"principal\",\"state\":\"created\"}")]
    [InlineData("command.json", "{\"commandId\":\"cmd-1\",\"sessionId\":\"session-1\",\"principalId\":\"principal\",\"tenantId\":\"tenant\",\"targetId\":\"target-1\",\"action\":\"invoke-registered-action\",\"argumentsHash\":\"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\",\"expiresAt\":\"2026-11-07T12:00:00Z\"}")]
    [InlineData("receipt.json", "{\"receiptId\":\"receipt-1\",\"commandId\":\"cmd-1\",\"sessionId\":\"session-1\",\"principalId\":\"principal\",\"tenantId\":\"tenant\",\"argumentsHash\":\"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\",\"outcome\":\"succeeded\",\"issuedAt\":\"2026-11-07T12:00:00Z\"}")]
    [InlineData("tour.json", "{\"tourId\":\"tour-1\",\"version\":1,\"routeId\":\"home\",\"steps\":[{\"type\":\"navigate\",\"action\":\"observe\",\"targetId\":\"target-1\",\"timeout\":\"PT2S\"}]}")]
    public void RuntimeResponseShapesSatisfyContractSchema(string schemaFile, string response)
    {
        var schema = JsonNode.Parse(File.ReadAllText(ContractPath(schemaFile)))!;
        var value = JsonNode.Parse(response)!;
        Assert.Empty(Validate(value, schema, "$"));
    }

    [Theory]
    // NAV-AT-019: schema enum validation rejects values outside the contract.
    [InlineData("session.json", "{\"sessionId\":\"s\",\"tenantId\":\"t\",\"principalId\":\"p\",\"state\":\"not-a-state\"}")]
    [InlineData("command.json", "{\"commandId\":\"c\",\"sessionId\":\"s\",\"principalId\":\"p\",\"tenantId\":\"t\",\"targetId\":\"x\",\"action\":\"not-an-action\",\"argumentsHash\":\"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\",\"expiresAt\":\"2026-11-07T12:00:00Z\"}")]
    [InlineData("receipt.json", "{\"receiptId\":\"r\",\"commandId\":\"c\",\"sessionId\":\"s\",\"principalId\":\"p\",\"tenantId\":\"t\",\"argumentsHash\":\"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\",\"outcome\":\"uncertain\",\"issuedAt\":\"2026-11-07T12:00:00Z\"}")]
    [InlineData("tour.json", "{\"tourId\":\"t\",\"version\":1,\"routeId\":\"home\",\"steps\":[{\"type\":\"not-a-step\"}]}")]
    public void SchemaRejectsInvalidEnumValues(string schemaFile, string response)
    {
        var schema = JsonNode.Parse(File.ReadAllText(ContractPath(schemaFile)))!;
        var value = JsonNode.Parse(response)!;
        Assert.NotEmpty(Validate(value, schema, "$"));
    }

    private static string ContractPath(string file)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "contracts", "navigator", "v1", file)))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "contracts", "navigator", "v1", file);
    }

    private static List<string> Validate(JsonNode? value, JsonNode schema, string path)
    {
        var errors = new List<string>();
        var type = (string?)schema["type"];
        var matchesType = type switch
        {
            "object" => value is JsonObject,
            "array" => value is JsonArray,
            "string" => value is JsonValue node && node.TryGetValue<string>(out _),
            "integer" => value is JsonValue node && node.TryGetValue<int>(out _),
            "boolean" => value is JsonValue node && node.TryGetValue<bool>(out _),
            _ => true
        };
        if (!matchesType) return [$"{path}: expected {type}"];

        if (schema["enum"] is JsonArray allowed && !allowed.Any(item => JsonNode.DeepEquals(item, value)))
            errors.Add($"{path}: value is outside enum");
        if (value is JsonObject obj)
        {
            if (schema["required"] is JsonArray required)
                foreach (var key in required.Select(item => item!.GetValue<string>()))
                    if (!obj.ContainsKey(key)) errors.Add($"{path}.{key}: required");
            var properties = schema["properties"] as JsonObject;
            foreach (var (key, child) in obj)
            {
                if (properties?[key] is JsonNode childSchema) errors.AddRange(Validate(child, childSchema, $"{path}.{key}"));
                else if ((bool?)schema["additionalProperties"] == false) errors.Add($"{path}.{key}: additional property");
            }
        }
        if (value is JsonArray array && schema["items"] is JsonNode itemSchema)
            for (var index = 0; index < array.Count; index++) errors.AddRange(Validate(array[index], itemSchema, $"{path}[{index}]"));
        if (value is JsonValue stringValue && stringValue.TryGetValue<string>(out var text))
        {
            if ((int?)schema["minLength"] is int minLength && text.Length < minLength) errors.Add($"{path}: too short");
            if ((string?)schema["pattern"] is string pattern && !System.Text.RegularExpressions.Regex.IsMatch(text, pattern)) errors.Add($"{path}: pattern mismatch");
            if ((string?)schema["format"] == "date-time" && !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _)) errors.Add($"{path}: invalid date-time");
        }
        if (value is JsonValue integerValue && integerValue.TryGetValue<int>(out var number) && (int?)schema["minimum"] is int minimum && number < minimum)
            errors.Add($"{path}: below minimum");
        return errors;
    }
}
