import asyncio
from concurrent.futures import ThreadPoolExecutor

import pytest
from fastapi import FastAPI, HTTPException
from fastapi.testclient import TestClient
from test_cloud_execution import SETTINGS, Client, Engine, envelope
from test_worker import settings_for

from app import cloud_api
from app.inference import ensure_model
from app.main import app as worker_app
from app.main import generate_plan
from app.settings import Settings


def test_cloud_route_requires_token_and_consumes_before_return(monkeypatch):
    monkeypatch.setenv("OPENCONNECT_CLOUD_LIVEUI_ENABLED", "true")
    backend = Client()
    backend.ready_deployment = lambda: "11111111-1111-1111-1111-111111111111"
    monkeypatch.setattr(cloud_api, "configured_client", lambda: backend)
    app = FastAPI()
    app.state.settings = SETTINGS
    app.state.settings.bearer_token = "fixture-token"
    app.state.settings.request_timeout_seconds = 5
    app.state.engine = Engine()
    app.state.ready = True
    app.state.slots = asyncio.Semaphore(1)
    app.state.executor = ThreadPoolExecutor(max_workers=1)
    app.include_router(cloud_api.router)
    try:
        client = TestClient(app)
        headers = {"Authorization": "Bearer fixture-token"}
        assert client.get("/internal/cloud/v1/liveui/ready").status_code == 401
        assert client.get("/internal/cloud/v1/liveui/ready", headers=headers).json()["status"] == "ready"
        assert (
            client.post(
                "/internal/cloud/v1/liveui/execute", headers={"Authorization": "Bearer wrong"}, json=envelope()
            ).status_code
            == 401
        )
        response = client.post("/internal/cloud/v1/liveui/execute", headers=headers, json=envelope())
        assert response.status_code == 200
        assert response.headers["cache-control"] == "no-store"
        assert backend.calls == ["consume", "settle"]
        monkeypatch.setenv("OPENCONNECT_CLOUD_LIVEUI_ENABLED", "invalid")
        assert cloud_api.cloud_mode_blocks_legacy()
        assert client.post("/internal/cloud/v1/liveui/execute", headers=headers, json=envelope()).status_code == 503
    finally:
        app.state.executor.shutdown(wait=True)


@pytest.mark.asyncio
async def test_cloud_mode_never_downloads_a_missing_model(monkeypatch, tmp_path):
    monkeypatch.setenv("OPENCONNECT_CLOUD_LIVEUI_ENABLED", "true")
    path = tmp_path / "missing" / "model.gguf"
    with pytest.raises(RuntimeError, match="not provisioned"):
        await ensure_model(settings_for(path, "a" * 64))
    assert not path.parent.exists()


def test_cloud_mode_requires_model_approvals(monkeypatch, tmp_path):
    monkeypatch.setenv("OPENCONNECT_CLOUD_LIVEUI_ENABLED", "true")
    settings = settings_for(tmp_path / "model.gguf", "a" * 64)
    with pytest.raises(ValueError, match="approved"):
        settings.validate()
    monkeypatch.setenv("OPENCONNECT_CLOUD_MODEL_LICENSE_ACCEPTED", "true")
    monkeypatch.setenv("OPENCONNECT_CLOUD_MODEL_ARTIFACT_REVIEWED", "true")
    settings.validate()


def test_cloud_worker_token_file_is_bounded_and_conflicts_fail_closed(monkeypatch, tmp_path):
    path = tmp_path / "worker-token"
    path.write_text("fixture-token\n", encoding="ascii")
    monkeypatch.setenv("OPENCONNECT_WORKER_TOKEN_FILE", str(path))
    monkeypatch.delenv("OPENCONNECT_WORKER_TOKEN", raising=False)
    assert Settings.from_environment().bearer_token == "fixture-token"
    monkeypatch.setenv("OPENCONNECT_WORKER_TOKEN", "conflict")
    assert Settings.from_environment().bearer_token == ""
    monkeypatch.delenv("OPENCONNECT_WORKER_TOKEN")
    path.write_bytes(b"x" * 513)
    assert Settings.from_environment().bearer_token == ""


@pytest.mark.asyncio
async def test_legacy_route_is_disabled_even_for_invalid_cloud_mode(monkeypatch):
    for mode in ("true", "invalid"):
        monkeypatch.setenv("OPENCONNECT_CLOUD_LIVEUI_ENABLED", mode)
        with pytest.raises(HTTPException) as error:
            await generate_plan(None, None)
        assert error.value.status_code == 503


def test_cloud_mode_blocks_legacy_before_validation(monkeypatch):
    monkeypatch.setenv("OPENCONNECT_CLOUD_LIVEUI_ENABLED", "true")
    client = TestClient(worker_app)
    response = client.post("/v1/liveui/inference/plan", json={"intent": "sensitive-fixture"})
    assert response.status_code == 503
    assert "sensitive-fixture" not in response.text


def test_cloud_route_rejects_non_ascii_bearer(monkeypatch):
    monkeypatch.setenv("OPENCONNECT_CLOUD_LIVEUI_ENABLED", "true")
    app = FastAPI()
    app.state.settings = SETTINGS
    app.state.settings.bearer_token = "fixture-token"
    app.include_router(cloud_api.router)
    client = TestClient(app)
    for raw in ("Bearer t\u00f6k\u00e9n".encode("latin-1"), "Bearer t\u00f6k\u00e9n".encode(), b"Bearer "):
        response = client.get("/internal/cloud/v1/liveui/ready", headers={"Authorization": raw})
        assert response.status_code == 401
