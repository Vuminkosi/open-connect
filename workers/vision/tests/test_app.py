import base64
import json
from datetime import datetime, timedelta, timezone
from pathlib import Path

import pytest
from fastapi.testclient import TestClient
from jsonschema import Draft202012Validator, FormatChecker

from openconnect_vision import app as module
from openconnect_vision.classifier import Classification, ImageClassification
from openconnect_worker_common.security.clamav import MalwareDetectedError, MalwareScanUnavailableError
from openconnect_worker_common.security.signing import sign_bytes

KEY = bytes.fromhex("ab" * 32)


def test_image_payload_representation_hides_content():
    secret = "cHJpdmF0ZS1pbWFnZQ=="
    payload = module.Payload(contentBase64=secret)
    assert secret not in repr(payload) and secret not in str(payload)
    assert payload.model_dump()["contentBase64"] == secret


def test_shared_vision_request_fixtures_match_request_model():
    root = Path(__file__).resolve().parents[3] / "contracts" / "fixtures"
    module.ExecuteRequest.model_validate_json((root / "valid" / "vision-analyze-request.json").read_bytes())
    with pytest.raises(ValueError):
        module.ExecuteRequest.model_validate_json((root / "invalid" / "vision-analyze-request.json").read_bytes())


class Scanner:
    def scan(self, content, *, timeout_seconds):
        if content == b"infected":
            raise MalwareDetectedError("malware_detected")
        if content == b"scanner-down":
            raise MalwareScanUnavailableError("malware_scan_unavailable")


class Classifier:
    def _verify(self):
        pass

    def classify(self, content, *, top_k):
        assert content == b"clean" and top_k == 1
        return ImageClassification("fixture/model", "a" * 64, "fixture-v1", "imagenet-center224-v1",
                                   10, 10, (Classification("fixture-class", 1, 0.8),))


@pytest.fixture
def client(monkeypatch):
    monkeypatch.setenv("OPENCONNECT_WORKER_TOKEN", "test-token")
    monkeypatch.setenv("OPENCONNECT_WORKER_SIGNING_KEY", KEY.hex())
    monkeypatch.setattr(module, "_sandbox_ready", lambda: True)
    monkeypatch.setattr(module, "_load_runtime", lambda: module.Runtime(Scanner(), Classifier()))
    with TestClient(module.app) as instance:
        yield instance


def command(content=b"clean"):
    now = datetime.now(timezone.utc)
    return {"schemaVersion": "openconnect.v1", "taskId": "task-1", "kind": "vision.analyze",
            "tenantId": "01ARZ3NDEKTSV4RRFFQ69G5FAV", "issuedAt": (now - timedelta(seconds=1)).isoformat(),
            "expiresAt": (now + timedelta(minutes=1)).isoformat(),
            "payload": {"contentBase64": base64.b64encode(content).decode(), "topK": 1}}


def submit(client, body, signature=None):
    raw = json.dumps(body).encode()
    return client.post("/internal/v1/execute", content=raw, headers={
        "Authorization": "Bearer test-token", "X-OpenConnect-Signature": signature or sign_bytes(raw, KEY),
    })


def test_signed_execution_capabilities_and_schema(client):
    assert client.get("/health/ready").status_code == 200
    assert client.get("/internal/v1/capabilities").status_code == 401
    caps = client.get("/internal/v1/capabilities", headers={"Authorization": "Bearer test-token"}).json()
    assert [item["id"] for item in caps["capabilities"]] == ["vision.analyze"]
    body = command()
    response = submit(client, body)
    assert response.status_code == 200 and response.json()["result"]["classifications"][0]["label"] == "fixture-class"
    schemas = Path(__file__).resolve().parents[3] / "contracts" / "jsonschema"
    for value, name in [(body, "vision-analyze-request.json"), (response.json(), "vision-analyze-response.json")]:
        schema = json.loads((schemas / name).read_text(encoding="utf-8"))
        Draft202012Validator.check_schema(schema)
        Draft202012Validator(schema, format_checker=FormatChecker()).validate(value)


@pytest.mark.parametrize("field", ["tenantId", "topK"])
def test_signed_request_with_duplicate_json_properties_is_rejected(client, field):
    raw = json.dumps(command()).replace(f'"{field}":', f'"{field}": "conflict", "{field}":', 1).encode()
    response = client.post("/internal/v1/execute", content=raw, headers={
        "Authorization": "Bearer test-token", "X-OpenConnect-Signature": sign_bytes(raw, KEY),
    })
    assert response.status_code == 422


@pytest.mark.parametrize("content,status,code", [(b"infected", 422, "malware_detected"),
                                                 (b"scanner-down", 503, "capability_unavailable")])
def test_scanner_fails_closed_before_inference(client, content, status, code):
    response = submit(client, command(content))
    assert response.status_code == status and response.json()["error"]["code"] == code
    assert content.decode() not in response.text


def test_auth_expiry_and_sandbox_are_enforced(client, monkeypatch):
    assert submit(client, command(), signature="0" * 64).status_code == 401
    expired = command()
    expired["expiresAt"] = (datetime.now(timezone.utc) - timedelta(seconds=1)).isoformat()
    assert submit(client, expired).status_code == 422
    bad = command()
    bad["payload"]["topK"] = True
    assert submit(client, bad).status_code == 422
    bad_tenant = command()
    bad_tenant["tenantId"] += "\n"
    assert submit(client, bad_tenant).status_code == 422
    monkeypatch.setattr(module, "_sandbox_ready", lambda: False)
    assert client.get("/health/ready").status_code == 503
    assert submit(client, command()).status_code == 503


def test_disabled_worker_never_probes_sandbox(monkeypatch):
    monkeypatch.setenv("OC_VISION_ENABLED", "false")
    monkeypatch.setattr(module, "_sandbox_ready", lambda: pytest.fail("sandbox inspected"))
    assert module._load_runtime() is None


def test_busy_worker_rejects_without_processing(client):
    assert module._slot.acquire(blocking=False)
    try:
        assert submit(client, command()).status_code == 429
    finally:
        module._slot.release()


def test_unavailable_scanner_removes_readiness_and_capability(client, monkeypatch):
    def fail(*args, **kwargs):
        raise MalwareScanUnavailableError("malware_scan_unavailable")
    monkeypatch.setattr(module.app.state.runtime.scanner, "scan", fail)
    assert client.get("/health/ready").status_code == 503
    caps = client.get("/internal/v1/capabilities", headers={"Authorization": "Bearer test-token"}).json()
    assert caps == {"capabilities": []}
