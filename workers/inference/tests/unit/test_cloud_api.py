import base64
import json
from uuid import uuid4

from fastapi import FastAPI
from fastapi.testclient import TestClient

from openconnect_inference import cloud_api


class Prepared:
    input_tokens = 3
    def generate(self, maximum):
        assert maximum == 4
        return "result", 2


class Runtime:
    model_id = "registry/model-a"
    model_revision = "revision-1"
    model_digest = "sha256:" + "a" * 64
    def prepare(self, payload):
        return Prepared()


class Client:
    def __init__(self):
        self.calls = []
    def ready_deployment(self):
        return "11111111-1111-1111-1111-111111111111"
    def consume(self, reservation, claim, payload, key):
        self.calls.append("consume")
        return {"status": "consumed", "reservedTokens": 7, "modelId": "registry/model-a", "modelRevision": "revision-1", "modelDigest": "sha256:" + "a" * 64}
    def settle(self, reservation, claim, input_tokens, output_tokens):
        self.calls.append("settle")
        return True


def body():
    payload = {"schemaVersion": "openconnect.cloud.inference.worker/v1", "requestId": str(uuid4()),
               "modelId": "registry/model-a", "input": {"messages": [{"role": "user", "content": "fixture"}],
               "maxTokens": 4, "stream": False}}
    return {"schemaVersion": "openconnect.cloud.worker.inference/v1", "reservationId": str(uuid4()),
            "claimId": str(uuid4()), "payload": base64.b64encode(json.dumps(payload).encode()).decode(),
            "key": base64.b64encode(bytes(32)).decode()}


def test_route_requires_enabled_runtime_worker_token_and_settlement(monkeypatch):
    monkeypatch.setenv("OPENCONNECT_CLOUD_INFERENCE_ENABLED", "true")
    monkeypatch.setenv("OPENCONNECT_WORKER_TOKEN", "test-worker-token")
    backend = Client()
    monkeypatch.setattr(cloud_api, "configured_client", lambda: backend)
    app = FastAPI()
    app.include_router(cloud_api.router)
    client = TestClient(app)
    headers = {"Authorization": "Bearer test-worker-token"}
    assert client.get("/internal/cloud/v1/inference/ready").status_code == 401
    assert client.post("/internal/cloud/v1/inference/execute", headers=headers, json=body()).status_code == 503
    app.state.cloud_inference_runtime = Runtime()
    assert client.get("/internal/cloud/v1/inference/ready", headers=headers).json()["status"] == "ready"
    assert client.post("/internal/cloud/v1/inference/execute", headers={"Authorization": "Bearer wrong"}, json=body()).status_code == 401
    response = client.post("/internal/cloud/v1/inference/execute", headers=headers, json=body())
    assert response.status_code == 200
    assert response.headers["cache-control"] == "no-store"
    assert response.json()["usage"] == {"inputTokens": 3, "outputTokens": 2}
    assert backend.calls == ["consume", "settle"]
    monkeypatch.setenv("OPENCONNECT_CLOUD_INFERENCE_ENABLED", "invalid")
    assert cloud_api.cloud_mode_blocks_legacy()
    assert client.post("/internal/cloud/v1/inference/execute", headers=headers, json=body()).status_code == 503


def test_cloud_mode_blocks_legacy_execution_even_when_flag_is_invalid(monkeypatch):
    from fastapi import HTTPException
    from openconnect_inference import app as app_module
    from openconnect_inference.schemas import ExecuteRequest
    import pytest

    request = ExecuteRequest.model_validate({"schemaVersion": "openconnect.v1", "requestId": str(uuid4()),
        "input": {"messages": [{"role": "user", "content": "fixture"}]}})
    for flag in ("true", "invalid"):
        monkeypatch.setenv("OPENCONNECT_CLOUD_INFERENCE_ENABLED", flag)
        with pytest.raises(HTTPException) as error:
            app_module.execute(request)
        assert error.value.status_code == 503


def test_worker_token_file_authentication_fails_closed(monkeypatch, tmp_path):
    monkeypatch.setenv("OPENCONNECT_CLOUD_INFERENCE_ENABLED", "true")
    monkeypatch.delenv("OPENCONNECT_WORKER_TOKEN", raising=False)
    token_file = tmp_path / "worker-token"
    token_file.write_bytes(b"file-token\n")
    monkeypatch.setenv("OPENCONNECT_WORKER_TOKEN_FILE", str(token_file))
    monkeypatch.setattr(cloud_api, "configured_client", lambda: Client())
    app = FastAPI()
    app.state.cloud_inference_runtime = Runtime()
    app.include_router(cloud_api.router)
    client = TestClient(app)
    assert client.get("/internal/cloud/v1/inference/ready", headers={"Authorization": "Bearer file-token"}).status_code == 200
    assert client.get("/internal/cloud/v1/inference/ready", headers={"Authorization": "Bearer wrong"}).status_code == 401
    monkeypatch.setenv("OPENCONNECT_WORKER_TOKEN", "another-token")
    assert client.get("/internal/cloud/v1/inference/ready", headers={"Authorization": "Bearer file-token"}).status_code == 401
    monkeypatch.delenv("OPENCONNECT_WORKER_TOKEN")
    for invalid in (b"", b"token with spaces", b"x" * 513):
        token_file.write_bytes(invalid)
        assert client.get("/internal/cloud/v1/inference/ready", headers={"Authorization": "Bearer file-token"}).status_code == 401
    token_file.unlink()
    assert client.get("/internal/cloud/v1/inference/ready", headers={"Authorization": "Bearer file-token"}).status_code == 401


def test_cloud_route_rejects_non_ascii_bearer_and_accepts_non_ascii_token(monkeypatch):
    monkeypatch.setenv("OPENCONNECT_CLOUD_INFERENCE_ENABLED", "true")
    monkeypatch.setenv("OPENCONNECT_WORKER_TOKEN", "test-worker-token")
    app = FastAPI()
    app.include_router(cloud_api.router)
    app.state.cloud_inference_runtime = Runtime()
    client = TestClient(app)
    url = "/internal/cloud/v1/inference/ready"
    for raw in ("Bearer t\u00f6k\u00e9n".encode("latin-1"), "Bearer t\u00f6k\u00e9n".encode(), b"Bearer "):
        assert client.get(url, headers={"Authorization": raw}).status_code == 401
