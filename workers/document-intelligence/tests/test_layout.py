import json
from dataclasses import asdict, replace
from pathlib import Path

import pytest
from jsonschema import Draft202012Validator

from openconnect_document_intelligence.layout import extract_line_regions
from openconnect_document_intelligence.ocr import OcrPage, WordSpan


def test_line_regions_preserve_source_offsets_boxes_and_confidence():
    page = OcrPage("Hello world\nZoë", 100, 100, (
        WordSpan("Hello", 0, 5, (.1, .1, .3, .2), .9),
        WordSpan("world", 6, 11, (.4, .1, .8, .3), .7),
        WordSpan("Zoë", 12, 15, (.2, .5, .5, .6), .8),
    ), "fixture", "a" * 64)
    result = extract_line_regions((page, page))
    assert len(result.lines) == 4 and not result.warnings
    first, second = result.lines[:2]
    assert page.text[first.start:first.end] == "Hello world"
    assert first.span_indexes == (0, 1)
    assert first.box == (.1, .1, .8, .3) and first.minimum_ocr_confidence == .7
    assert page.text[second.start:second.end] == "Zoë" and second.span_indexes == (2,)
    assert result.lines[2].page_index == 1


def test_invalid_span_separator_is_rejected():
    page = OcrPage("one|two", 10, 10, (
        WordSpan("one", 0, 3, (0, 0, .4, 1), .9),
        WordSpan("two", 4, 7, (.5, 0, 1, 1), .9),
    ), "fixture", "a" * 64)
    with pytest.raises(ValueError, match="invalid_ocr_spans"):
        extract_line_regions((page,))


@pytest.mark.parametrize("mutation", ["wrong_text", "wrong_offset", "uncited_text", "box", "confidence"])
def test_malformed_ocr_cannot_produce_layout_evidence(mutation):
    span = WordSpan("word", 0, 4, (0, 0, 1, 1), .8)
    page = OcrPage("word", 10, 10, (span,), "fixture", "a" * 64)
    if mutation == "wrong_text":
        span = replace(span, text="other")
    elif mutation == "wrong_offset":
        span = replace(span, end=5)
    elif mutation == "uncited_text":
        page = replace(page, text="word hidden")
    elif mutation == "box":
        span = replace(span, box=(.8, 0, .2, 1))
    elif mutation == "confidence":
        span = replace(span, confidence=float("nan"))
    with pytest.raises(ValueError, match="invalid_ocr_spans"):
        extract_line_regions((replace(page, spans=(span,)),))


def test_line_limit_is_explicit(monkeypatch):
    monkeypatch.setattr("openconnect_document_intelligence.layout.MAX_LINES", 1)
    page = OcrPage("one\ntwo", 10, 10, (
        WordSpan("one", 0, 3, (0, 0, .4, .4), .9),
        WordSpan("two", 4, 7, (0, .5, .4, 1), .8),
    ), "fixture", "a" * 64)
    result = extract_line_regions((page,))
    assert len(result.lines) == 1 and result.warnings == ("line_limit_reached",)


def test_shared_fixture_matches_python_layout_and_schema():
    root = Path(__file__).resolve().parents[3]
    fixture = json.loads((root / "contracts/fixtures/valid/document-layout-response.json").read_text(encoding="utf-8"))
    schema = json.loads((root / "contracts/jsonschema/document-layout-response.json").read_text(encoding="utf-8"))
    Draft202012Validator(schema).validate(fixture)
    pages = tuple(OcrPage(page["text"], page["width"], page["height"],
                          tuple(WordSpan(**span) for span in page["spans"]), page["model_id"],
                          page["model_fingerprint"], page["language"]) for page in fixture["result"]["pages"])
    actual = json.loads(json.dumps(asdict(extract_line_regions(pages))))
    assert actual == {"lines": fixture["result"]["lines"], "warnings": fixture["result"]["warnings"]}
