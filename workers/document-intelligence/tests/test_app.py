import base64
import json
from datetime import datetime, timedelta, timezone
from pathlib import Path

import pytest
from fastapi.testclient import TestClient
from jsonschema import Draft202012Validator, FormatChecker

from openconnect_document_intelligence import app as app_module
from openconnect_document_intelligence.malware import MalwareDetectedError, MalwareScanUnavailableError
from openconnect_document_intelligence.ocr import OcrPage, WordSpan
from openconnect_document_intelligence.pipeline import DocumentResult
from openconnect_worker_common.security.signing import sign_bytes

KEY = bytes.fromhex("ab" * 32)


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


def test_private_signed_execution_and_capability(client):
    assert client.get("/health/ready").status_code == 200
    assert client.get("/internal/v1/capabilities").status_code == 401
    capabilities = client.get("/internal/v1/capabilities", headers={"Authorization": "Bearer test-token"})
    assert capabilities.json()["capabilities"][0]["id"] == "documents.ocr"
    response = submit(client, request_body())
    assert response.status_code == 200 and response.json()["result"]["pages"][0]["text"] == "redacted result"
    schemas = Path(__file__).resolve().parents[3] / "contracts" / "jsonschema"
    for value, name in [(request_body(), "document-ocr-request.json"),
                        (response.json(), "document-ocr-response.json")]:
        schema = json.loads((schemas / name).read_text(encoding="utf-8"))
        Draft202012Validator.check_schema(schema)
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
    assert submit(client, incomplete).status_code == 422


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


def test_disabled_worker_does_not_check_sandbox_or_load_config(monkeypatch):
    monkeypatch.setenv("OC_DOCUMENT_ENABLED", "false")
    monkeypatch.setattr(app_module, "_sandbox_ready", lambda: pytest.fail("sandbox inspected"))
    assert app_module._load_runtime() is None
