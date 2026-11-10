"""Cloud-with-local-backup tests (mocked transports only, no real credentials)."""

import logging
import urllib.error

import pytest

import openconnect_inference.app as app_module
from openconnect_inference import cloud as cloud_module
from openconnect_inference.app import app
from openconnect_inference.cloud import (
    CloudConfigError,
    CloudLegFailed,
    privacy_zone_strict,
    resolve_route_order,
    validate_cloud_url,
)
from openconnect_inference.config import Settings

from fastapi.testclient import TestClient

client = TestClient(app)

REQUEST_ID = "123e4567-e89b-42d3-a456-426614174000"
AUTH = {"Authorization": "Bearer local-test-token"}
CLOUD_URL = "https://llm.internal.example/v1/execute"


def _settings(**overrides) -> Settings:
    params = {
        "worker_token": "local-test-token",
        "backend": "python-transformers",
        "fake_engine": False,
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


def _cloud_settings(**overrides):
    params = {
        "cloud_url": CLOUD_URL,
        "cloud_api_key": "secret-mounted-key",
        "route_order": "cloud-first",
    }
    params.update(overrides)
    return _settings(**params)


def _execute(**input_overrides):
    body = {
        "schemaVersion": "openconnect.v1",
        "requestId": REQUEST_ID,
        "input": {"messages": [{"role": "user", "content": "hello"}]},
    }
    body["input"].update(input_overrides)
    return client.post("/internal/v1/execute", json=body, headers=AUTH)


def _ok_cloud(monkeypatch, output="cloud says hi", **kwargs):
    def _post(**call_kwargs):
        return {"output": output, **kwargs}

    monkeypatch.setattr(cloud_module, "post_json", _post)


# --- defaults ----------------------------------------------------------------


def test_cloud_disabled_by_default(monkeypatch) -> None:
    for var in ("INFERENCE_CLOUD_URL", "INFERENCE_CLOUD_API_KEY", "INFERENCE_ROUTE_ORDER"):
        monkeypatch.delenv(var, raising=False)
    settings = Settings.from_environment()
    assert settings.cloud_url is None
    assert settings.cloud_api_key is None
    assert settings.route_order == "local-first"
    monkeypatch.setattr(app_module, "settings", _settings())
    ready = client.get("/health/ready")
    assert ready.status_code == 503
    assert ready.json()["detail"] == "model_unavailable"
    # Routing fields ride the ready payload: fake-mode readiness reflects them.
    monkeypatch.setattr(app_module, "settings", _settings(fake_engine=True))
    body = client.get("/health/ready").json()
    assert body["routeOrder"] == "local-first"
    assert body["cloudConfigured"] is False
    assert body["cloudAvailable"] is False
    assert body["localAvailable"] is False


# --- happy path ----------------------------------------------------------------


def test_cloud_first_happy_path_mocked_transport(monkeypatch) -> None:
    monkeypatch.setattr(app_module, "settings", _cloud_settings())
    _ok_cloud(monkeypatch)
    response = _execute()
    assert response.status_code == 200
    assert response.json()["output"] == "cloud says hi"
    body = client.get("/health/ready").json()
    assert body["status"] == "ready"
    assert body["cloudAvailable"] is True


def test_local_first_routes_to_cloud_after_local_fails(monkeypatch) -> None:
    monkeypatch.setattr(app_module, "settings", _cloud_settings(route_order="local-first"))
    _ok_cloud(monkeypatch, output="via cloud second")
    response = _execute()
    assert response.status_code == 200
    assert response.json()["output"] == "via cloud second"


def test_local_success_skips_cloud(monkeypatch) -> None:
    monkeypatch.setattr(app_module, "settings", _cloud_settings(route_order="local-first"))
    monkeypatch.setattr(app_module, "_try_local_real", lambda cfg: "local says hi")

    def _boom(**kwargs):
        raise AssertionError("cloud must not be called when local serves")

    monkeypatch.setattr(cloud_module, "post_json", _boom)
    response = _execute()
    assert response.status_code == 200
    assert response.json()["output"] == "local says hi"


# --- fallback ------------------------------------------------------------------


def test_cloud_down_falls_back_to_typed_unavailable(monkeypatch) -> None:
    monkeypatch.setattr(app_module, "settings", _cloud_settings())

    def _down(**kwargs):
        raise CloudLegFailed("unreachable")

    monkeypatch.setattr(cloud_module, "post_json", _down)
    response = _execute()
    assert response.status_code == 503
    assert response.json()["detail"] == "model_unavailable"


def test_cloud_500_retries_then_falls_back(monkeypatch) -> None:
    monkeypatch.setattr(app_module, "settings", _cloud_settings())
    calls = []

    def _flaky(request, timeout=None):
        calls.append(request)
        raise urllib.error.HTTPError(
            CLOUD_URL,
            500,
            "Internal Error",
            {},
            None,  # type: ignore[arg-type]
        )

    monkeypatch.setattr("urllib.request.urlopen", _flaky)
    response = _execute()
    assert response.status_code == 503
    assert response.json()["detail"] == "model_unavailable"
    assert len(calls) == cloud_module.CLOUD_MAX_ATTEMPTS


def test_stream_with_cloud_only_is_typed_unavailable(monkeypatch) -> None:
    monkeypatch.setattr(app_module, "settings", _cloud_settings())
    _ok_cloud(monkeypatch)
    response = _execute(stream=True)
    assert response.status_code == 503
    assert response.json()["detail"] == "model_unavailable"


# --- privacy -------------------------------------------------------------------


def test_strict_zone_blocks_cloud_with_privacy_blocked(monkeypatch) -> None:
    monkeypatch.setattr(app_module, "settings", _cloud_settings(privacy_zone="strict-local"))
    monkeypatch.setenv("OC_PRIVACY_ZONE", "strict-local")
    _ok_cloud(monkeypatch)
    response = _execute()
    assert response.status_code == 503
    assert response.json()["detail"] == "privacy_blocked"
    assert privacy_zone_strict() is True


def test_strict_zone_without_cloud_stays_local_only(monkeypatch) -> None:
    monkeypatch.setattr(app_module, "settings", _settings(privacy_zone="strict-local"))
    monkeypatch.setenv("OC_PRIVACY_ZONE", "strict-local")
    response = _execute()
    assert response.status_code == 503
    assert response.json()["detail"] == "model_unavailable"


# --- startup validation ----------------------------------------------------------


def test_missing_key_with_cloud_url_fails_startup(monkeypatch) -> None:
    monkeypatch.setenv("INFERENCE_CLOUD_URL", CLOUD_URL)
    monkeypatch.delenv("INFERENCE_CLOUD_API_KEY", raising=False)
    with pytest.raises(CloudConfigError):
        Settings.from_environment()


@pytest.mark.parametrize(
    "url",
    [
        "http://llm.internal.example/v1",
        "https://localhost/v1",
        "https://127.0.0.1/v1",
        "https://169.254.169.254/latest",
        "https://user:pass@llm.internal.example/v1",
        "not-a-url",
        "",
    ],
)
def test_bad_cloud_urls_rejected(url: str) -> None:
    if url == "":
        assert validate_cloud_url(url) is None
    else:
        with pytest.raises(CloudConfigError):
            validate_cloud_url(url)


@pytest.mark.parametrize(
    "url",
    [
        "https://llm.internal.example/v1/execute",
        "https://10.20.30.40/v1/execute",
        "https://[fd00::1]/v1/execute",
    ],
)
def test_good_cloud_urls_accepted(url: str) -> None:
    assert validate_cloud_url(url) == url


def test_invalid_route_order_falls_back_to_local_first() -> None:
    assert resolve_route_order("bogus", cloud_configured=True) == cloud_module.LOCAL_FIRST
    assert resolve_route_order(None, cloud_configured=True) == cloud_module.CLOUD_FIRST
    assert resolve_route_order(None, cloud_configured=False) == cloud_module.LOCAL_FIRST


# --- secret redaction ------------------------------------------------------------


def test_secrets_redacted_in_errors_and_logs(monkeypatch, caplog) -> None:
    monkeypatch.setattr(app_module, "settings", _cloud_settings())

    def _boom(request, timeout=None):
        raise urllib.error.URLError("connection refused")

    monkeypatch.setattr("urllib.request.urlopen", _boom)
    with caplog.at_level(logging.INFO):
        response = _execute()
    assert response.status_code == 503
    assert "secret-mounted-key" not in response.text
    assert CLOUD_URL not in response.text
    assert caplog.records, "expected cloud attempt logs"
    for record in caplog.records:
        assert "secret-mounted-key" not in record.getMessage()
        assert "hello" not in record.getMessage()


def test_key_never_in_readiness_or_capabilities(monkeypatch) -> None:
    monkeypatch.setattr(app_module, "settings", _cloud_settings())
    ready_text = client.get("/health/ready").text
    assert "secret-mounted-key" not in ready_text
    caps = client.get("/internal/v1/capabilities", headers=AUTH)
    assert "secret-mounted-key" not in caps.text
