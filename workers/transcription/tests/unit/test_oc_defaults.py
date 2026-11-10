"""OC_* defaults contract tests for the transcription worker.

Covers: OC_WORKER_ENGINE primary honored, legacy alias still works + warns,
invalid OC_WORKER_ENGINE rejected, OC_TRANSCRIPTION_MODE default/validation,
file-URL mapping (OC_ wins), OC_TRANSCRIPTION_MODEL default empty with the
cloud path unready until set, provisioning/cache-dir defaults, and readiness
exposure. Existing fake-gate/prod-reject/device tests are unchanged.
"""

import base64
import struct

import pytest

import openconnect_transcription.app as app_module
from openconnect_transcription import cloud as cloud_module
from openconnect_transcription import engine_mode
from openconnect_transcription.app import app
from openconnect_transcription.config import (
    Settings,
    parse_transcription_mode,
)
from openconnect_transcription.engine_mode import (
    EngineModeConfigError,
    parse_provisioning,
    resolve_cache_dir,
    resolve_engine_mode,
)

from fastapi.testclient import TestClient

client = TestClient(app)

REQUEST_ID = "123e4567-e89b-42d3-a456-426614174000"
AUTH = {"Authorization": "Bearer local-test-token"}
CLOUD_URL = "https://stt.internal.example/v1/execute"


@pytest.fixture(autouse=True)
def _reset_deprecation_warnings():
    engine_mode._legacy_warned.clear()
    yield
    engine_mode._legacy_warned.clear()


def _hermetic(monkeypatch) -> None:
    for var in (
        "OC_WORKER_ENGINE",
        "OPENCONNECT_FAKE_ENGINE",
        "OPENCONNECT_ENV",
        "OC_MODEL_DEVICE",
        "OC_GPU_ALLOWED",
        "OC_MODEL_PROVISIONING",
        "OC_MODEL_CACHE_DIR",
        "MODEL_DIR",
        "OC_TRANSCRIPTION_PROVIDER",
        "OC_TRANSCRIPTION_MODE",
        "OC_TRANSCRIPTION_REALTIME_URL",
        "OC_TRANSCRIPTION_FILE_URL",
        "TRANSCRIPTION_CLOUD_URL",
        "TRANSCRIPTION_CLOUD_API_KEY",
        "OC_TRANSCRIPTION_MODEL",
        "TRANSCRIPTION_MODEL_ID",
    ):
        monkeypatch.delenv(var, raising=False)


def make_wav(frames: int = 1600) -> str:
    frame_bytes = 2
    pcm = (b"\x01\x02" * (frames * frame_bytes // 2 + 1))[: frames * frame_bytes]
    header = struct.pack(
        "<4sI4s4sIHHIIHH4sI",
        b"RIFF",
        36 + len(pcm),
        b"WAVE",
        b"fmt ",
        16,
        1,
        1,
        16_000,
        16_000 * frame_bytes,
        frame_bytes,
        16,
        b"data",
        len(pcm),
    )
    return base64.b64encode(header + pcm).decode("ascii")


def _settings(**overrides) -> Settings:
    params = {
        "worker_token": "local-test-token",
        "backend": "local-fake",
        "fake_engine": True,
        "is_production": False,
        "model_device": "auto",
        "cloud_url": None,
        "cloud_api_key": None,
        "route_order": "local-first",
        "cloud_timeout_ms": 5000,
        "privacy_zone": "",
        "privacy_mode": "strict-local",
        "model_id": None,
        "model_revision": None,
        "model_dir": "/models",
        "endpoint_url": None,
        "max_audio_bytes": 5_242_880,
        "max_duration_ms": 60_000,
        "max_tasks": 1_000,
        "default_lease_ms": 30_000,
        "default_locale": "en",
    }
    params.update(overrides)
    return Settings(**params)  # type: ignore[arg-type]


def _execute(**input_overrides):
    body = {
        "schemaVersion": "openconnect.v1",
        "requestId": REQUEST_ID,
        "input": {
            "audioBase64": make_wav(),
            "mimeType": "audio/wav",
            "sampleRateHz": 16_000,
            "channels": 1,
            "bitsPerSample": 16,
            "durationMs": 100,
            "locale": "en",
        },
    }
    body["input"].update(input_overrides)
    return client.post("/internal/v1/execute", json=body, headers=AUTH)


# --- OC_WORKER_ENGINE --------------------------------------------------------


def test_oc_engine_primary_honored() -> None:
    assert resolve_engine_mode({"OC_WORKER_ENGINE": "fake"}) == "fake"
    assert resolve_engine_mode({"OC_WORKER_ENGINE": " real "}) == "real"
    assert resolve_engine_mode({}) == "real"


def test_oc_engine_invalid_rejected() -> None:
    with pytest.raises(EngineModeConfigError):
        resolve_engine_mode({"OC_WORKER_ENGINE": "sometimes"})


def test_oc_engine_invalid_rejected_at_startup(monkeypatch) -> None:
    _hermetic(monkeypatch)
    monkeypatch.setenv("OC_WORKER_ENGINE", "bogus")
    monkeypatch.setenv("TRANSCRIPTION_CLOUD_API_KEY", "k")
    with pytest.raises(EngineModeConfigError):
        Settings.from_environment()


def test_legacy_fake_alias_still_works_and_warns(caplog) -> None:
    with caplog.at_level("WARNING"):
        assert resolve_engine_mode({"OPENCONNECT_FAKE_ENGINE": "1"}) == "fake"
    assert "OPENCONNECT_FAKE_ENGINE" in caplog.text
    assert resolve_engine_mode({"OPENCONNECT_FAKE_ENGINE": "off"}) == "real"


def test_oc_wins_over_legacy_alias() -> None:
    assert (
        resolve_engine_mode({"OC_WORKER_ENGINE": "real", "OPENCONNECT_FAKE_ENGINE": "1"}) == "real"
    )


# --- provisioning / cache dir -------------------------------------------------


def test_provisioning_default_and_invalid() -> None:
    assert parse_provisioning(None) == "first-start-if-missing"
    assert parse_provisioning("") == "first-start-if-missing"
    assert parse_provisioning("verify-existing-only") == "verify-existing-only"
    with pytest.raises(EngineModeConfigError):
        parse_provisioning("download-always")


def test_provisioning_invalid_rejected_at_startup(monkeypatch) -> None:
    _hermetic(monkeypatch)
    monkeypatch.setenv("TRANSCRIPTION_CLOUD_API_KEY", "k")
    monkeypatch.setenv("OC_MODEL_PROVISIONING", "bogus")
    with pytest.raises(EngineModeConfigError):
        Settings.from_environment()


def test_cache_dir_defaults_and_precedence(monkeypatch, caplog) -> None:
    assert resolve_cache_dir(None, None) == "/models"
    assert resolve_cache_dir("/data/models", None) == "/data/models"
    with caplog.at_level("WARNING"):
        assert resolve_cache_dir(None, "/legacy/models") == "/legacy/models"
    assert "MODEL_DIR" in caplog.text
    assert resolve_cache_dir("/oc/models", "/legacy/models") == "/oc/models"
    _hermetic(monkeypatch)
    monkeypatch.setenv("TRANSCRIPTION_CLOUD_API_KEY", "k")
    monkeypatch.setenv("OC_MODEL_CACHE_DIR", "/cache/models")
    assert Settings.from_environment().model_dir == "/cache/models"


# --- documented OC_ defaults ---------------------------------------------------


def test_oc_defaults_documented(monkeypatch) -> None:
    _hermetic(monkeypatch)
    monkeypatch.setenv("TRANSCRIPTION_CLOUD_API_KEY", "secret-mounted-key")
    settings = Settings.from_environment()
    assert settings.transcription_provider == "meta-model-api"
    assert settings.transcription_mode == "ENDPOINTING"
    assert settings.transcription_realtime_url == "wss://api.meta.ai/v1/asr/realtime"
    assert settings.transcription_file_url == "https://api.meta.ai/v1/asr/transcribe"
    assert settings.cloud_url == "https://api.meta.ai/v1/asr/transcribe"
    # Model default is EMPTY: never guessed.
    assert settings.model_id is None
    assert settings.provisioning == "first-start-if-missing"
    assert settings.model_dir == "/models"


def test_file_url_mapping_and_precedence(monkeypatch, caplog) -> None:
    _hermetic(monkeypatch)
    monkeypatch.setenv("TRANSCRIPTION_CLOUD_API_KEY", "k")
    # Legacy alias configures the cloud leg (with a deprecation warning).
    monkeypatch.setenv("TRANSCRIPTION_CLOUD_URL", CLOUD_URL)
    with caplog.at_level("WARNING"):
        assert Settings.from_environment().cloud_url == CLOUD_URL
    assert "TRANSCRIPTION_CLOUD_URL" in caplog.text
    # OC_ wins when both are set.
    monkeypatch.setenv("OC_TRANSCRIPTION_FILE_URL", "https://oc.example/v1/file")
    assert Settings.from_environment().cloud_url == "https://oc.example/v1/file"


def test_model_mapping_and_precedence(monkeypatch) -> None:
    _hermetic(monkeypatch)
    monkeypatch.setenv("TRANSCRIPTION_CLOUD_API_KEY", "k")
    monkeypatch.setenv("TRANSCRIPTION_MODEL_ID", "legacy-model")
    assert Settings.from_environment().model_id == "legacy-model"
    monkeypatch.setenv("OC_TRANSCRIPTION_MODEL", "oc-model")
    assert Settings.from_environment().model_id == "oc-model"


# --- transcription mode ---------------------------------------------------------


def test_transcription_mode_default_and_validation() -> None:
    assert parse_transcription_mode(None) == "ENDPOINTING"
    assert parse_transcription_mode("") == "ENDPOINTING"
    assert parse_transcription_mode("endpointing") == "ENDPOINTING"
    assert parse_transcription_mode(" diarization ") == "DIARIZATION"
    assert parse_transcription_mode("PUSH_TO_TALK") == "PUSH_TO_TALK"
    with pytest.raises(EngineModeConfigError):
        parse_transcription_mode("DIALOG")
    with pytest.raises(EngineModeConfigError):
        parse_transcription_mode("bogus")


def test_transcription_mode_invalid_rejected_at_startup(monkeypatch) -> None:
    _hermetic(monkeypatch)
    monkeypatch.setenv("TRANSCRIPTION_CLOUD_API_KEY", "k")
    monkeypatch.setenv("OC_TRANSCRIPTION_MODE", "DIALOG")
    with pytest.raises(EngineModeConfigError):
        Settings.from_environment()


# --- cloud unready without OC_TRANSCRIPTION_MODEL --------------------------------


def test_cloud_unready_without_model(monkeypatch) -> None:
    _hermetic(monkeypatch)
    monkeypatch.setenv("TRANSCRIPTION_CLOUD_API_KEY", "secret-mounted-key")
    settings = Settings.from_environment()
    assert settings.cloud_url is not None and settings.model_id is None
    monkeypatch.setattr(
        app_module,
        "settings",
        _settings(
            backend="local-fake",
            fake_engine=False,
            cloud_url=settings.cloud_url,
            cloud_api_key="secret-mounted-key",
            route_order="cloud-first",
            model_id=None,
        ),
    )
    ready = client.get("/health/ready")
    assert ready.status_code == 503
    assert ready.json()["detail"] == "model_unavailable"
    response = _execute()
    assert response.status_code == 503
    assert response.json()["detail"] == "model_unavailable"


def test_cloud_ready_once_model_set(monkeypatch) -> None:
    monkeypatch.setattr(
        app_module,
        "settings",
        _settings(
            backend="local-fake",
            fake_engine=False,
            cloud_url=CLOUD_URL,
            cloud_api_key="secret-mounted-key",
            route_order="cloud-first",
            model_id="muse-voice-transcribe-1",
        ),
    )

    def _post(**kwargs):
        return {"transcript": "cloud transcript"}

    monkeypatch.setattr(cloud_module, "post_json", _post)
    body = client.get("/health/ready").json()
    assert body["status"] == "ready"
    assert body["cloudAvailable"] is True
    response = _execute()
    assert response.status_code == 200
    assert response.json()["transcript"] == "cloud transcript"


# --- readiness exposure ---------------------------------------------------------


def test_readiness_exposes_oc_contract(monkeypatch) -> None:
    monkeypatch.setattr(app_module, "settings", _settings())
    body = client.get("/health/ready").json()
    assert body["status"] == "ready"
    assert body["provisioning"] == "first-start-if-missing"
    assert body["modelCacheDir"] == "/models"
    assert body["transcriptionProvider"] == "meta-model-api"
    assert body["transcriptionMode"] == "ENDPOINTING"
    assert body["transcriptionModel"] is None
