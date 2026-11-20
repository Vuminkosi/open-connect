import json
from datetime import datetime, timedelta, timezone
from pathlib import Path

import pytest
from fastapi.testclient import TestClient
from jsonschema import Draft202012Validator, FormatChecker

from openconnect_knowledge.app import app
from openconnect_worker_common.security.signing import sign_bytes

pytest_plugins = ["test_embeddings"]

KEY = bytes.fromhex("ab" * 32)


@pytest.fixture
def service(cached_model, tmp_path, monkeypatch):
    cache, manifest, revision = cached_model
    config = {"manifest": manifest.model_dump(), "profile": {
        "modelAsset": "model.onnx", "tokenizerAsset": "tokenizer.json",
        "dimension": 3, "maxTokens": 8,
    }}
    config_path = tmp_path / "service.json"
    config_path.write_text(json.dumps(config))
    monkeypatch.setenv("OC_KNOWLEDGE_ENABLED", "true")
    monkeypatch.setenv("OC_KNOWLEDGE_CONFIG_PATH", str(config_path))
    monkeypatch.setenv("OC_MODEL_CACHE_DIR", str(cache.cache_root))
    monkeypatch.setenv("OPENCONNECT_WORKER_TOKEN", "test-token")
    monkeypatch.setenv("OPENCONNECT_WORKER_SIGNING_KEY", KEY.hex())
    with TestClient(app) as client:
        yield client, revision


def payload():
    now = datetime.now(timezone.utc)
    return {"schemaVersion": "openconnect.v1", "taskId": "task-1", "kind": "knowledge.embed",
            "tenantId": "tenant-1", "issuedAt": (now - timedelta(seconds=1)).isoformat(),
            "expiresAt": (now + timedelta(minutes=1)).isoformat(),
            "payload": {"inputKind": "query", "texts": ["tree", "forest"]}}


def signed(client, data):
    body = json.dumps(data).encode()
    return client.post("/internal/v1/execute", content=body, headers={
        "Authorization": "Bearer test-token", "X-OpenConnect-Signature": sign_bytes(body, KEY),
    })


def test_authenticated_real_cpu_execution_and_cache_failure(service):
    client, revision = service
    assert client.get("/health/ready").status_code == 200
    assert client.get("/internal/v1/capabilities").status_code == 401
    response = client.get("/internal/v1/capabilities", headers={"Authorization": "Bearer test-token"})
    assert response.json()["capabilities"][0]["id"] == "knowledge.embed"
    response = signed(client, payload())
    assert response.status_code == 200
    assert len(response.json()["result"]["items"]) == 2
    schema_dir = Path(__file__).resolve().parents[3] / "contracts" / "jsonschema"
    for document, name in [(payload(), "knowledge-embed-request.json"),
                           (response.json(), "knowledge-embed-response.json")]:
        schema = json.loads((schema_dir / name).read_text(encoding="utf-8"))
        Draft202012Validator.check_schema(schema)
        Draft202012Validator(schema, format_checker=FormatChecker()).validate(document)
    result = response.json()["result"]
    assert all(len(item["vector"]) == result["dimension"] for item in result["items"])
    (revision / "assets" / "model.onnx").write_bytes(b"corrupt")
    assert client.get("/health/ready").status_code == 503
    assert signed(client, payload()).status_code == 503


def test_invalid_signature_and_expired_request_fail_closed(service):
    client, _ = service
    body = json.dumps(payload()).encode()
    assert client.post("/internal/v1/execute", content=body, headers={
        "Authorization": "Bearer test-token", "X-OpenConnect-Signature": "0" * 64,
    }).status_code == 401
    request = payload()
    request["expiresAt"] = (datetime.now(timezone.utc) - timedelta(seconds=1)).isoformat()
    assert signed(client, request).status_code == 422


@pytest.mark.parametrize("change", ["unknown", "empty_text", "unknown_kind"])
def test_contract_rejects_invalid_request_in_schema_and_api(service, change):
    client, _ = service
    request = payload()
    if change == "unknown":
        request["payload"]["unknown"] = "private-sentinel"
    elif change == "empty_text":
        request["payload"]["texts"] = [""]
    else:
        request["kind"] = "knowledge.rerank"
    schema_path = (Path(__file__).resolve().parents[3] / "contracts" / "jsonschema"
                   / "knowledge-embed-request.json")
    schema = json.loads(schema_path.read_text(encoding="utf-8"))
    assert list(Draft202012Validator(schema).iter_errors(request))
    response = signed(client, request)
    assert response.status_code == 422
    assert "private-sentinel" not in response.text


def test_disabled_does_not_load_or_advertise_model(monkeypatch, tmp_path):
    monkeypatch.setenv("OC_KNOWLEDGE_ENABLED", "false")
    monkeypatch.setenv("OC_MODEL_CACHE_DIR", str(tmp_path / "absent"))
    monkeypatch.setenv("OPENCONNECT_WORKER_TOKEN", "test-token")
    monkeypatch.setenv("OPENCONNECT_WORKER_SIGNING_KEY", KEY.hex())
    with TestClient(app) as client:
        assert client.get("/health/ready").status_code == 503
        assert client.get("/internal/v1/capabilities", headers={
            "Authorization": "Bearer test-token"}).json() == {"capabilities": []}
    assert not (tmp_path / "absent").exists()


def test_disabled_provisioning_does_not_load_existing_cache(cached_model, monkeypatch):
    cache, _, _ = cached_model
    monkeypatch.setenv("OC_KNOWLEDGE_ENABLED", "true")
    monkeypatch.setenv("OC_MODEL_PROVISIONING", "disabled")
    monkeypatch.setenv("OC_MODEL_CACHE_DIR", str(cache.cache_root))
    monkeypatch.setenv("OPENCONNECT_WORKER_TOKEN", "test-token")
    monkeypatch.setenv("OPENCONNECT_WORKER_SIGNING_KEY", KEY.hex())
    with TestClient(app) as client:
        assert client.get("/health/ready").status_code == 503
        assert client.get("/internal/v1/capabilities", headers={
            "Authorization": "Bearer test-token"}).json() == {"capabilities": []}
