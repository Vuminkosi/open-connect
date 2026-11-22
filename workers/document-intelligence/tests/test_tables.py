import json
from dataclasses import asdict, replace
from pathlib import Path

import pytest

from openconnect_document_intelligence.ocr import OcrPage, WordSpan
from openconnect_document_intelligence.tables import extract_table_candidates


def page() -> OcrPage:
    return OcrPage("Item Price\nWood 10", 100, 100, (
        WordSpan("Item", 0, 4, (.10, .10, .24, .18), .9),
        WordSpan("Price", 5, 10, (.60, .10, .78, .18), .8),
        WordSpan("Wood", 11, 15, (.11, .22, .25, .30), .7),
        WordSpan("10", 16, 18, (.61, .22, .70, .30), .95),
    ), "fixture", "a" * 64)


def test_aligned_rows_return_source_cited_heuristic_cells():
    source = page()
    result = extract_table_candidates((source,))
    assert result.warnings == ("heuristic_tables_require_review",)
    table, = result.tables
    assert (table.page_index, table.rows, table.columns) == (0, 2, 2)
    assert [(cell.row, cell.column, source.text[cell.start:cell.end]) for cell in table.cells] == [
        (0, 0, "Item"), (0, 1, "Price"), (1, 0, "Wood"), (1, 1, "10")]
    assert table.cells[2].span_indexes == (2,)
    assert table.cells[2].minimum_ocr_confidence == .7


def test_unaligned_rows_are_not_presented_as_tables():
    source = page()
    spans = list(source.spans)
    spans[3] = replace(spans[3], box=(.8, .22, .9, .30))
    assert not extract_table_candidates((replace(source, spans=tuple(spans)),)).tables


def test_invalid_ocr_data_is_rejected_before_candidate_generation():
    source = page()
    spans = list(source.spans)
    spans[0] = replace(spans[0], text="fabricated")
    with pytest.raises(ValueError, match="invalid_ocr_spans"):
        extract_table_candidates((replace(source, spans=tuple(spans)),))


def test_cell_limit_reports_incomplete_extraction(monkeypatch):
    monkeypatch.setattr("openconnect_document_intelligence.tables.MAX_CELLS", 3)
    result = extract_table_candidates((page(),))
    assert result.tables == ()
    assert result.warnings == ("table_limit_reached",)


def test_shared_fixture_matches_python_candidates():
    root = Path(__file__).resolve().parents[3]
    fixture = json.loads((root / "contracts/fixtures/valid/document-tables-response.json").read_text(encoding="utf-8"))
    actual = json.loads(json.dumps(asdict(extract_table_candidates((page(),)))))
    assert actual == {"tables": fixture["result"]["tables"], "warnings": fixture["result"]["warnings"]}
