import json
from datetime import datetime, timedelta, timezone

import pytest
from fastapi.testclient import TestClient

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
