import json
from dataclasses import asdict
from pathlib import Path

from jsonschema import Draft202012Validator

from openconnect_document_intelligence.fields import extract_field_candidates
from openconnect_document_intelligence.ocr import OcrPage, WordSpan


def page(text, words):
    spans = []
    start = 0
    for word, confidence in words:
        start = text.index(word, start)
        spans.append(WordSpan(word, start, start + len(word), (0, 0, 1, 1), confidence))
        start += len(word)
    return OcrPage(text, 100, 100, tuple(spans), "fixture", "a" * 64)


def test_fields_are_literal_candidates_with_page_offsets_and_ocr_spans():
    first = page("Invoice: A-10\nTotal: 45.00", [("Invoice:", .9), ("A-10", .8),
                                                   ("Total:", .7), ("45.00", .6)])
    second = page("Name: Zoë Ng", [("Name:", .9), ("Zoë", .8), ("Ng", .7)])
    result = extract_field_candidates((first, second))
    assert [(f.page_index, f.label, f.value) for f in result.fields] == [
        (0, "Invoice", "A-10"), (0, "Total", "45.00"), (1, "Name", "Zoë Ng")]
    for candidate in result.fields:
        source = (first, second)[candidate.page_index]
        assert source.text[candidate.label_start:candidate.label_end] == candidate.label
        assert source.text[candidate.value_start:candidate.value_end] == candidate.value
        assert candidate.label_span_indexes and candidate.value_span_indexes
    assert result.fields[-1].value_span_indexes == (1, 2)
    assert result.fields[-1].minimum_ocr_confidence == .7


def test_missing_values_and_uncited_text_are_not_invented():
    source = page("Absent:\n: value\nUncited: 17\n", [("Absent:", .9), (":", .8)])
    result = extract_field_candidates((source,))
    assert result.fields == ()
    assert result.warnings == ("field_candidates_skipped",)


def test_field_count_is_bounded_and_signals_truncation(monkeypatch):
    monkeypatch.setattr("openconnect_document_intelligence.fields.MAX_FIELDS", 1)
    source = page("A: 1\nB: 2", [("A:", .9), ("1", .9), ("B:", .8), ("2", .8)])
    result = extract_field_candidates((source,))
    assert len(result.fields) == 1
    assert result.warnings == ("field_limit_reached",)


def test_shared_response_fixture_matches_python_field_extraction():
    root = Path(__file__).resolve().parents[3]
    fixture = json.loads((root / "contracts/fixtures/valid/document-fields-response.json").read_text(encoding="utf-8"))
    schema = json.loads((root / "contracts/jsonschema/document-fields-response.json").read_text(encoding="utf-8"))
    Draft202012Validator(schema).validate(fixture)
    fixture_page = fixture["result"]["pages"][0]
    source = OcrPage(fixture_page["text"], fixture_page["width"], fixture_page["height"],
        tuple(WordSpan(**span) for span in fixture_page["spans"]), fixture_page["model_id"],
        fixture_page["model_fingerprint"], fixture_page["language"])
    actual = json.loads(json.dumps(asdict(extract_field_candidates((source,)))))
    assert actual == {"fields": fixture["result"]["fields"], "warnings": fixture["result"]["warnings"]}
