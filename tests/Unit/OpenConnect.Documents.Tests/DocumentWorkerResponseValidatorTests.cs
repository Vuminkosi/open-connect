using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace OpenConnect.Documents.Tests;

public sealed class DocumentWorkerResponseValidatorTests
{
    private const string Valid = """
        {"pages":[{"text":"名😀: 42","width":100,"height":50,"model_id":"model",
          "model_fingerprint":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","language":"eng",
          "spans":[{"text":"名😀:","start":0,"end":3,"box":[0,0,0.5,1],"confidence":0.9},
                   {"text":"42","start":4,"end":6,"box":[0.5,0,1,1],"confidence":0.7}]}],
         "fields":[{"page_index":0,"label":"名😀","value":"42","label_start":0,"label_end":2,
          "value_start":4,"value_end":6,"label_span_indexes":[0],"value_span_indexes":[1],
          "minimum_ocr_confidence":0.7}],"warnings":[]}
        """;

    [Fact]
    public void AcceptsUnicodeCodePointOffsetsAndValidCitations()
    {
        using var document = JsonDocument.Parse(Valid);
        DocumentWorkerResponseValidator.Validate(document.RootElement, "documents.fields");
    }

    [Fact]
    public void RejectsMixedOcrModelProvenanceAcrossPages()
    {
        var pages = JsonNode.Parse(Valid)!["pages"]!.AsArray();
        pages.Add(pages[0]!.DeepClone());
        var result = new JsonObject { ["pages"] = pages.DeepClone() };
        using (var valid = JsonDocument.Parse(result.ToJsonString()))
            DocumentWorkerResponseValidator.Validate(valid.RootElement, "documents.ocr");
        result["pages"]![1]!["model_fingerprint"] = new string('b', 64);
        using var invalid = JsonDocument.Parse(result.ToJsonString());
        Assert.Throws<InvalidOperationException>(() => DocumentWorkerResponseValidator.Validate(invalid.RootElement, "documents.ocr"));
    }

    [Fact]
    public void AcceptsTheSharedPythonResponseFixture()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "contracts", "fixtures")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var fixture = File.ReadAllText(Path.Combine(directory.FullName, "contracts", "fixtures", "valid", "document-fields-response.json"));
        using var document = JsonDocument.Parse(fixture);
        DocumentWorkerResponseValidator.Validate(document.RootElement.GetProperty("result"), "documents.fields");
    }

    [Fact]
    public void AcceptsTheSharedPythonLayoutFixture()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "contracts", "fixtures")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var fixture = File.ReadAllText(Path.Combine(directory.FullName, "contracts", "fixtures", "valid", "document-layout-response.json"));
        using var document = JsonDocument.Parse(fixture);
        DocumentWorkerResponseValidator.Validate(document.RootElement.GetProperty("result"), "documents.layout");
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("failed_page_citation")]
    [InlineData("page_order")]
    [InlineData("misleading_status")]
    [InlineData("missing_warning")]
    public void ValidatesPartialAnalysisFixture(string mutation)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "contracts", "fixtures")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var fixture = File.ReadAllText(Path.Combine(directory.FullName, "contracts", "fixtures", "valid", "document-analyze-response.json"));
        var result = JsonNode.Parse(fixture)!["result"]!;
        switch (mutation)
        {
            case "failed_page_citation":
                result["tables"]!["items"]![0]!["page_index"] = 0;
                break;
            case "page_order":
                result["pages"]![1]!["page_index"] = 0;
                break;
            case "misleading_status":
                result["status"] = "completed";
                break;
            case "missing_warning":
                result["warnings"] = new JsonArray("heuristic_candidates_require_review");
                break;
        }
        using var document = JsonDocument.Parse(result.ToJsonString());
        if (mutation == "valid")
            DocumentWorkerResponseValidator.Validate(document.RootElement, "documents.analyze");
        else
            Assert.Throws<InvalidOperationException>(() => DocumentWorkerResponseValidator.Validate(document.RootElement, "documents.analyze"));
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("offset")]
    [InlineData("box")]
    [InlineData("cell_order")]
    [InlineData("missing_warning")]
    [InlineData("duplicate_span")]
    [InlineData("misaligned_column")]
    [InlineData("distant_row")]
    public void ValidatesTableCandidateCitationsFixture(string mutation)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "contracts", "fixtures")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var fixture = File.ReadAllText(Path.Combine(directory.FullName, "contracts", "fixtures", "valid", "document-tables-response.json"));
        var result = JsonNode.Parse(fixture)!["result"]!;
        var cells = result["tables"]![0]!["cells"]!;
        switch (mutation)
        {
            case "offset":
                cells[0]!["start"] = 1;
                break;
            case "box":
                cells[0]!["box"]![0] = 0;
                break;
            case "cell_order":
                cells[0]!["column"] = 1;
                break;
            case "missing_warning":
                result["warnings"] = new JsonArray();
                break;
            case "duplicate_span":
                cells[1]!["span_indexes"]![0] = 0;
                break;
            case "misaligned_column":
                result["pages"]![0]!["spans"]![2]!["box"]![0] = 0.3;
                result["pages"]![0]!["spans"]![2]!["box"]![2] = 0.4;
                cells[2]!["box"]![0] = 0.3;
                cells[2]!["box"]![2] = 0.4;
                break;
            case "distant_row":
                for (var i = 2; i < 4; i++)
                {
                    result["pages"]![0]!["spans"]![i]!["box"]![1] = 0.6;
                    result["pages"]![0]!["spans"]![i]!["box"]![3] = 0.7;
                    cells[i]!["box"]![1] = 0.6;
                    cells[i]!["box"]![3] = 0.7;
                }
                break;
        }
        using var document = JsonDocument.Parse(result.ToJsonString());
        if (mutation == "valid")
            DocumentWorkerResponseValidator.Validate(document.RootElement, "documents.tables");
        else
            Assert.Throws<InvalidOperationException>(() => DocumentWorkerResponseValidator.Validate(document.RootElement, "documents.tables"));
    }

    [Theory]
    [InlineData("wrong_text")]
    [InlineData("wrong_offset")]
    [InlineData("wrong_reference")]
    [InlineData("wrong_confidence")]
    [InlineData("invalid_box")]
    [InlineData("extra_property")]
    [InlineData("unknown_warning")]
    [InlineData("missing_citation")]
    [InlineData("uncited_page_text")]
    public void RejectsInvalidResultsAndUnreliableCitations(string mutation)
    {
        var root = JsonNode.Parse(Valid)!;
        var page = root["pages"]![0]!;
        var field = root["fields"]![0]!;
        switch (mutation)
        {
            case "wrong_text":
                field["value"] = "43";
                break;
            case "wrong_offset":
                field["label_end"] = 3;
                break;
            case "wrong_reference":
                field["value_span_indexes"]![0] = 0;
                break;
            case "wrong_confidence":
                field["minimum_ocr_confidence"] = 0.9;
                break;
            case "invalid_box":
                page["spans"]![0]!["box"]![2] = 0;
                break;
            case "extra_property":
                page["extra"] = true;
                break;
            case "unknown_warning":
                root["warnings"]!.AsArray().Add("other");
                break;
            case "missing_citation":
                page["text"] = "名😀: 42 USD";
                page["spans"]!.AsArray().Add(JsonNode.Parse("""
                    {"text":"USD","start":7,"end":10,"box":[0.5,0,1,1],"confidence":0.2}
                    """));
                field["value"] = "42 USD";
                field["value_end"] = 10;
                break;
            case "uncited_page_text":
                page["text"] = "名😀: 42 X";
                break;
        }
        using var document = JsonDocument.Parse(root.ToJsonString());
        Assert.Throws<InvalidOperationException>(() =>
            DocumentWorkerResponseValidator.Validate(document.RootElement, "documents.fields"));
    }

    [Fact]
    public void RejectsDuplicateNestedProperties()
    {
        using var document = JsonDocument.Parse(Valid.Replace("\"width\":100", "\"width\":100,\"width\":100"));
        Assert.Throws<InvalidOperationException>(() =>
            DocumentWorkerResponseValidator.Validate(document.RootElement, "documents.fields"));
    }
}
