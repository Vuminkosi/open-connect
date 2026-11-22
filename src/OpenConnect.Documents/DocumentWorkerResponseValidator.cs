using System.Text;
using System.Text.Json;

namespace OpenConnect.Documents;

/// <summary>Checks the worker result and the integrity of its source citations.</summary>
internal static class DocumentWorkerResponseValidator
{
    private sealed record Span(int Start, int End, double Confidence, double[] Box);
    private sealed record Page(string Text, int[] Offsets, IReadOnlyList<Span> Spans, string? ModelId, string? ModelFingerprint);

    internal static void Validate(JsonElement result, string kind)
    {
        if (kind == "documents.analyze") { ValidateAnalysis(result); return; }
        Properties(result, kind == "documents.fields" ? ["pages", "fields", "warnings"] :
            kind == "documents.layout" ? ["pages", "lines", "warnings"] :
            kind == "documents.tables" ? ["pages", "tables", "warnings"] : ["pages"]);
        var pagesElement = result.GetProperty("pages");
        Array(pagesElement, 1, 30);
        var pages = new List<Page>();
        foreach (var item in pagesElement.EnumerateArray()) pages.Add(ParsePage(item));
        EnsureSameProvenance(pages);
        if (kind == "documents.layout") { ValidateLines(result, pages); return; }
        if (kind == "documents.tables") { ValidateTables(result, pages); return; }
        if (kind != "documents.fields") return;
        ValidateFields(result, pages);
    }

    private static Page ParsePage(JsonElement item)
    {
            Properties(item, ["text", "width", "height", "spans", "model_id", "model_fingerprint", "language"]);
            var text = String(item.GetProperty("text"));
            PositiveInt(item.GetProperty("width"));
            PositiveInt(item.GetProperty("height"));
            var modelId = String(item.GetProperty("model_id"));
            if (modelId.Length == 0 || String(item.GetProperty("language")) != "eng")
                Invalid();
            var fingerprint = String(item.GetProperty("model_fingerprint"));
            if (fingerprint.Length != 64 || fingerprint.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
                Invalid();
            var offsets = CodePointOffsets(text);
            var spanElements = item.GetProperty("spans");
            Array(spanElements, 0, 50000);
            var spans = new List<Span>();
            var previousEnd = 0;
            foreach (var span in spanElements.EnumerateArray())
            {
                Properties(span, ["text", "start", "end", "box", "confidence"]);
                var start = NonnegativeInt(span.GetProperty("start"));
                var end = PositiveInt(span.GetProperty("end"));
                var word = String(span.GetProperty("text"));
                if (start < previousEnd || start >= end || end >= offsets.Length || word.Length == 0 ||
                    !text.AsSpan(offsets[start], offsets[end] - offsets[start]).SequenceEqual(word))
                    Invalid();
                if (spans.Count == 0 ? start != 0 :
                    start != previousEnd + 1 || text[offsets[previousEnd]] is not (' ' or '\n'))
                    Invalid();
                var box = span.GetProperty("box");
                Array(box, 4, 4);
                var coordinates = box.EnumerateArray().Select(UnitNumber).ToArray();
                if (coordinates[0] >= coordinates[2] || coordinates[1] >= coordinates[3]) Invalid();
                spans.Add(new Span(start, end, UnitNumber(span.GetProperty("confidence")), coordinates));
                previousEnd = end;
            }
            if (previousEnd != offsets.Length - 1) Invalid();
            return new Page(text, offsets, spans, modelId, fingerprint);
    }

    private static void EnsureSameProvenance(IReadOnlyList<Page> pages)
    {
        var first = pages.FirstOrDefault(page => page.ModelId is not null);
        if (first is not null && pages.Any(page => page.ModelId is not null &&
            (page.ModelId != first.ModelId || page.ModelFingerprint != first.ModelFingerprint))) Invalid();
    }

    private static void ValidateAnalysis(JsonElement result)
    {
        Properties(result, ["pages", "layout", "tables", "fields", "status", "warnings"]);
        var rawPages = result.GetProperty("pages");
        Array(rawPages, 1, 30);
        var pages = new List<Page>();
        var failed = false;
        var completedPages = 0;
        foreach (var entry in rawPages.EnumerateArray())
        {
            Properties(entry, ["page_index", "status", "page", "reason"]);
            if (NonnegativeInt(entry.GetProperty("page_index")) != pages.Count) Invalid();
            var status = String(entry.GetProperty("status"));
            var page = entry.GetProperty("page");
            var reason = entry.GetProperty("reason");
            if (status == "completed")
            {
                if (reason.ValueKind != JsonValueKind.Null) Invalid();
                pages.Add(ParsePage(page));
                completedPages++;
            }
            else if (status == "failed")
            {
                if (page.ValueKind != JsonValueKind.Null || String(reason) is not ("page_unavailable" or "ocr_unavailable" or "deadline_exceeded")) Invalid();
                failed = true;
                pages.Add(new Page("", [0], [], null, null));
            }
            else Invalid();
        }
        EnsureSameProvenance(pages);
        var incomplete = false;
        foreach (var (name, itemName) in new[] { ("layout", "lines"), ("tables", "tables"), ("fields", "fields") })
        {
            var component = result.GetProperty(name);
            Properties(component, ["status", "items", "warnings"]);
            var status = String(component.GetProperty("status"));
            if (completedPages == 0 && status != "unavailable") Invalid();
            var items = component.GetProperty("items");
            var warnings = component.GetProperty("warnings");
            Array(items, 0, name == "layout" ? 50000 : name == "tables" ? 100 : 1000);
            Array(warnings, 0, 8);
            var codes = warnings.EnumerateArray().Select(String).ToArray();
            if (codes.Distinct(StringComparer.Ordinal).Count() != codes.Length) Invalid();
            if (status == "unavailable")
            {
                if (items.GetArrayLength() != 0 || codes.Length != 1 || codes[0] is not ("component_unavailable" or "no_completed_pages")) Invalid();
                if ((codes[0] == "no_completed_pages") != (completedPages == 0)) Invalid();
                incomplete = true;
                continue;
            }
            if (status is not ("completed" or "partial")) Invalid();
            var partial = codes.Any(code => code != "heuristic_tables_require_review");
            if ((status == "partial") != partial) Invalid();
            incomplete |= partial;
            var synthetic = JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement> { [itemName] = items, ["warnings"] = warnings });
            if (name == "layout") ValidateLines(synthetic, pages);
            else if (name == "tables") ValidateTables(synthetic, pages);
            else ValidateFields(synthetic, pages);
        }
        var overall = String(result.GetProperty("status"));
        if (overall != (failed || incomplete ? "partial" : "completed")) Invalid();
        var overallWarnings = result.GetProperty("warnings");
        Array(overallWarnings, 1, 3);
        var actual = overallWarnings.EnumerateArray().Select(String).ToArray();
        var expected = new List<string> { "heuristic_candidates_require_review" };
        if (incomplete && completedPages > 0) expected.Add("component_results_incomplete");
        if (failed) expected.Add("page_results_incomplete");
        if (actual.Length != expected.Count || !actual.ToHashSet(StringComparer.Ordinal).SetEquals(expected)) Invalid();
    }

    private static void ValidateFields(JsonElement result, IReadOnlyList<Page> pages)
    {
        var fields = result.GetProperty("fields");
        Array(fields, 0, 1000);
        foreach (var field in fields.EnumerateArray())
        {
            Properties(field, ["page_index", "label", "value", "label_start", "label_end", "value_start",
                "value_end", "label_span_indexes", "value_span_indexes", "minimum_ocr_confidence"]);
            var pageIndex = NonnegativeInt(field.GetProperty("page_index"));
            if (pageIndex >= pages.Count) Invalid();
            var page = pages[pageIndex];
            var labelStart = NonnegativeInt(field.GetProperty("label_start"));
            var labelEnd = PositiveInt(field.GetProperty("label_end"));
            var valueStart = NonnegativeInt(field.GetProperty("value_start"));
            var valueEnd = PositiveInt(field.GetProperty("value_end"));
            var label = String(field.GetProperty("label"));
            var value = String(field.GetProperty("value"));
            if (labelStart >= labelEnd || labelEnd >= valueStart || valueStart >= valueEnd ||
                valueEnd >= page.Offsets.Length || labelEnd - labelStart > 128 || valueEnd - valueStart > 1024 ||
                !page.Text.AsSpan(page.Offsets[labelStart], page.Offsets[labelEnd] - page.Offsets[labelStart]).SequenceEqual(label) ||
                !page.Text.AsSpan(page.Offsets[valueStart], page.Offsets[valueEnd] - page.Offsets[valueStart]).SequenceEqual(value) ||
                !page.Text.AsSpan(page.Offsets[labelEnd], page.Offsets[valueStart] - page.Offsets[labelEnd]).Trim().SequenceEqual(":"))
                Invalid();
            var labelConfidence = ValidateReferences(field.GetProperty("label_span_indexes"), page, labelStart, labelEnd);
            var valueConfidence = ValidateReferences(field.GetProperty("value_span_indexes"), page, valueStart, valueEnd);
            if (UnitNumber(field.GetProperty("minimum_ocr_confidence")) != Math.Min(labelConfidence, valueConfidence))
                Invalid();
        }
        var warnings = result.GetProperty("warnings");
        Array(warnings, 0, 2);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var warning in warnings.EnumerateArray())
        {
            var value = String(warning);
            if (value is not ("field_candidates_skipped" or "field_limit_reached") || !seen.Add(value)) Invalid();
        }
    }

    private static void ValidateTables(JsonElement result, IReadOnlyList<Page> pages)
    {
        var tables = result.GetProperty("tables");
        Array(tables, 0, 100);
        var used = new HashSet<(int Page, int Span)>();
        var lastSpan = Enumerable.Repeat(-1, pages.Count).ToArray();
        var lastPage = 0;
        var total = 0;
        foreach (var table in tables.EnumerateArray())
        {
            Properties(table, ["page_index", "rows", "columns", "cells"]);
            var pageIndex = NonnegativeInt(table.GetProperty("page_index"));
            var rows = PositiveInt(table.GetProperty("rows"));
            var columns = PositiveInt(table.GetProperty("columns"));
            if (pageIndex >= pages.Count || pageIndex < lastPage || rows < 2 || columns is < 2 or > 20 || (long)rows * columns > 10000) Invalid();
            lastPage = pageIndex;
            var page = pages[pageIndex];
            var cells = table.GetProperty("cells");
            Array(cells, 1, 10000);
            if (cells.GetArrayLength() != rows * columns || (total += cells.GetArrayLength()) > 10000) Invalid();
            var position = 0;
            var previousTableSpan = -1;
            var columnStarts = new double[columns];
            double rowTop = 0, rowBottom = 0, priorTop = 0, priorBottom = 0;
            foreach (var cell in cells.EnumerateArray())
            {
                Properties(cell, ["row", "column", "start", "end", "span_indexes", "box", "minimum_ocr_confidence"]);
                var row = position / columns;
                var column = position % columns;
                if (NonnegativeInt(cell.GetProperty("row")) != row ||
                    NonnegativeInt(cell.GetProperty("column")) != column) Invalid();
                position++;
                var start = NonnegativeInt(cell.GetProperty("start"));
                var end = PositiveInt(cell.GetProperty("end"));
                var refs = cell.GetProperty("span_indexes");
                var confidence = ValidateReferences(refs, page, start, end);
                var selected = refs.EnumerateArray().Select(item => NonnegativeInt(item)).ToArray();
                if (page.Spans[selected[0]].Start != start || page.Spans[selected[^1]].End != end) Invalid();
                for (var i = 1; i < selected.Length; i++)
                {
                    var previous = page.Spans[selected[i - 1]];
                    var current = page.Spans[selected[i]];
                    if (page.Text.AsSpan(page.Offsets[previous.End], page.Offsets[current.Start] - page.Offsets[previous.End]).Contains('\n') ||
                        current.Box[0] < previous.Box[0] || current.Box[0] - previous.Box[2] >= 0.04) Invalid();
                }
                var first = page.Spans[selected[0]];
                if (column == 0 && selected[0] > 0)
                {
                    var previous = page.Spans[selected[0] - 1];
                    if (!page.Text.AsSpan(page.Offsets[previous.End], page.Offsets[first.Start] - page.Offsets[previous.End]).SequenceEqual("\n")) Invalid();
                }
                if (previousTableSpan >= 0)
                {
                    if (selected[0] != previousTableSpan + 1) Invalid();
                    var previous = page.Spans[previousTableSpan];
                    var separator = page.Text.AsSpan(page.Offsets[previous.End], page.Offsets[first.Start] - page.Offsets[previous.End]);
                    if (!separator.SequenceEqual(column == 0 ? "\n" : " ")) Invalid();
                    if (column > 0 && (first.Box[0] < previous.Box[0] || first.Box[0] - previous.Box[2] < 0.04)) Invalid();
                }
                if (row == 0) columnStarts[column] = first.Box[0];
                else if (Math.Abs(first.Box[0] - columnStarts[column]) > 0.025) Invalid();
                if (column == 0) { rowTop = first.Box[1]; rowBottom = first.Box[3]; }
                foreach (var index in selected)
                {
                    rowTop = Math.Min(rowTop, page.Spans[index].Box[1]);
                    rowBottom = Math.Max(rowBottom, page.Spans[index].Box[3]);
                }
                if (column == columns - 1)
                {
                    var last = page.Spans[selected[^1]];
                    if (selected[^1] + 1 < page.Spans.Count &&
                        !page.Text.AsSpan(page.Offsets[last.End], page.Offsets[page.Spans[selected[^1] + 1].Start] - page.Offsets[last.End]).SequenceEqual("\n")) Invalid();
                    if (row > 0 && (rowTop < priorTop || rowTop - priorBottom > 0.08)) Invalid();
                    priorTop = rowTop; priorBottom = rowBottom;
                }
                previousTableSpan = selected[^1];
                foreach (var index in selected)
                {
                    if (index <= lastSpan[pageIndex] || !used.Add((pageIndex, index))) Invalid();
                    lastSpan[pageIndex] = index;
                }
                var bounds = cell.GetProperty("box");
                Array(bounds, 4, 4);
                var box = bounds.EnumerateArray().Select(UnitNumber).ToArray();
                if (box[0] != selected.Min(i => page.Spans[i].Box[0]) || box[1] != selected.Min(i => page.Spans[i].Box[1]) ||
                    box[2] != selected.Max(i => page.Spans[i].Box[2]) || box[3] != selected.Max(i => page.Spans[i].Box[3]) ||
                    UnitNumber(cell.GetProperty("minimum_ocr_confidence")) != confidence) Invalid();
            }
        }
        var warnings = result.GetProperty("warnings");
        Array(warnings, 0, 3);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var warning in warnings.EnumerateArray())
        {
            var value = String(warning);
            if (value is not ("line_limit_reached" or "heuristic_tables_require_review" or "table_limit_reached") || !seen.Add(value)) Invalid();
        }
        if (tables.GetArrayLength() > 0 && !seen.Contains("heuristic_tables_require_review")) Invalid();
    }

    private static void ValidateLines(JsonElement result, IReadOnlyList<Page> pages)
    {
        var lines = result.GetProperty("lines");
        Array(lines, 0, 50000);
        var next = new int[pages.Count];
        var previousPage = 0;
        foreach (var line in lines.EnumerateArray())
        {
            Properties(line, ["page_index", "start", "end", "span_indexes", "box", "minimum_ocr_confidence"]);
            var pageIndex = NonnegativeInt(line.GetProperty("page_index"));
            if (pageIndex >= pages.Count || pageIndex < previousPage) Invalid();
            for (var index = previousPage; index < pageIndex; index++)
                if (next[index] != pages[index].Spans.Count) Invalid();
            previousPage = pageIndex;
            var page = pages[pageIndex];
            var start = NonnegativeInt(line.GetProperty("start"));
            var end = PositiveInt(line.GetProperty("end"));
            var refs = line.GetProperty("span_indexes");
            Array(refs, 1, 50000);
            var selected = new List<Span>();
            if (next[pageIndex] > 0)
            {
                var previous = page.Spans[next[pageIndex] - 1];
                if (next[pageIndex] >= page.Spans.Count ||
                    !page.Text.AsSpan(page.Offsets[previous.End], page.Offsets[page.Spans[next[pageIndex]].Start] - page.Offsets[previous.End]).SequenceEqual("\n")) Invalid();
            }
            foreach (var reference in refs.EnumerateArray())
            {
                var index = NonnegativeInt(reference);
                if (index != next[pageIndex]++ || index >= page.Spans.Count) Invalid();
                selected.Add(page.Spans[index]);
            }
            if (selected[0].Start != start || selected[^1].End != end) Invalid();
            for (var index = 1; index < selected.Count; index++)
                if (page.Text.AsSpan(page.Offsets[selected[index - 1].End],
                    page.Offsets[selected[index].Start] - page.Offsets[selected[index - 1].End]).SequenceEqual("\n")) Invalid();
            var bounds = line.GetProperty("box");
            Array(bounds, 4, 4);
            var values = bounds.EnumerateArray().Select(UnitNumber).ToArray();
            if (values[0] != selected.Min(span => span.Box[0]) || values[1] != selected.Min(span => span.Box[1]) ||
                values[2] != selected.Max(span => span.Box[2]) || values[3] != selected.Max(span => span.Box[3]) ||
                UnitNumber(line.GetProperty("minimum_ocr_confidence")) != selected.Min(span => span.Confidence)) Invalid();
        }
        var warnings = result.GetProperty("warnings");
        Array(warnings, 0, 1);
        var truncated = warnings.GetArrayLength() == 1 && String(warnings[0]) == "line_limit_reached";
        if (warnings.GetArrayLength() == 1 && !truncated) Invalid();
        if (truncated && (lines.GetArrayLength() != 50000 || !pages.Where((page, index) => next[index] < page.Spans.Count).Any())) Invalid();
        if (!truncated && pages.Where((page, index) => next[index] != page.Spans.Count).Any()) Invalid();
    }

    private static double ValidateReferences(JsonElement references, Page page, int start, int end)
    {
        Array(references, 1, 50000);
        var minimum = 1d;
        var low = 0;
        var high = page.Spans.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (page.Spans[middle].End <= start) low = middle + 1;
            else high = middle;
        }
        var expected = low;
        foreach (var reference in references.EnumerateArray())
        {
            var index = NonnegativeInt(reference);
            if (index != expected || index >= page.Spans.Count) Invalid();
            var span = page.Spans[index];
            if (span.End <= start || span.Start >= end) Invalid();
            minimum = Math.Min(minimum, span.Confidence);
            expected++;
        }
        if (expected < page.Spans.Count && page.Spans[expected].Start < end) Invalid();
        return minimum;
    }

    private static int[] CodePointOffsets(string text)
    {
        var offsets = new List<int>(text.Length + 1) { 0 };
        var offset = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            offset += rune.Utf16SequenceLength;
            offsets.Add(offset);
        }
        return offsets.ToArray();
    }

    private static void Properties(JsonElement value, string[] expected)
    {
        if (value.ValueKind != JsonValueKind.Object) Invalid();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!expected.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name)) Invalid();
        }
        if (seen.Count != expected.Length) Invalid();
    }

    private static void Array(JsonElement value, int minimum, int maximum)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() < minimum || value.GetArrayLength() > maximum)
            Invalid();
    }

    private static string String(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String) Invalid();
        return value.GetString()!;
    }

    private static int NonnegativeInt(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var result) || result < 0) Invalid();
        return value.GetInt32();
    }

    private static int PositiveInt(JsonElement value)
    {
        var number = NonnegativeInt(value);
        if (number == 0) Invalid();
        return number;
    }

    private static double UnitNumber(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) ||
            !double.IsFinite(number) || number < 0 || number > 1) Invalid();
        return value.GetDouble();
    }

    private static void Invalid() => throw new InvalidOperationException("The document worker result is invalid.");
}
