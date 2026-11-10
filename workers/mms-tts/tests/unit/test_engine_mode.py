"""Engine-mode contract tests: real-by-default, env-gated fake, device selection."""

import base64
import pathlib

import pytest

import openconnect_mms_tts.app as app_module
from openconnect_mms_tts import engine_mode
from openconnect_mms_tts.app import app
from openconnect_mms_tts.config import Settings
from openconnect_mms_tts.engine_mode import (
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
    base = {
        "worker_token": "local-test-token",
        "model_dir": "/models",
        "fake_engine": False,
        "is_production": False,
        "model_device": "auto",
        "languages": ("eng",),
        "commercial_use": False,
        "cache_size": 2,
    }
    base.update(overrides)
    return Settings(**base)


def _synth(language: str = "eng", text: str = "hello world"):
    return client.post(
        "/internal/v1/execute",
        json={
            "schemaVersion": "openconnect.v1",
            "taskId": "t-synth",
            "kind": "mms.synthesize",
            "payload": {
                "schemaVersion": "openconnect.v1",
                "text": text,
                "language": language,
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
    ready = client.get("/health/ready")
    assert ready.status_code == 503
    assert ready.json()["detail"] == "model_unavailable"
    response = _synth()
    assert response.status_code == 503
    assert response.json()["detail"] == "model_unavailable"


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
    assert body["optInLanguages"] == ["eng"]
    capabilities = client.get("/internal/v1/capabilities", headers=AUTH).json()
    assert capabilities["engine"] == "fake-engine"
    response = _synth()
    assert response.status_code == 200
    result = response.json()["result"]
    assert result["language"] == "eng"
    assert base64.b64decode(result["audioBase64"])


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
    response = _synth()
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
    response = _synth()
    assert response.status_code == 503
    assert response.json()["detail"] == "gpu_unavailable"


def test_cuda_with_gpu_serves_fake(monkeypatch) -> None:
    monkeypatch.setattr(app_module, "settings", _settings(fake_engine=True, model_device="cuda"))
    monkeypatch.setattr(engine_mode, "detect_gpu_present", lambda: True)
    assert client.get("/health/ready").json()["status"] == "ready"
    assert _synth().status_code == 200


# --- auth + policy parity in fake mode ---------------------------------------


def test_auth_failure_in_fake_mode(monkeypatch) -> None:
    monkeypatch.setattr(app_module, "settings", _settings(fake_engine=True))
    assert client.get("/internal/v1/capabilities").status_code == 401
    body = {
        "schemaVersion": "openconnect.v1",
        "taskId": "t-synth",
        "kind": "mms.synthesize",
        "payload": {"schemaVersion": "openconnect.v1", "text": "hi", "language": "eng"},
    }
    assert client.post("/internal/v1/execute", json=body).status_code == 401
    wrong = client.post(
        "/internal/v1/execute", json=body, headers={"Authorization": "Bearer wrong"}
    )
    assert wrong.status_code == 401


def test_policy_denial_identical_in_fake_mode(monkeypatch) -> None:
    monkeypatch.setattr(app_module, "settings", _settings(fake_engine=True, commercial_use=True))
    response = _synth()
    assert response.status_code == 403
    assert "commercial" in response.json()["detail"]


def test_image_never_downloads_models() -> None:
    dockerfile = (pathlib.Path(__file__).resolve().parents[2] / "Dockerfile").read_text()
    lowered = dockerfile.lower()
    assert "snapshot_download" not in lowered
    assert "hf_hub_download" not in lowered
    assert "curl" not in lowered and "wget" not in lowered
