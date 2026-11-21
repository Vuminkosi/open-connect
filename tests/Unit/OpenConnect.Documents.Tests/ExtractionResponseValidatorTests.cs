using System.Text.Json;
using Xunit;

namespace OpenConnect.Documents.Tests;

public sealed class ExtractionResponseValidatorTests
{
    private const string Schema = """
        {"type":"object","additionalProperties":false,"required":["total","currency"],
         "properties":{"total":{"type":"number","minimum":0},"currency":{"type":"string","enum":["USD","EUR"]}}}
        """;
    private const string Result = """
        {"fields":{"total":{"value":42,"start":9,"end":11},"currency":{"value":"USD","start":12,"end":15}},
         "warnings":["source_support_not_semantic_validation"],"schema_valid":true,"confidence_method":"not_estimated"}
        """;
    private const string Source = "📄 Total: 42 USD";

    [Fact]
    public void Accepts_citations_with_unicode_code_point_offsets()
    {
        using var result = JsonDocument.Parse(Result);
        using var schema = JsonDocument.Parse(Schema);
        ExtractionResponseValidator.Validate(result.RootElement, schema.RootElement, Source);
    }

    [Fact]
    public void Null_optional_constraints_match_worker_semantics()
    {
        const string schemaText = """
            {"type":"object","additionalProperties":false,"required":["total","currency"],
             "properties":{"total":{"type":"number","minimum":null,"maximum":null,"enum":null},
                           "currency":{"type":"string","minLength":null,"maxLength":null,"enum":null}}}
            """;
        using var result = JsonDocument.Parse(Result);
        using var schema = JsonDocument.Parse(schemaText);
        ExtractionResponseValidator.Validate(result.RootElement, schema.RootElement, Source);
    }

    [Theory]
    [InlineData("\"value\":42", "\"value\":43")]
    [InlineData("\"start\":9", "\"start\":0")]
    [InlineData("\"value\":\"USD\"", "\"value\":\"EUR\"")]
    [InlineData("\"schema_valid\":true", "\"schema_valid\":false")]
    [InlineData("\"confidence_method\":\"not_estimated\"", "\"confidence_method\":\"certain\"")]
    public void Rejects_untrusted_worker_values_and_claims(string oldValue, string newValue)
    {
        using var result = JsonDocument.Parse(Result.Replace(oldValue, newValue, StringComparison.Ordinal));
        using var schema = JsonDocument.Parse(Schema);
        var error = Assert.Throws<InvalidOperationException>(() =>
            ExtractionResponseValidator.Validate(result.RootElement, schema.RootElement, Source));
        Assert.DoesNotContain(Source, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_duplicate_field_properties()
    {
        using var result = JsonDocument.Parse(Result.Replace("\"value\":42", "\"value\":42,\"value\":42", StringComparison.Ordinal));
        using var schema = JsonDocument.Parse(Schema);
        Assert.Throws<InvalidOperationException>(() => ExtractionResponseValidator.Validate(result.RootElement, schema.RootElement, Source));
    }
}
