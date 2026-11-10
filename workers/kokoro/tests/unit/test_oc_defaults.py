"""OC_* defaults contract tests for the Kokoro TTS worker.

Covers: OC_WORKER_ENGINE primary honored, legacy OPENCONNECT_FAKE_ENGINE
alias still works (with deprecation warning), OC_ wins, invalid engine
rejected, provisioning/cache-dir defaults and validation, and readiness
exposure. Existing fake-gate/prod-reject/device semantics are unchanged.
"""

import pytest

import openconnect_kokoro.app as app_module
from openconnect_kokoro import engine_mode
from openconnect_kokoro.app import app
from openconnect_kokoro.config import Settings
from openconnect_kokoro.engine_mode import (
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
        assert resolve_engine_mode({"OPENCONNECT_FAKE_ENGINE": "True"}) == "fake"
    assert "OPENCONNECT_FAKE_ENGINE" in caplog.text
    assert resolve_engine_mode({"OPENCONNECT_FAKE_ENGINE": "0"}) == "real"


def test_oc_wins_over_legacy_alias() -> None:
    assert (
        resolve_engine_mode({"OC_WORKER_ENGINE": "fake", "OPENCONNECT_FAKE_ENGINE": "0"}) == "fake"
    )


def test_oc_engine_from_environment(monkeypatch) -> None:
    _hermetic(monkeypatch)
    monkeypatch.setenv("OC_WORKER_ENGINE", "fake")
    assert Settings.from_environment().fake_engine is True


# --- provisioning / cache dir -------------------------------------------------


def test_provisioning_default_and_invalid() -> None:
    assert parse_provisioning(None) == "first-start-if-missing"
    assert parse_provisioning("verify-existing-only") == "verify-existing-only"
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
    monkeypatch.setenv("OC_WORKER_ENGINE", "fake")
    monkeypatch.setenv("OC_MODEL_PROVISIONING", "disabled")
    monkeypatch.setenv("OC_MODEL_CACHE_DIR", "/cache/models")
    settings = Settings.from_environment()
    assert settings.provisioning == "disabled"
    assert settings.model_dir == "/cache/models"


def test_readiness_exposes_provisioning_and_cache_dir(monkeypatch) -> None:
    _hermetic(monkeypatch)
    monkeypatch.setenv("OC_WORKER_ENGINE", "fake")
    monkeypatch.setenv("OPENCONNECT_WORKER_TOKEN", "local-test-token")
    monkeypatch.setattr(app_module, "settings", Settings.from_environment())
    body = client.get("/health/ready").json()
    assert body["status"] == "ready"
    assert body["provisioning"] == "first-start-if-missing"
    assert body["modelCacheDir"] == "/models"
