import io
import json

import pytest
from PIL import Image

from openconnect_document_intelligence.pipeline import DocumentAnalyzer
from openconnect_document_intelligence.tiff import render_tiff_page


def make_tiff(pages=1):
    images = [Image.new("RGB", (20, 10), (index, 0, 0)) for index in range(pages)]
    output = io.BytesIO()
    images[0].save(output, format="TIFF", save_all=True, append_images=images[1:])
    return output.getvalue()


def test_renders_multipage_tiff_in_child():
    rendered = render_tiff_page(make_tiff(2), 1)
    assert rendered.page_count == 2 and rendered.page_index == 1
    assert rendered.png.startswith(b"\x89PNG\r\n\x1a\n")
    assert (rendered.width, rendered.height) == (20, 10)


def test_rejects_excess_pages_corrupt_input_and_invalid_page():
    assert render_tiff_page(make_tiff(30), 29).page_count == 30
    with pytest.raises(ValueError, match="page_limit_exceeded"):
        render_tiff_page(make_tiff(31), 0)
    with pytest.raises(ValueError, match="invalid_document"):
        render_tiff_page(b"II*\x00corrupt", 0)
    with pytest.raises(ValueError, match="invalid_page"):
        render_tiff_page(make_tiff(), 1)


def test_rejects_page_above_pixel_limit():
    output = io.BytesIO()
    Image.new("L", (4001, 4000), 255).save(output, format="TIFF", compression="tiff_adobe_deflate")
    with pytest.raises(ValueError, match="page_limit_exceeded"):
        render_tiff_page(output.getvalue(), 0)


def test_original_tiff_is_scanned_before_rendering_and_all_pages_are_processed():
    content = make_tiff(2)
    events = []

    class Scanner:
        def scan(self, data, *, timeout_seconds):
            assert data == content and timeout_seconds > 0
            events.append("scan")

    class Ocr:
        def analyze(self, data, *, timeout_seconds):
            assert data.startswith(b"\x89PNG") and timeout_seconds > 0
            events.append("ocr")
            return "page"

    result = DocumentAnalyzer(Scanner(), Ocr()).analyze(content)
    assert result.pages == ("page", "page")
    assert events == ["scan", "ocr", "ocr"]


def test_scanner_failure_prevents_tiff_decoding(monkeypatch):
    class Scanner:
        def scan(self, data, *, timeout_seconds):
            raise RuntimeError("malware_detected")

    monkeypatch.setattr("openconnect_document_intelligence.pipeline.render_tiff_page",
                        lambda *args, **kwargs: pytest.fail("decoded before scan"))
    with pytest.raises(RuntimeError, match="malware_detected"):
        DocumentAnalyzer(Scanner(), object()).analyze(make_tiff())


@pytest.mark.parametrize(("index", "page_count", "page_index"), [
    (0, True, 0), (0, 1, False), (1, 1, 1), (1, 2, True),
])
def test_rejects_inconsistent_child_page_metadata(monkeypatch, index, page_count, page_index):
    response = json.dumps({"pageCount": page_count, "pageIndex": page_index, "png": ""}).encode()
    monkeypatch.setattr("openconnect_document_intelligence.tiff._run_tesseract", lambda *_: response)
    with pytest.raises(ValueError, match="invalid_document"):
        render_tiff_page(b"II*\x00fixture", index)
