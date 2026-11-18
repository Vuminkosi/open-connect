"""OC_* defaults contract tests for the realtime-voice worker.

Covers: OC_WORKER_ENGINE primary honored, legacy OPENCONNECT_FAKE_ENGINE
alias still works (with deprecation warning), OC_ wins, invalid engine
rejected, OC_TRANSCRIPTION_MODE default/validation, provisioning/cache-dir
defaults, and readiness exposure. Existing fake-gate/prod-reject/device
semantics are unchanged.
"""

import pytest

import openconnect_realtime_voice.app as app_module
from openconnect_realtime_voice import engine_mode
from openconnect_realtime_voice.app import app
from openconnect_realtime_voice.config import (
    Settings,
    parse_transcription_mode,
)
from openconnect_realtime_voice.engine_mode import (
    EngineModeConfigError,
    parse_provisioning,
    resolve_cache_dir,
    resolve_engine_mode,
)

from fastapi.testclient import TestClient

client = TestClient(app)


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
        "OC_TRANSCRIPTION_MODE",
    ):
        monkeypatch.delenv(var, raising=False)


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


# --- transcription mode ---------------------------------------------------------


def test_transcription_mode_default_and_validation() -> None:
    assert parse_transcription_mode(None) == "ENDPOINTING"
    assert parse_transcription_mode("") == "ENDPOINTING"
    assert parse_transcription_mode("diarization") == "DIARIZATION"
    assert parse_transcription_mode("PUSH_TO_TALK") == "PUSH_TO_TALK"
    with pytest.raises(EngineModeConfigError):
        parse_transcription_mode("DIALOG")
    with pytest.raises(EngineModeConfigError):
        parse_transcription_mode("bogus")


def test_transcription_mode_invalid_rejected_at_startup(monkeypatch) -> None:
    _hermetic(monkeypatch)
    monkeypatch.setenv("OC_TRANSCRIPTION_MODE", "DIALOG")
    with pytest.raises(EngineModeConfigError):
        Settings.from_environment()


# --- provisioning / cache dir -------------------------------------------------


def test_provisioning_default_and_invalid() -> None:
    assert parse_provisioning(None) == "first-start-if-missing"
    assert parse_provisioning("disabled") == "disabled"
    with pytest.raises(EngineModeConfigError):
        parse_provisioning("download-always")


def test_cache_dir_defaults_and_precedence(caplog) -> None:
    assert resolve_cache_dir(None, None) == "/models"
    assert resolve_cache_dir("/data/models", None) == "/data/models"
    with caplog.at_level("WARNING"):
        assert resolve_cache_dir(None, "/legacy/models") == "/legacy/models"
    assert "MODEL_DIR" in caplog.text
    assert resolve_cache_dir("/oc/models", "/legacy/models") == "/oc/models"


def test_settings_exposes_new_fields(monkeypatch) -> None:
    _hermetic(monkeypatch)
    monkeypatch.setenv("OC_TRANSCRIPTION_MODE", "DIARIZATION")
    monkeypatch.setenv("OC_MODEL_CACHE_DIR", "/cache/models")
    settings = Settings.from_environment()
    assert settings.transcription_mode == "DIARIZATION"
    assert settings.provisioning == "first-start-if-missing"
    assert settings.model_dir == "/cache/models"


def test_readiness_exposes_oc_contract(monkeypatch) -> None:
    _hermetic(monkeypatch)
    monkeypatch.setenv("OC_WORKER_ENGINE", "fake")
    monkeypatch.setenv("OPENCONNECT_WORKER_TOKEN", "local-test-token")
    monkeypatch.setattr(app_module, "settings", Settings.from_environment())
    body = client.get("/health/ready").json()
    assert body["status"] == "ready"
    assert body["provisioning"] == "first-start-if-missing"
    assert body["modelCacheDir"] == "/models"
    assert body["transcriptionMode"] == "ENDPOINTING"
