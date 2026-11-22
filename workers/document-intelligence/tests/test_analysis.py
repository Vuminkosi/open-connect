from dataclasses import asdict
import json
from pathlib import Path

import pytest
from jsonschema import Draft202012Validator, FormatChecker

from openconnect_document_intelligence.analysis import (
    AnalysisComponent, DocumentAnalysis, PartialDocumentAnalysis, assemble_analysis, assemble_partial_analysis,
)
from openconnect_document_intelligence.ocr import OcrPage, WordSpan
from openconnect_document_intelligence.pipeline import DocumentPageResult, DocumentResult, PartialDocumentResult
from openconnect_document_intelligence.storage import DocumentReference


def page():
    return OcrPage("Item Price\nWood 10", 100, 100, (
        WordSpan("Item", 0, 4, (.10, .10, .24, .18), .9),
        WordSpan("Price", 5, 10, (.60, .10, .78, .18), .8),
        WordSpan("Wood", 11, 15, (.11, .22, .25, .30), .7),
        WordSpan("10", 16, 18, (.61, .22, .70, .30), .95),
    ), "fixture", "a" * 64)


def test_analysis_and_storage_reference_representations_hide_sensitive_values():
    secret = "private-extracted-value"
    component = AnalysisComponent("completed", ({"value": secret},), ())
    full = DocumentAnalysis((secret,), component, component, component, "completed", ())
    partial = PartialDocumentAnalysis((secret,), component, component, component, "partial", ())
    reference = DocumentReference("a" * 64, "b" * 64, 1, secret)
    for value in (component, full, partial, reference):
        assert secret not in repr(value)
    assert reference.object_id not in repr(reference)


def test_combines_cited_candidates_and_preserves_source():
    source = page()
    result = assemble_analysis(DocumentResult((source,)))
    assert result.status == "completed"
    assert result.pages == (source,)
    assert len(result.layout.items) == 2 and len(result.tables.items) == 1
    assert result.tables.items[0]["cells"][2]["span_indexes"] == (2,)
    assert "heuristic_candidates_require_review" in result.warnings
    assert asdict(result)["pages"][0]["text"] == source.text


def test_component_failure_preserves_other_results_without_private_diagnostics(monkeypatch):
    def fail(_pages):
        raise RuntimeError("private content must not escape")

    monkeypatch.setattr("openconnect_document_intelligence.analysis.extract_table_candidates", fail)
    result = assemble_analysis(DocumentResult((page(),)))
    assert result.status == "partial" and result.tables.status == "unavailable"
    assert result.layout.status == "completed" and result.pages[0].text == page().text
    assert "private content" not in str(result.warnings)


def test_truncated_components_are_reported_partial(monkeypatch):
    monkeypatch.setattr("openconnect_document_intelligence.tables.MAX_CELLS", 3)
    result = assemble_analysis(DocumentResult((page(),)))
    assert result.status == "partial" and result.tables.status == "partial"
    assert "table_limit_reached" in result.tables.warnings


def test_empty_document_result_is_rejected():
    with pytest.raises(ValueError, match="invalid_document_result"):
        assemble_analysis(DocumentResult(()))


def test_mixed_ocr_provenance_is_rejected():
    from dataclasses import replace

    source = page()
    with pytest.raises(ValueError, match="inconsistent_ocr_provenance"):
        assemble_analysis(DocumentResult((source, replace(source, model_fingerprint="b" * 64))))


def test_shared_analysis_contract_and_partial_response_fixture():
    root = Path(__file__).resolve().parents[3] / "contracts"
    for direction in ("request", "response"):
        schema = json.loads((root / "jsonschema" / f"document-analyze-{direction}.json").read_text(encoding="utf-8"))
        Draft202012Validator.check_schema(schema)
        validator = Draft202012Validator(schema, format_checker=FormatChecker())
        valid = json.loads((root / "fixtures" / "valid" / f"document-analyze-{direction}.json").read_text(encoding="utf-8"))
        invalid = json.loads((root / "fixtures" / "invalid" / f"document-analyze-{direction}.json").read_text(encoding="utf-8"))
        validator.validate(valid)
        assert not validator.is_valid(invalid)
    document = PartialDocumentResult((DocumentPageResult(0, "failed", None, "page_unavailable"),
                                      DocumentPageResult(1, "completed", page())), "partial")
    assert json.loads(json.dumps(asdict(assemble_partial_analysis(document)))) == valid["result"]
