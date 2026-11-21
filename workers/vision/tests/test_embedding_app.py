import base64
import json
from datetime import datetime, timedelta, timezone
from pathlib import Path

import pytest
from fastapi.testclient import TestClient
from jsonschema import Draft202012Validator, FormatChecker

from openconnect_vision import embedding_app as module
from openconnect_vision.embeddings import ClipEmbedding
from openconnect_worker_common.security.clamav import MalwareDetectedError, MalwareScanUnavailableError
from openconnect_worker_common.security.signing import sign_bytes

KEY = bytes.fromhex("ab" * 32)


@pytest.mark.parametrize("payload,secret", [
    (module.ImagePayload(modality="image", contentBase64="cHJpdmF0ZQ=="), "cHJpdmF0ZQ=="),
    (module.TextPayload(modality="text", text="private-customer-query"), "private-customer-query"),
])
def test_embedding_payload_representation_hides_content(payload, secret):
    assert secret not in repr(payload) and secret not in str(payload)
    assert secret in payload.model_dump().values()


def test_shared_embedding_request_fixtures_match_request_model():
    root = Path(__file__).resolve().parents[3] / "contracts" / "fixtures"
    module.ExecuteRequest.model_validate_json((root / "valid" / "vision-embed-request.json").read_bytes())
    with pytest.raises(ValueError):
        module.ExecuteRequest.model_validate_json((root / "invalid" / "vision-embed-request.json").read_bytes())


class Scanner:
    def scan(self, content, *, timeout_seconds):
        if content == b"infected":
            raise MalwareDetectedError("malware_detected")
        if content == b"scanner-down":
            raise MalwareScanUnavailableError("malware_scan_unavailable")


class Encoder:
    def _verify(self): pass

    def embed_image(self, content):
        assert content == b"clean"
        return self.embed_text("fixture")

    def embed_text(self, text):
        return ClipEmbedding((1.0,) + (0.0,) * 511, "fixture", "a" * 64, "b" * 64)


@pytest.fixture
def client(monkeypatch):
    monkeypatch.setenv("OPENCONNECT_WORKER_TOKEN", "test-token")
    monkeypatch.setenv("OPENCONNECT_WORKER_SIGNING_KEY", KEY.hex())
    monkeypatch.setattr(module, "restricted_media_sandbox_ready", lambda: True)
    monkeypatch.setattr(module, "_load_runtime", lambda: module.Runtime(Scanner(), Encoder()))
    with TestClient(module.app) as instance:
        yield instance


def command(payload):
    now = datetime.now(timezone.utc)
    return {"schemaVersion": "openconnect.v1", "taskId": "task", "kind": "vision.embed",
            "tenantId": "01ARZ3NDEKTSV4RRFFQ69G5FAV", "issuedAt": (now - timedelta(seconds=1)).isoformat(),
            "expiresAt": (now + timedelta(minutes=1)).isoformat(), "payload": payload}


def submit(client, request, signature=None):
    raw = json.dumps(request).encode()
    return client.post("/internal/v1/execute", content=raw, headers={
        "Authorization": "Bearer test-token", "X-OpenConnect-Signature": signature or sign_bytes(raw, KEY)})


def test_signed_text_and_scanned_image_share_space(client):
    text_command = command({"modality": "text", "text": "red image"})
    image_command = command({"modality": "image", "contentBase64": base64.b64encode(b"clean").decode()})
    text = submit(client, text_command)
    image = submit(client, image_command)
    assert text.status_code == image.status_code == 200
    assert text.json()["result"]["space_id"] == image.json()["result"]["space_id"] == "b" * 64
    assert len(image.json()["result"]["vector"]) == 512
    schemas = Path(__file__).resolve().parents[3] / "contracts" / "jsonschema"
    for value, name in [(text_command, "vision-embed-request.json"), (image_command, "vision-embed-request.json"),
                        (image.json(), "vision-embed-response.json"), (text.json(), "vision-embed-response.json")]:
        schema = json.loads((schemas / name).read_text(encoding="utf-8"))
        Draft202012Validator.check_schema(schema)
        Draft202012Validator(schema, format_checker=FormatChecker()).validate(value)
    caps = client.get("/internal/v1/capabilities", headers={"Authorization": "Bearer test-token"}).json()
    assert [item["id"] for item in caps["capabilities"]] == ["vision.embed"]


@pytest.mark.parametrize("field", ["tenantId", "modality"])
def test_signed_request_with_duplicate_json_properties_is_rejected(client, field):
    raw = json.dumps(command({"modality": "text", "text": "private"})).replace(
        f'"{field}":', f'"{field}": "conflict", "{field}":', 1).encode()
    response = client.post("/internal/v1/execute", content=raw, headers={
        "Authorization": "Bearer test-token", "X-OpenConnect-Signature": sign_bytes(raw, KEY),
    })
    assert response.status_code == 422


@pytest.mark.parametrize("content,status,code", [(b"infected", 422, "malware_detected"),
                                                 (b"scanner-down", 503, "capability_unavailable")])
def test_scanner_fails_closed(client, content, status, code):
    response = submit(client, command({"modality": "image", "contentBase64": base64.b64encode(content).decode()}))
    assert response.status_code == status and response.json()["error"]["code"] == code


def test_auth_bounds_and_sandbox_fail_closed(client, monkeypatch):
    request = command({"modality": "text", "text": "private"})
    assert submit(client, request, signature="0" * 64).status_code == 401
    request["payload"]["extra"] = "sensitive"
    assert submit(client, request).status_code == 422
    monkeypatch.setattr(module, "restricted_media_sandbox_ready", lambda: False)
    assert client.get("/health/ready").status_code == 503
    assert submit(client, command({"modality": "text", "text": "private"})).status_code == 503


def test_disabled_embedding_never_loads_model(monkeypatch):
    monkeypatch.setenv("OC_VISION_EMBED_ENABLED", "false")
    monkeypatch.setattr(module, "restricted_media_sandbox_ready", lambda: pytest.fail("sandbox inspected"))
    assert module._load_runtime() is None
