from dataclasses import replace

import pytest

from openconnect_document_intelligence.analysis import assemble_partial_analysis
from openconnect_document_intelligence.malware import MalwareDetectedError
from openconnect_document_intelligence.ocr import OcrPage, WordSpan
from openconnect_document_intelligence.pdf import RenderedPage
from openconnect_document_intelligence.pipeline import DocumentAnalyzer


def page():
    return OcrPage("Item Price\nWood 10", 100, 100, (
        WordSpan("Item", 0, 4, (.10, .10, .24, .18), .9),
        WordSpan("Price", 5, 10, (.60, .10, .78, .18), .8),
        WordSpan("Wood", 11, 15, (.11, .22, .25, .30), .7),
        WordSpan("10", 16, 18, (.61, .22, .70, .30), .95),
    ), "fixture", "a" * 64)


class Scanner:
    def __init__(self):
        self.calls = []

    def scan(self, content, **_kwargs):
        self.calls.append(content)


def test_partial_pages_keep_original_page_citations(monkeypatch):
    scanner = Scanner()

    def render(_content, index, **_kwargs):
        if index == 1:
            raise RuntimeError("private diagnostic")
        return RenderedPage(3, index, bytes([index + 1]), 100, 100)

    class Ocr:
        def analyze(self, _content, **_kwargs):
            assert scanner.calls == [b"%PDF-fixture"]
            return page()

    monkeypatch.setattr("openconnect_document_intelligence.pipeline.render_pdf_page", render)
    document = DocumentAnalyzer(scanner, Ocr()).analyze_partial(b"%PDF-fixture")
    assert document.status == "partial"
    assert [(p.page_index, p.status, p.reason) for p in document.pages] == [
        (0, "completed", None), (1, "failed", "page_unavailable"), (2, "completed", None)]
    result = assemble_partial_analysis(document)
    assert result.status == "partial"
    assert {item["page_index"] for item in result.layout.items} == {0, 2}
    assert {item["page_index"] for item in result.tables.items} == {0, 2}
    assert "private diagnostic" not in str(result)


def test_scanner_failure_aborts_before_any_rendering(monkeypatch):
    class Infected:
        def scan(self, *_args, **_kwargs):
            raise MalwareDetectedError("private scanner detail")

    monkeypatch.setattr("openconnect_document_intelligence.pipeline.render_pdf_page",
                        lambda *_args, **_kwargs: pytest.fail("rendered before scan"))
    with pytest.raises(MalwareDetectedError):
        DocumentAnalyzer(Infected(), object()).analyze_partial(b"%PDF-fixture")


def test_invalid_page_metadata_fails_closed(monkeypatch):
    monkeypatch.setattr("openconnect_document_intelligence.pipeline.render_pdf_page",
                        lambda _content, index, **_kwargs: RenderedPage(2 if index == 0 else 3, index, b"x", 100, 100))

    class Ocr:
        def analyze(self, *_args, **_kwargs):
            return page()

    with pytest.raises(ValueError, match="invalid_document"):
        DocumentAnalyzer(Scanner(), Ocr()).analyze_partial(b"%PDF-fixture")


def test_invalid_partial_metadata_is_rejected(monkeypatch):
    monkeypatch.setattr("openconnect_document_intelligence.pipeline.render_pdf_page",
                        lambda _content, index, **_kwargs: RenderedPage(1, index, b"x", 100, 100))

    class Ocr:
        def analyze(self, *_args, **_kwargs):
            return page()

    result = DocumentAnalyzer(Scanner(), Ocr()).analyze_partial(b"%PDF-fixture")
    with pytest.raises(ValueError, match="invalid_document_result"):
        assemble_partial_analysis(replace(result, status="partial"))
