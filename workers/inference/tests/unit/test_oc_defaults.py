"""OC_* defaults contract tests for the inference worker.

Covers: OC_WORKER_ENGINE primary honored, legacy alias still works + warns,
invalid OC_WORKER_ENGINE rejected, contributor-tier approval gate (startup +
execute, 503 contributor_approval_required), INFERENCE_CLOUD_URL mapped onto
OC_INFERENCE_API_BASE_URL (OC_ wins), transcription-style model mapping, and
readiness exposure. Existing fake-gate/prod-reject/device tests are unchanged.
"""

import pytest

import openconnect_inference.app as app_module
from openconnect_inference import engine_mode
from openconnect_inference.app import app
from openconnect_inference.config import (
    CONTRIBUTOR_CODE,
    ContributorApprovalError,
    Settings,
    contributor_approved,
    contributor_gate_blocks,
)
from openconnect_inference.engine_mode import (
    EngineModeConfigError,
    parse_provisioning,
    resolve_cache_dir,
    resolve_engine_mode,
)

from fastapi.testclient import TestClient

client = TestClient(app)

REQUEST_ID = "123e4567-e89b-42d3-a456-426614174000"
AUTH = {"Authorization": "Bearer local-test-token"}
CLOUD_URL = "https://llm.internal.example/v1/execute"


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
        "OC_INFERENCE_PROVIDER",
        "OC_INFERENCE_MODEL",
        "INFERENCE_MODEL_ID",
        "OC_INFERENCE_API_BASE_URL",
        "INFERENCE_CLOUD_URL",
        "INFERENCE_CLOUD_API_KEY",
        "OC_CONTRIBUTOR_DATA_USE_APPROVED",
    ):
        monkeypatch.delenv(var, raising=False)


def _settings(**overrides) -> Settings:
    params = {
        "worker_token": "local-test-token",
        "backend": "python-transformers",
        "fake_engine": True,
        "is_production": False,
        "model_device": "auto",
        "cloud_url": None,
        "cloud_api_key": None,
        "route_order": "local-first",
        "cloud_timeout_ms": 5000,
        "privacy_zone": "",
        "model_id": None,
        "model_revision": None,
        "model_dir": "/models",
        "endpoint_url": None,
        "enable_tools": False,
        "enable_structured_output": False,
        "enable_embeddings": False,
        "max_messages": 64,
        "max_input_chars": 32_768,
        "max_output_tokens": 1_024,
        "max_tasks": 1_000,
        "default_lease_ms": 30_000,
    }
    params.update(overrides)
    return Settings(**params)  # type: ignore[arg-type]


def _execute(**input_overrides):
    body = {
        "schemaVersion": "openconnect.v1",
        "requestId": REQUEST_ID,
        "input": {"messages": [{"role": "user", "content": "hello"}]},
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
    monkeypatch.setenv("INFERENCE_CLOUD_API_KEY", "k")
    monkeypatch.setenv("OC_CONTRIBUTOR_DATA_USE_APPROVED", "1")
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
    assert parse_provisioning("disabled") == "disabled"
    with pytest.raises(EngineModeConfigError):
        parse_provisioning("download-always")


def test_provisioning_invalid_rejected_at_startup(monkeypatch) -> None:
    _hermetic(monkeypatch)
    monkeypatch.setenv("INFERENCE_CLOUD_API_KEY", "k")
    monkeypatch.setenv("OC_CONTRIBUTOR_DATA_USE_APPROVED", "1")
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
    monkeypatch.setenv("INFERENCE_CLOUD_API_KEY", "k")
    monkeypatch.setenv("OC_CONTRIBUTOR_DATA_USE_APPROVED", "1")
    monkeypatch.setenv("OC_MODEL_CACHE_DIR", "/cache/models")
    assert Settings.from_environment().model_dir == "/cache/models"


# --- documented OC_ defaults ---------------------------------------------------


def test_oc_defaults_documented(monkeypatch) -> None:
    _hermetic(monkeypatch)
    monkeypatch.setenv("INFERENCE_CLOUD_API_KEY", "secret-mounted-key")
    monkeypatch.setenv("OC_CONTRIBUTOR_DATA_USE_APPROVED", "1")
    settings = Settings.from_environment()
    assert settings.inference_provider == "meta-model-api"
    assert settings.model_id == "muse-spark-1.3-contributor"
    assert settings.cloud_url == "https://api.meta.ai/v1"
    assert settings.route_order == "cloud-first"
    assert settings.provisioning == "first-start-if-missing"
    assert settings.model_dir == "/models"


def test_base_url_mapping_and_precedence(monkeypatch, caplog) -> None:
    _hermetic(monkeypatch)
    monkeypatch.setenv("INFERENCE_CLOUD_API_KEY", "k")
    monkeypatch.setenv("OC_CONTRIBUTOR_DATA_USE_APPROVED", "1")
    # Legacy alias configures the cloud leg (with a deprecation warning).
    monkeypatch.setenv("INFERENCE_CLOUD_URL", CLOUD_URL)
    with caplog.at_level("WARNING"):
        assert Settings.from_environment().cloud_url == CLOUD_URL
    assert "INFERENCE_CLOUD_URL" in caplog.text
    # OC_ wins when both are set.
    monkeypatch.setenv("OC_INFERENCE_API_BASE_URL", "https://oc.example/v1")
    assert Settings.from_environment().cloud_url == "https://oc.example/v1"


def test_model_mapping_and_precedence(monkeypatch) -> None:
    _hermetic(monkeypatch)
    monkeypatch.setenv("INFERENCE_CLOUD_API_KEY", "k")
    monkeypatch.setenv("OC_CONTRIBUTOR_DATA_USE_APPROVED", "1")
    monkeypatch.setenv("OC_INFERENCE_API_BASE_URL", "")
    monkeypatch.setenv("INFERENCE_MODEL_ID", "custom-model")
    assert Settings.from_environment().model_id == "custom-model"
    monkeypatch.setenv("OC_INFERENCE_MODEL", "oc-model")
    assert Settings.from_environment().model_id == "oc-model"


# --- contributor-tier approval gate -------------------------------------------


def test_contributor_gate_exact_approval() -> None:
    assert contributor_gate_blocks("muse-spark-1.3-contributor", {}) is True
    assert (
        contributor_gate_blocks(
            "Muse-Spark-1.3-Contributor", {"OC_CONTRIBUTOR_DATA_USE_APPROVED": "1"}
        )
        is False
    )
    # Approval must be exactly "1": truthy lookalikes do not approve.
    for value in ("0", "yes", "true", "True", " 1 ", "", "2"):
        env = {} if value == "" else {"OC_CONTRIBUTOR_DATA_USE_APPROVED": value}
        assert contributor_gate_blocks("muse-spark-1.3-contributor", env) is True
    assert contributor_approved({"OC_CONTRIBUTOR_DATA_USE_APPROVED": "1"}) is True
    # Non-contributor tiers never trigger the gate.
    assert contributor_gate_blocks("muse-spark-1.3", {}) is False
    assert contributor_gate_blocks(None, {}) is False


def test_contributor_gate_blocks_startup_without_approval(monkeypatch) -> None:
    _hermetic(monkeypatch)
    monkeypatch.setenv("INFERENCE_CLOUD_API_KEY", "k")
    monkeypatch.setenv("OC_INFERENCE_API_BASE_URL", "")
    # Default model is contributor-tier; no approval => fail closed.
    with pytest.raises(ContributorApprovalError):
        Settings.from_environment()


def test_contributor_gate_passes_with_approval(monkeypatch) -> None:
    _hermetic(monkeypatch)
    monkeypatch.setenv("INFERENCE_CLOUD_API_KEY", "k")
    monkeypatch.setenv("OC_INFERENCE_API_BASE_URL", "")
    monkeypatch.setenv("OC_CONTRIBUTOR_DATA_USE_APPROVED", "1")
    assert Settings.from_environment().model_id == "muse-spark-1.3-contributor"


def test_contributor_gate_non_contributor_needs_no_approval(monkeypatch) -> None:
    _hermetic(monkeypatch)
    monkeypatch.setenv("OC_INFERENCE_MODEL", "muse-spark-1.3")
    monkeypatch.setenv("OC_INFERENCE_API_BASE_URL", "")
    assert Settings.from_environment().model_id == "muse-spark-1.3"


def test_contributor_gate_blocks_execute_and_readiness(monkeypatch) -> None:
    monkeypatch.setattr(
        app_module,
        "settings",
        _settings(model_id="muse-spark-1.3-contributor"),
    )
    monkeypatch.delenv("OC_CONTRIBUTOR_DATA_USE_APPROVED", raising=False)
    response = _execute()
    assert response.status_code == 503
    assert response.json()["detail"] == CONTRIBUTOR_CODE
    ready = client.get("/health/ready")
    assert ready.status_code == 503
    assert ready.json()["detail"] == CONTRIBUTOR_CODE


def test_contributor_gate_passes_execute_with_approval(monkeypatch) -> None:
    monkeypatch.setattr(
        app_module,
        "settings",
        _settings(model_id="muse-spark-1.3-contributor"),
    )
    monkeypatch.setenv("OC_CONTRIBUTOR_DATA_USE_APPROVED", "1")
    assert client.get("/health/ready").status_code == 200
    response = _execute()
    assert response.status_code == 200


# --- readiness exposure ---------------------------------------------------------


def test_readiness_exposes_oc_contract(monkeypatch) -> None:
    monkeypatch.setattr(app_module, "settings", _settings())
    monkeypatch.setenv("OC_CONTRIBUTOR_DATA_USE_APPROVED", "1")
    body = client.get("/health/ready").json()
    assert body["status"] == "ready"
    assert body["provisioning"] == "first-start-if-missing"
    assert body["modelCacheDir"] == "/models"
    assert body["inferenceProvider"] == "meta-model-api"
    assert body["inferenceModel"] is None
    assert body["contributorApproved"] is True
