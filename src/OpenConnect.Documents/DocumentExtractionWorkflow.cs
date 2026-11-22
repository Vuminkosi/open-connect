using System.Text;
using System.Text.Json;
using OpenConnect.Privacy;

namespace OpenConnect.Documents;

/// <summary>Page-relative Unicode code-point citation for an extracted field.</summary>
public sealed class DocumentFieldCitation
{
    internal DocumentFieldCitation(int pageIndex, int start, int end) { PageIndex = pageIndex; Start = start; End = end; }
    /// <summary>Gets the zero-based page index.</summary>
    public int PageIndex { get; }
    /// <summary>Gets the inclusive code-point offset in the page.</summary>
    public int Start { get; }
    /// <summary>Gets the exclusive code-point offset in the page.</summary>
    public int End { get; }
}

/// <summary>Owned OCR and extraction results and their page-relative citations.</summary>
public sealed class DocumentExtractionResult : IDisposable
{
    internal DocumentExtractionResult(JsonDocument ocr, JsonDocument extraction,
        IReadOnlyDictionary<string, DocumentFieldCitation?> citations)
    {
        Ocr = ocr;
        Extraction = extraction;
        Citations = citations;
    }

    /// <summary>Gets OCR evidence; the document is owned by this result.</summary>
    public JsonDocument Ocr { get; }
    /// <summary>Gets extraction evidence; the document is owned by this result.</summary>
    public JsonDocument Extraction { get; }
    /// <summary>Gets a page-relative citation for each field, or null for an absent value.</summary>
    public IReadOnlyDictionary<string, DocumentFieldCitation?> Citations { get; }
    /// <summary>Disposes the owned JSON documents.</summary>
    public void Dispose() { Ocr.Dispose(); Extraction.Dispose(); }
}

/// <summary>Runs authorized OCR followed by authorized structured extraction.</summary>
public sealed class DocumentExtractionWorkflow(DocumentWorkerClient documents, ExtractionWorkerClient extraction)
{
    private readonly DocumentWorkerClient _documents = documents ?? throw new ArgumentNullException(nameof(documents));
    private readonly ExtractionWorkerClient _extraction = extraction ?? throw new ArgumentNullException(nameof(extraction));

    /// <summary>
    /// Processes a document and maps extraction citations to OCR pages. Both clients apply their own
    /// host authorization and privacy policies. The caller owns the returned result.
    /// </summary>
    public async Task<DocumentExtractionResult> ExecuteAsync(EgressContext context, ReadOnlyMemory<byte> content,
        JsonElement schema, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (schema.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Invalid extraction schema.", nameof(schema));
        var stableSchema = schema.Clone();
        var ocr = await _documents.ExecuteAsync(context, "documents.ocr", content, cancellationToken).ConfigureAwait(false);
        JsonDocument? extracted = null;
        try
        {
            var pages = ocr.RootElement.GetProperty("result").GetProperty("pages").EnumerateArray()
                .Select(page => page.GetProperty("text").GetString()!).ToArray();
            var offsets = new (int Start, int End)[pages.Length];
            var cursor = 0;
            for (var index = 0; index < pages.Length; index++)
            {
                var length = pages[index].EnumerateRunes().Count();
                offsets[index] = (cursor, checked(cursor + length));
                cursor = checked(cursor + length + (index + 1 < pages.Length ? 1 : 0));
            }
            var source = string.Join('\n', pages);
            extracted = await _extraction.ExtractAsync(context, source, stableSchema, cancellationToken).ConfigureAwait(false);
            var citations = new Dictionary<string, DocumentFieldCitation?>(StringComparer.Ordinal);
            foreach (var field in extracted.RootElement.GetProperty("result").GetProperty("fields").EnumerateObject())
            {
                var startElement = field.Value.GetProperty("start");
                if (startElement.ValueKind == JsonValueKind.Null)
                { citations.Add(field.Name, null); continue; }
                var start = startElement.GetInt32();
                var end = field.Value.GetProperty("end").GetInt32();
                var pageIndex = Array.FindIndex(offsets, page => start >= page.Start && end <= page.End && end > start);
                if (pageIndex < 0)
                    throw new InvalidOperationException("The extraction citation crosses a page boundary.");
                citations.Add(field.Name, new DocumentFieldCitation(pageIndex, start - offsets[pageIndex].Start,
                    end - offsets[pageIndex].Start));
            }
            return new DocumentExtractionResult(ocr, extracted,
                new System.Collections.ObjectModel.ReadOnlyDictionary<string, DocumentFieldCitation?>(citations));
        }
        catch { extracted?.Dispose(); ocr.Dispose(); throw; }
    }
}
