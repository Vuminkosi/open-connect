"""Engine-mode contract tests: real-by-default, env-gated fake, device selection."""

import pathlib
import uuid

import pytest

import openconnect_realtime_voice.app as app_module
from openconnect_realtime_voice import engine_mode
from openconnect_realtime_voice.app import app
from openconnect_realtime_voice.config import Settings
from openconnect_realtime_voice.engine_mode import (
    FakeNotAllowedInProductionError,
    device_info,
    fake_requested,
    gpu_allowed,
    is_production,
    parse_device,
    production_fake_active,
)

from fastapi.testclient import TestClient

client = TestClient(app)

AUTH = {"Authorization": "Bearer local-test-token"}


def _settings(**overrides) -> Settings:
    params = {
        "worker_token": "local-test-token",
        "orchestration_only": True,
        "model_dir": "/models",
        "fake_engine": False,
        "is_production": False,
        "model_device": "auto",
    }
    params.update(overrides)
    return Settings(**params)


def _open(session_id: str):
    return client.post(
        "/internal/v1/execute",
        json={
            "schemaVersion": "openconnect.v1",
            "taskId": f"t-open-{session_id[:8]}",
            "kind": "voice.session-open",
            "payload": {"schemaVersion": "openconnect.v1", "sessionId": session_id},
        },
        headers=AUTH,
    )


def _flow_event(session_id: str, event: str, **extra):
    payload = {
        "schemaVersion": "openconnect.v1",
        "sessionId": session_id,
        "type": event,
    }
    payload.update(extra)
    return client.post(
        "/internal/v1/execute",
        json={
            "schemaVersion": "openconnect.v1",
            "taskId": f"t-{event}-{session_id[:8]}",
            "kind": "voice.event",
            "payload": payload,
        },
        headers=AUTH,
    )


def _speak(session_id: str, epoch: int = 0):
    return client.post(
        "/internal/v1/execute",
        json={
            "schemaVersion": "openconnect.v1",
            "taskId": f"t-speak-{session_id[:8]}",
            "kind": "voice.event",
            "payload": {
                "schemaVersion": "openconnect.v1",
                "sessionId": session_id,
                "type": "begin-speaking",
                "speechEpoch": epoch,
                "text": "hello there",
                "voice": "default",
                "language": "en",
            },
        },
        headers=AUTH,
    )


# --- default is real --------------------------------------------------------


def test_default_is_real_without_env(monkeypatch) -> None:
    for var in (
        "OPENCONNECT_FAKE_ENGINE",
        "OPENCONNECT_ENV",
        "OC_MODEL_DEVICE",
        "OC_GPU_ALLOWED",
    ):
        monkeypatch.delenv(var, raising=False)
    settings = Settings.from_environment()
    assert settings.fake_engine is False
    assert settings.is_production is False
    assert settings.model_device == "auto"
    monkeypatch.setattr(app_module, "settings", _settings())
    body = client.get("/health/ready").json()
    # Orchestration needs no weights: ready in real mode, fake chunks withheld.
    assert body["status"] == "ready"
    assert body["engine"] == "real"
    assert "voice.fake-chunks" not in body["capabilities"]
    sid = str(uuid.uuid4())
    assert _open(sid).status_code == 200
    speaking = _speak(sid)
    assert speaking.status_code == 503
    assert speaking.json()["detail"] == "model_unavailable"


# --- explicit fake flag -----------------------------------------------------


@pytest.mark.parametrize("value", ["1", "true", "yes", "True", "YES", " Yes "])
def test_fake_flag_on_values(value: str) -> None:
    assert fake_requested({"OPENCONNECT_FAKE_ENGINE": value}) is True


@pytest.mark.parametrize("value", ["0", "false", "no", "", "2", "on", "off", "maybe", "truthy"])
def test_fake_flag_off_values(value: str) -> None:
    assert fake_requested({"OPENCONNECT_FAKE_ENGINE": value}) is False


def test_fake_flag_absent_is_real() -> None:
    assert fake_requested({}) is False


def test_explicit_fake_on_serves_fake(monkeypatch) -> None:
    monkeypatch.setattr(app_module, "settings", _settings(fake_engine=True))
    body = client.get("/health/ready").json()
    assert body["status"] == "ready"
    assert body["engine"] == "fake-engine"
    assert "voice.fake-chunks" in body["capabilities"]
    capabilities = client.get("/internal/v1/capabilities", headers=AUTH).json()
    assert capabilities["engine"] == "fake-engine"
    sid = str(uuid.uuid4())
    assert _open(sid).status_code == 200
    assert _flow_event(sid, "begin-listening").status_code == 200
    assert _flow_event(sid, "transcript-final", text="go").status_code == 200
    assert _flow_event(sid, "begin-reasoning").status_code == 200
    speaking = _speak(sid)
    assert speaking.status_code == 200
    assert speaking.json()["result"]["chunks"]


# --- production rejects fake -------------------------------------------------


def test_production_value_is_exact() -> None:
    assert is_production({"OPENCONNECT_ENV": "production"}) is True
    assert is_production({"OPENCONNECT_ENV": "Production"}) is False
    assert is_production({"OPENCONNECT_ENV": "PRODUCTION"}) is False
    assert is_production({}) is False


def test_production_rejects_fake_at_startup(monkeypatch) -> None:
    monkeypatch.setenv("OPENCONNECT_ENV", "production")
    monkeypatch.setenv("OPENCONNECT_FAKE_ENGINE", "1")
    with pytest.raises(FakeNotAllowedInProductionError):
        Settings.from_environment()


def test_production_without_fake_starts(monkeypatch) -> None:
    monkeypatch.setenv("OPENCONNECT_ENV", "production")
    monkeypatch.delenv("OPENCONNECT_FAKE_ENGINE", raising=False)
    settings = Settings.from_environment()
    assert settings.is_production is True and settings.fake_engine is False


def test_production_rejects_fake_on_execute(monkeypatch) -> None:
    monkeypatch.setattr(app_module, "settings", _settings(fake_engine=True))
    monkeypatch.setenv("OPENCONNECT_ENV", "production")
    assert production_fake_active(True) is True
    sid = str(uuid.uuid4())
    response = _open(sid)
    assert response.status_code == 503
    assert response.json()["detail"] == "fake_not_allowed_in_production"
    ready = client.get("/health/ready")
    assert ready.status_code == 503
    assert ready.json()["detail"] == "fake_not_allowed_in_production"


# --- device selector ---------------------------------------------------------


def test_device_default_auto_and_invalid() -> None:
    assert parse_device(None) == "auto"
    assert parse_device("") == "auto"
    assert parse_device("tpu") == "auto"
    assert parse_device("CUDA") == "cuda"
    assert parse_device(" cpu ") == "cpu"


def test_cpu_never_touches_gpu() -> None:
    def _boom() -> bool:
        raise AssertionError("GPU detection must not run on the cpu path")

    info = device_info("cpu", detector=_boom)
    assert info.effective == "cpu"
    assert info.gpu_present is None
    assert info.gpu_required_missing is False


def test_cuda_requires_gpu() -> None:
    missing = device_info("cuda", gpu_present=False)
    assert missing.gpu_required_missing is True
    assert missing.effective == "cpu"
    present = device_info("cuda", gpu_present=True)
    assert present.gpu_required_missing is False
    assert present.effective == "cuda"


def test_auto_prefers_gpu_only_when_allowed_and_present() -> None:
    assert device_info("auto", env={"OC_GPU_ALLOWED": "1"}, gpu_present=True).effective == "cuda"
    assert device_info("auto", env={"OC_GPU_ALLOWED": "1"}, gpu_present=False).effective == "cpu"
    assert device_info("auto", env={}, gpu_present=True).effective == "cpu"
    assert gpu_allowed({"OC_GPU_ALLOWED": "yes"}) is True
    assert gpu_allowed({}) is False


def test_cuda_without_gpu_unready_and_execute_rejected(monkeypatch) -> None:
    monkeypatch.setattr(app_module, "settings", _settings(fake_engine=True, model_device="cuda"))
    monkeypatch.setattr(engine_mode, "detect_gpu_present", lambda: False)
    ready = client.get("/health/ready")
    assert ready.status_code == 503
    assert ready.json()["detail"] == "gpu_unavailable"
    sid = str(uuid.uuid4())
    response = _open(sid)
    assert response.status_code == 503
    assert response.json()["detail"] == "gpu_unavailable"


def test_cuda_with_gpu_serves_fake(monkeypatch) -> None:
    monkeypatch.setattr(app_module, "settings", _settings(fake_engine=True, model_device="cuda"))
    monkeypatch.setattr(engine_mode, "detect_gpu_present", lambda: True)
    assert client.get("/health/ready").json()["status"] == "ready"
    sid = str(uuid.uuid4())
    assert _open(sid).status_code == 200


# --- auth + policy parity in fake mode ---------------------------------------


def test_auth_failure_in_fake_mode(monkeypatch) -> None:
    monkeypatch.setattr(app_module, "settings", _settings(fake_engine=True))
    assert client.get("/internal/v1/capabilities").status_code == 401
    sid = str(uuid.uuid4())
    payload = {
        "schemaVersion": "openconnect.v1",
        "taskId": "t-unauth",
        "kind": "voice.session-open",
        "payload": {"schemaVersion": "openconnect.v1", "sessionId": sid},
    }
    assert client.post("/internal/v1/execute", json=payload).status_code == 401
    wrong = client.post(
        "/internal/v1/execute", json=payload, headers={"Authorization": "Bearer wrong"}
    )
    assert wrong.status_code == 401


def test_policy_denial_identical_in_fake_mode(monkeypatch) -> None:
    monkeypatch.setattr(app_module, "settings", _settings(fake_engine=True))
    response = client.post(
        "/internal/v1/execute",
        json={
            "schemaVersion": "openconnect.v1",
            "taskId": "t-unknown-kind",
            "kind": "voice.nope",
            "payload": {},
        },
        headers=AUTH,
    )
    assert response.status_code == 422


def test_image_never_downloads_models() -> None:
    dockerfile = (pathlib.Path(__file__).resolve().parents[2] / "Dockerfile").read_text()
    lowered = dockerfile.lower()
    assert "snapshot_download" not in lowered
    assert "hf_hub_download" not in lowered
    assert "curl" not in lowered and "wget" not in lowered
