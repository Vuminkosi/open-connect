import hashlib
import io
import sys
from pathlib import Path

import pytest
from PIL import Image

from openconnect_document_intelligence import ocr
from openconnect_document_intelligence.ocr import TesseractOcr, _parse_tsv, _prepare_image
from openconnect_worker_common.models.runtime_cache import (
    RuntimeModelAsset, RuntimeModelCacheManager, RuntimeModelManifest,
)

DATA = b"tiny-test-model"
HEADER = b"level\tpage_num\tblock_num\tpar_num\tline_num\tleft\ttop\twidth\theight\tconf\ttext\n"


@pytest.fixture
def model(tmp_path: Path):
    manifest = RuntimeModelManifest(workerKind="document-intelligence", modelId="fixture/ocr",
        immutableRevision="a" * 40, license="apache-2.0", assets=[RuntimeModelAsset(
            relativePath="eng.traineddata", sizeBytes=len(DATA), sha256=hashlib.sha256(DATA).hexdigest())])
    manager = RuntimeModelCacheManager(tmp_path / "cache", "document-intelligence")
    result = manager.provision_or_verify(manifest, asset_fetcher=lambda asset, target: target.write_bytes(DATA))
    assert result.ready
    executable = tmp_path / "tesseract"
    executable.write_bytes(b"not invoked")
    return manager, manifest, executable, result.revision_dir


def image_bytes() -> bytes:
    output = io.BytesIO()
    Image.new("RGB", (100, 50), "white").save(output, format="PNG")
    return output.getvalue()


def test_offline_ocr_preserves_text_spans_and_normalized_boxes(model, monkeypatch):
    manager, manifest, executable, _ = model
    def run(args, content, timeout):
        assert args[0] == str(executable) and args[1:3] == ["stdin", "stdout"]
        assert args[-2:] == ["-c", "tessedit_create_tsv=1"]
        assert content.startswith(b"\x89PNG") and timeout == 30
        return (HEADER + b"5\t1\t1\t1\t1\t10\t5\t30\t10\t95\tHello\n"
                + b"5\t1\t1\t1\t1\t45\t5\t25\t10\t80\tworld\n")
    monkeypatch.setattr(ocr, "_run_tesseract", run)
    page = TesseractOcr(manager, manifest, executable).analyze(image_bytes())
    assert page.text == "Hello world" and (page.width, page.height) == (100, 50)
    assert page.spans[0].box == pytest.approx((0.1, 0.1, 0.4, 0.3))
    assert page.spans[0].confidence == 0.95
    assert all(page.text[span.start:span.end] == span.text for span in page.spans)


@pytest.mark.parametrize("content", [b"", b"<html>bad</html>", b"\x89PNG\r\n\x1a\ntruncated"])
def test_invalid_image_is_rejected(content: bytes):
    with pytest.raises(ValueError):
        _prepare_image(content)


@pytest.mark.parametrize("row", [b"5\t1\t1\t1\t1\t-1\t0\t10\t10\t99\tbad\n",
                                  b"5\t1\t1\t1\t1\t0\t0\t200\t10\t99\tbad\n",
                                  b"5\t1\t1\t1\t1\t0\t0\t10\t10\tnan\tbad\n",
                                  b"5\t1\t0\t1\t1\t0\t0\t10\t10\t90\tbad\n"])
def test_invalid_ocr_output_is_rejected(row):
    with pytest.raises(RuntimeError, match="ocr_output_invalid"):
        _parse_tsv(HEADER + row, 100, 50)


def test_recognized_lines_preserve_source_offsets():
    data = (HEADER + b"5\t1\t1\t1\t1\t0\t0\t10\t10\t95\tInvoice\n"
            + b"5\t1\t1\t1\t1\t15\t0\t10\t10\t80\t123\n"
            + b"5\t1\t1\t1\t2\t0\t15\t10\t10\t75\tTotal:\n"
            + b"5\t1\t1\t1\t2\t15\t15\t10\t10\t70\t45.00\n"
            + b"5\t1\t2\t1\t1\t0\t30\t10\t10\t90\tPaid\n")
    text, spans = _parse_tsv(data, 100, 50)
    assert text == "Invoice 123\nTotal: 45.00\nPaid"
    assert [text[span.start:span.end] for span in spans] == [
        "Invoice", "123", "Total:", "45.00", "Paid"]


def test_corrupted_model_never_executes(model, monkeypatch):
    manager, manifest, executable, revision = model
    engine = TesseractOcr(manager, manifest, executable)
    (revision / "assets" / "eng.traineddata").write_bytes(b"corrupt")
    monkeypatch.setattr(ocr, "_run_tesseract", lambda *a, **k: pytest.fail("OCR invoked"))
    with pytest.raises(RuntimeError, match="model_unavailable"):
        engine.analyze(image_bytes())


def test_timeout_returns_safe_error():
    with pytest.raises(RuntimeError, match="ocr_unavailable"):
        ocr._run_tesseract([sys.executable, "-c", "import time; time.sleep(5)"], b"", 0.1)


def test_child_output_is_bounded(monkeypatch):
    monkeypatch.setattr(ocr, "MAX_OUTPUT_BYTES", 1024)
    with pytest.raises(RuntimeError, match="ocr_output_limit"):
        ocr._run_tesseract([sys.executable, "-c", "import sys; sys.stdout.write('x'*65536)"], b"", 5)
def test_ocr_results_hide_document_text_in_default_representation():
    from openconnect_document_intelligence.ocr import OcrPage, WordSpan

    secret = "private account number"
    span = WordSpan(secret, 0, len(secret), (0.0, 0.0, 1.0, 1.0), 0.9)
    page = OcrPage(secret, 100, 100, (span,), "model", "a" * 64)
    assert secret not in repr(span)
    assert secret not in repr(page)
