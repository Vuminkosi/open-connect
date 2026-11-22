import asyncio
import base64
import json
import threading
from datetime import datetime, timedelta, timezone
from pathlib import Path

import pytest
from fastapi.testclient import TestClient
from jsonschema import Draft202012Validator, FormatChecker

from openconnect_document_intelligence import app as app_module
from openconnect_document_intelligence.malware import MalwareDetectedError, MalwareScanUnavailableError
from openconnect_document_intelligence.ocr import OcrPage, WordSpan
from openconnect_document_intelligence.pipeline import DocumentAnalyzer, DocumentPageResult, DocumentResult, PartialDocumentResult
from openconnect_worker_common.security.signing import sign_bytes

KEY = bytes.fromhex("ab" * 32)


def test_document_payload_representation_hides_content():
    secret = "cHJpdmF0ZS1kb2N1bWVudA=="
    payload = app_module.Payload(contentBase64=secret)
    assert secret not in repr(payload) and secret not in str(payload)
    assert payload.model_dump()["contentBase64"] == secret


class Scanner:
    def scan(self, content, *, timeout_seconds=None):
        if content == b"infected":
            raise MalwareDetectedError("malware_detected")
        if content == b"scanner-down":
            raise MalwareScanUnavailableError("malware_scan_unavailable")


class Ocr:
    def _verify(self):
        pass


class Analyzer:
    def analyze(self, content, *, timeout_seconds):
        assert timeout_seconds > 0
        Scanner().scan(content)
        return DocumentResult((OcrPage("redacted result", 10, 10, (), "test/model", "a" * 64),))


@pytest.fixture
def client(monkeypatch):
    monkeypatch.setenv("OPENCONNECT_WORKER_TOKEN", "test-token")
    monkeypatch.setenv("OPENCONNECT_WORKER_SIGNING_KEY", KEY.hex())
    monkeypatch.setenv("OC_DOCUMENT_ANALYZE_ENABLED", "false")
    monkeypatch.setattr(app_module, "_sandbox_ready", lambda: True)
    monkeypatch.setattr(app_module, "_load_runtime", lambda: app_module.Runtime(Scanner(), Ocr(), Analyzer()))
    with TestClient(app_module.app) as test_client:
        yield test_client


def request_body(content=b"clean"):
    now = datetime.now(timezone.utc)
    return {"schemaVersion": "openconnect.v1", "taskId": "task-1", "kind": "documents.ocr",
            "tenantId": "01ARZ3NDEKTSV4RRFFQ69G5FAV",
            "issuedAt": (now - timedelta(seconds=1)).isoformat(),
            "expiresAt": (now + timedelta(minutes=1)).isoformat(),
            "payload": {"contentBase64": base64.b64encode(content).decode()}}


def submit(client, body, signature=None):
    content = json.dumps(body).encode()
    return client.post("/internal/v1/execute", content=content, headers={
        "Authorization": "Bearer test-token", "X-OpenConnect-Signature": signature or sign_bytes(content, KEY),
    })


def test_cancelled_request_keeps_slot_until_ocr_thread_finishes():
    started = threading.Event()
    release = threading.Event()
    finished = threading.Event()

    class BlockingAnalyzer:
        def analyze(self, content, *, timeout_seconds):
            started.set()
            try:
                assert release.wait(5)
            finally:
                finished.set()

    runtime = app_module.Runtime(Scanner(), Ocr(), BlockingAnalyzer())
    expires = datetime.now(timezone.utc) + timedelta(minutes=1)

    async def exercise():
        task = asyncio.create_task(asyncio.to_thread(app_module._analyze, runtime, b"clean", expires))
        try:
            assert await asyncio.to_thread(started.wait, 5)
            task.cancel()
            with pytest.raises(asyncio.CancelledError):
                await task
            with pytest.raises(app_module._Busy):
                app_module._analyze(runtime, b"another", expires)
        finally:
            release.set()
            assert await asyncio.to_thread(finished.wait, 5)

    asyncio.run(exercise())
    assert app_module._slot.acquire(blocking=False)
    app_module._slot.release()


def test_request_expired_before_thread_starts_does_not_run_ocr():
    class NeverRun:
        def analyze(self, content, *, timeout_seconds):
            pytest.fail("expired work reached OCR")

    runtime = app_module.Runtime(Scanner(), Ocr(), NeverRun())
    with pytest.raises(ValueError, match="expired_request"):
        app_module._analyze(runtime, b"clean", datetime.now(timezone.utc) - timedelta(seconds=1))
    assert app_module._slot.acquire(blocking=False)
    app_module._slot.release()


def test_private_signed_execution_and_capability(client):
    assert client.get("/health/ready").status_code == 200
    assert client.get("/internal/v1/capabilities").status_code == 401
    capabilities = client.get("/internal/v1/capabilities", headers={"Authorization": "Bearer test-token"})
    assert capabilities.json()["capabilities"][0]["id"] == "documents.ocr"
    assert {item["id"] for item in capabilities.json()["capabilities"]} == {"documents.ocr", "documents.fields", "documents.layout", "documents.tables"}
    response = submit(client, request_body())
    assert response.status_code == 200 and response.json()["result"]["pages"][0]["text"] == "redacted result"
    schemas = Path(__file__).resolve().parents[3] / "contracts" / "jsonschema"
    for value, name in [(request_body(), "document-ocr-request.json"),
                        (response.json(), "document-ocr-response.json")]:
        schema = json.loads((schemas / name).read_text(encoding="utf-8"))
        Draft202012Validator.check_schema(schema)
        Draft202012Validator(schema, format_checker=FormatChecker()).validate(value)


def test_analysis_is_opt_in_and_reports_partial_pages_with_contract(client, monkeypatch):
    body = request_body()
    body["kind"] = "documents.analyze"
    monkeypatch.setenv("OC_DOCUMENT_ANALYZE_ENABLED", "false")
    assert submit(client, body).status_code == 503
    monkeypatch.setenv("OC_DOCUMENT_ANALYZE_ENABLED", "true")
    source = OcrPage("Item Price\nWood 10", 100, 100, (
        WordSpan("Item", 0, 4, (.10, .10, .24, .18), .9),
        WordSpan("Price", 5, 10, (.60, .10, .78, .18), .8),
        WordSpan("Wood", 11, 15, (.11, .22, .25, .30), .7),
        WordSpan("10", 16, 18, (.61, .22, .70, .30), .95),
    ), "test/model", "a" * 64)

    class PartialAnalyzer:
        def analyze_partial(self, content, *, timeout_seconds):
            Scanner().scan(content)
            return PartialDocumentResult((DocumentPageResult(0, "failed", None, "page_unavailable"),
                                          DocumentPageResult(1, "completed", source)), "partial")

    runtime = app_module.app.state.runtime
    monkeypatch.setattr(app_module.app.state, "runtime", app_module.Runtime(runtime.scanner, runtime.ocr, PartialAnalyzer()))
    capabilities = client.get("/internal/v1/capabilities", headers={"Authorization": "Bearer test-token"}).json()
    assert "documents.analyze" in {item["id"] for item in capabilities["capabilities"]}
    response = submit(client, body)
    assert response.status_code == 200
    result = response.json()["result"]
    assert result["status"] == "partial" and result["pages"][0]["reason"] == "page_unavailable"
    assert result["tables"]["items"][0]["page_index"] == 1
    root = Path(__file__).resolve().parents[3] / "contracts" / "jsonschema"
    for value, name in ((body, "document-analyze-request.json"), (response.json(), "document-analyze-response.json")):
        schema = json.loads((root / name).read_text(encoding="utf-8"))
        Draft202012Validator.check_schema(schema)
        Draft202012Validator(schema, format_checker=FormatChecker()).validate(value)


def test_signed_tiff_request_uses_real_renderer_and_existing_ocr_contract(client, monkeypatch):
    from test_tiff import make_tiff

    content = make_tiff(2)
    observed = []

    class ObservedScanner:
        def scan(self, value, *, timeout_seconds=None):
            assert value == content
            observed.append("scan")

    class FixtureOcr:
        def analyze(self, png, *, timeout_seconds):
            assert png.startswith(b"\x89PNG") and timeout_seconds > 0
            observed.append("ocr")
            return OcrPage("Hello", 20, 10, (WordSpan("Hello", 0, 5, (0, 0, 1, 1), .8),),
                           "test/model", "a" * 64)

    scanner, ocr = ObservedScanner(), FixtureOcr()
    monkeypatch.setattr(app_module.app.state, "runtime", app_module.Runtime(scanner, ocr, DocumentAnalyzer(scanner, ocr)))
    request = request_body(content)
    result = submit(client, request)
    assert result.status_code == 200
    assert [page["text"] for page in result.json()["result"]["pages"]] == ["Hello", "Hello"]
    assert observed == ["scan", "ocr", "ocr"]
    schemas = Path(__file__).resolve().parents[3] / "contracts" / "jsonschema"
    for value, name in [(request, "document-ocr-request.json"), (result.json(), "document-ocr-response.json")]:
        schema = json.loads((schemas / name).read_text(encoding="utf-8"))
        Draft202012Validator(schema, format_checker=FormatChecker()).validate(value)


@pytest.mark.parametrize("content,status,code", [
    (b"infected", 422, "malware_detected"),
    (b"scanner-down", 503, "capability_unavailable"),
])
def test_scan_failures_return_safe_errors(client, caplog, content, status, code):
    response = submit(client, request_body(content))
    assert response.status_code == status and response.json()["error"]["code"] == code
    assert content.decode() not in response.text and content.decode() not in caplog.text


def test_invalid_signatures_expiry_and_untrusted_fields_are_rejected(client):
    assert submit(client, request_body(), signature="0" * 64).status_code == 401
    expired = request_body()
    expired["expiresAt"] = (datetime.now(timezone.utc) - timedelta(seconds=1)).isoformat()
    assert submit(client, expired).status_code == 422
    unknown = request_body()
    unknown["payload"]["unknown"] = "secret-sentinel"
    response = submit(client, unknown)
    assert response.status_code == 422 and "secret-sentinel" not in response.text
    incomplete = request_body()
    incomplete["kind"] = "documents.analyze"
    assert submit(client, incomplete).status_code == 503


def test_signed_duplicate_fields_are_rejected_before_scanning(client, monkeypatch):
    class NeverRun:
        def analyze(self, *_args, **_kwargs):
            pytest.fail("ambiguous request reached the scanner")

    runtime = app_module.app.state.runtime
    monkeypatch.setattr(app_module.app.state, "runtime", app_module.Runtime(runtime.scanner, runtime.ocr, NeverRun()))
    raw = json.dumps(request_body()).encode()
    for duplicate in (raw.replace(b'{"schemaVersion"', b'{"taskId":"other","schemaVersion"', 1),
                      raw.replace(b'"contentBase64":', b'"contentBase64":"b3RoZXI=","contentBase64":', 1)):
        response = client.post("/internal/v1/execute", content=duplicate, headers={
            "Authorization": "Bearer test-token", "X-OpenConnect-Signature": sign_bytes(duplicate, KEY),
        })
        assert response.status_code == 422
    bad_tenant = request_body()
    bad_tenant["tenantId"] += "\n"
    assert submit(client, bad_tenant).status_code == 422


def test_missing_sandbox_does_not_advertise_or_execute(client, monkeypatch):
    monkeypatch.setattr(app_module, "_sandbox_ready", lambda: False)
    assert client.get("/health/ready").status_code == 503
    assert client.get("/internal/v1/capabilities", headers={
        "Authorization": "Bearer test-token"}).json() == {"capabilities": []}
    assert submit(client, request_body()).status_code == 503


def test_missing_auth_configuration_fails_closed(client, monkeypatch):
    monkeypatch.delenv("OPENCONNECT_WORKER_SIGNING_KEY")
    assert client.get("/health/ready").status_code == 503
    assert submit(client, request_body()).status_code == 503


def test_output_limit_fails_without_disclosing_text(client, monkeypatch):
    monkeypatch.setattr(app_module, "MAX_RESPONSE_BYTES", 1)
    response = submit(client, request_body())
    assert response.status_code == 503 and "redacted result" not in response.text


def test_fields_use_screened_ocr_and_return_citable_candidates(client, monkeypatch):
    class FieldAnalyzer:
        def analyze(self, content, *, timeout_seconds):
            Scanner().scan(content)
            return DocumentResult((OcrPage("Total: 45.00", 10, 10, (
                WordSpan("Total:", 0, 6, (0, 0, .5, 1), .9),
                WordSpan("45.00", 7, 12, (.5, 0, 1, 1), .7),
            ), "test/model", "a" * 64),))

    runtime = app_module.app.state.runtime
    monkeypatch.setattr(app_module.app.state, "runtime", app_module.Runtime(
        runtime.scanner, runtime.ocr, FieldAnalyzer()))
    body = request_body()
    body["kind"] = "documents.fields"
    response = submit(client, body)
    assert response.status_code == 200
    field = response.json()["result"]["fields"][0]
    assert (field["label"], field["value"], field["value_start"], field["value_end"]) == (
        "Total", "45.00", 7, 12)
    assert field["minimum_ocr_confidence"] == .7
    schemas = Path(__file__).resolve().parents[3] / "contracts" / "jsonschema"
    for value, name in [(body, "document-fields-request.json"),
                        (response.json(), "document-fields-response.json")]:
        schema = json.loads((schemas / name).read_text(encoding="utf-8"))
        Draft202012Validator.check_schema(schema)
        Draft202012Validator(schema, format_checker=FormatChecker()).validate(value)
    infected = request_body(b"infected")
    infected["kind"] = "documents.fields"
    assert submit(client, infected).json()["error"]["code"] == "malware_detected"


def test_layout_returns_cited_line_regions_and_conforms_to_contract(client, monkeypatch):
    class LayoutAnalyzer:
        def analyze(self, content, *, timeout_seconds):
            Scanner().scan(content)
            return DocumentResult((OcrPage("Hello world\nAgain", 10, 10, (
                WordSpan("Hello", 0, 5, (0, 0, .4, .4), .9),
                WordSpan("world", 6, 11, (.5, 0, 1, .4), .7),
                WordSpan("Again", 12, 17, (0, .5, .4, 1), .8),
            ), "test/model", "a" * 64),))

    runtime = app_module.app.state.runtime
    monkeypatch.setattr(app_module.app.state, "runtime", app_module.Runtime(runtime.scanner, runtime.ocr, LayoutAnalyzer()))
    body = request_body()
    body["kind"] = "documents.layout"
    response = submit(client, body)
    assert response.status_code == 200
    lines = response.json()["result"]["lines"]
    assert [line["span_indexes"] for line in lines] == [[0, 1], [2]]
    assert lines[0]["box"] == [0, 0, 1, .4] and lines[0]["minimum_ocr_confidence"] == .7
    schemas = Path(__file__).resolve().parents[3] / "contracts" / "jsonschema"
    for value, name in [(body, "document-layout-request.json"), (response.json(), "document-layout-response.json")]:
        schema = json.loads((schemas / name).read_text(encoding="utf-8"))
        Draft202012Validator.check_schema(schema)
        Draft202012Validator(schema, format_checker=FormatChecker()).validate(value)


def test_tables_returns_source_cited_candidates_and_conforms_to_contract(client, monkeypatch):
    class TableAnalyzer:
        def analyze(self, content, *, timeout_seconds):
            Scanner().scan(content)
            return DocumentResult((OcrPage("Item Price\nWood 10", 100, 100, (
                WordSpan("Item", 0, 4, (.1, .1, .24, .18), .9),
                WordSpan("Price", 5, 10, (.6, .1, .78, .18), .8),
                WordSpan("Wood", 11, 15, (.11, .22, .25, .3), .7),
                WordSpan("10", 16, 18, (.61, .22, .7, .3), .95),
            ), "test/model", "a" * 64),))

    runtime = app_module.app.state.runtime
    monkeypatch.setattr(app_module.app.state, "runtime", app_module.Runtime(runtime.scanner, runtime.ocr, TableAnalyzer()))
    body = request_body()
    body["kind"] = "documents.tables"
    response = submit(client, body)
    assert response.status_code == 200
    result = response.json()["result"]
    assert result["warnings"] == ["heuristic_tables_require_review"]
    assert len(result["tables"]) == 1 and len(result["tables"][0]["cells"]) == 4
    schemas = Path(__file__).resolve().parents[3] / "contracts" / "jsonschema"
    for value, name in [(body, "document-tables-request.json"), (response.json(), "document-tables-response.json")]:
        schema = json.loads((schemas / name).read_text(encoding="utf-8"))
        Draft202012Validator.check_schema(schema)
        Draft202012Validator(schema, format_checker=FormatChecker()).validate(value)


def test_invalid_layout_evidence_fails_without_returning_ocr_text(client, monkeypatch):
    class InvalidAnalyzer:
        def analyze(self, content, *, timeout_seconds):
            return DocumentResult((OcrPage("private text", 10, 10,
                (WordSpan("incorrect", 0, 7, (0, 0, 1, 1), .9),), "test/model", "a" * 64),))

    runtime = app_module.app.state.runtime
    monkeypatch.setattr(app_module.app.state, "runtime", app_module.Runtime(runtime.scanner, runtime.ocr, InvalidAnalyzer()))
    body = request_body()
    body["kind"] = "documents.layout"
    response = submit(client, body)
    assert response.status_code == 503 and "private text" not in response.text


def test_disabled_worker_does_not_check_sandbox_or_load_config(monkeypatch):
    monkeypatch.setenv("OC_DOCUMENT_ENABLED", "false")
    monkeypatch.setattr(app_module, "_sandbox_ready", lambda: pytest.fail("sandbox inspected"))
    assert app_module._load_runtime() is None
