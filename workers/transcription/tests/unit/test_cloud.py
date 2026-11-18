"""Cloud-with-local-backup tests (mocked transports only, no real credentials)."""

import base64
import logging
import struct
import urllib.error

import pytest

import openconnect_transcription.app as app_module
from openconnect_transcription import cloud as cloud_module
from openconnect_transcription.app import app
from openconnect_transcription.cloud import (
    CloudConfigError,
    CloudLegFailed,
    privacy_zone_strict,
    resolve_route_order,
    validate_cloud_url,
)
from openconnect_transcription.config import Settings

from fastapi.testclient import TestClient

client = TestClient(app)

REQUEST_ID = "123e4567-e89b-42d3-a456-426614174000"
AUTH = {"Authorization": "Bearer local-test-token"}
CLOUD_URL = "https://stt.internal.example/v1/execute"


def make_wav(frames: int = 1600) -> str:
    frame_bytes = 2  # mono 16-bit
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
        "fake_engine": False,
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


def _cloud_settings(**overrides):
    params = {
        "cloud_url": CLOUD_URL,
        "cloud_api_key": "secret-mounted-key",
        "route_order": "cloud-first",
        # The cloud leg requires an exact provider model ID (never guessed).
        "model_id": "e2e-test-transcriber",
    }
    params.update(overrides)
    return _settings(**params)


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


def _ok_cloud(monkeypatch, transcript="cloud transcript"):
    def _post(**kwargs):
        return {"transcript": transcript}

    monkeypatch.setattr(cloud_module, "post_json", _post)


# --- defaults ----------------------------------------------------------------


def test_cloud_unconfigured_when_urls_empty(monkeypatch) -> None:
    # Renamed contract: OC_TRANSCRIPTION_FILE_URL is primary,
    # TRANSCRIPTION_CLOUD_URL is the deprecated alias; either set (non-empty)
    # configures the cloud leg. Explicitly empty both for local-only.
    for var in (
        "TRANSCRIPTION_CLOUD_URL",
        "TRANSCRIPTION_CLOUD_API_KEY",
        "TRANSCRIPTION_ROUTE_ORDER",
        "OC_TRANSCRIPTION_FILE_URL",
        "OC_TRANSCRIPTION_MODEL",
        "OC_TRANSCRIPTION_PROVIDER",
        "OC_TRANSCRIPTION_MODE",
        "OC_MODEL_CACHE_DIR",
        "MODEL_DIR",
        "OC_MODEL_PROVISIONING",
    ):
        monkeypatch.delenv(var, raising=False)
    monkeypatch.setenv("OC_TRANSCRIPTION_FILE_URL", "")
    settings = Settings.from_environment()
    assert settings.cloud_url is None
    assert settings.cloud_api_key is None
    assert settings.route_order == "local-first"
    assert settings.model_id is None
    assert settings.transcription_mode == "ENDPOINTING"
    monkeypatch.setattr(app_module, "settings", _settings())
    ready = client.get("/health/ready")
    assert ready.status_code == 503
    assert ready.json()["detail"] == "model_unavailable"
    monkeypatch.setattr(app_module, "settings", _settings(fake_engine=True))
    body = client.get("/health/ready").json()
    assert body["routeOrder"] == "local-first"
    assert body["cloudConfigured"] is False
    assert body["cloudAvailable"] is False


# --- happy path -----------------------------------------------------------------


def test_cloud_first_happy_path_mocked_transport(monkeypatch) -> None:
    monkeypatch.setattr(app_module, "settings", _cloud_settings())
    _ok_cloud(monkeypatch)
    response = _execute()
    assert response.status_code == 200
    assert response.json()["transcript"] == "cloud transcript"
    body = client.get("/health/ready").json()
    assert body["status"] == "ready"
    assert body["cloudAvailable"] is True


def test_local_first_routes_to_cloud_after_local_fails(monkeypatch) -> None:
    monkeypatch.setattr(app_module, "settings", _cloud_settings(route_order="local-first"))
    _ok_cloud(monkeypatch, transcript="via cloud second")
    response = _execute()
    assert response.status_code == 200
    assert response.json()["transcript"] == "via cloud second"


def test_provisioned_local_model_serves_without_cloud(monkeypatch, tmp_path) -> None:
    (tmp_path / "model-manifest.json").write_text('{"modelId":"stt","revision":"r1"}')
    monkeypatch.setattr(
        app_module,
        "settings",
        _settings(backend="local-model", model_dir=str(tmp_path)),
    )
    body = client.get("/health/ready").json()
    assert body["status"] == "ready"
    capabilities = client.get("/internal/v1/capabilities", headers=AUTH).json()
    assert capabilities["engine"] == "real"
    response = _execute()
    assert response.status_code == 200
    assert response.json()["isFinal"] is True


# --- fallback --------------------------------------------------------------------


def test_cloud_down_falls_back_to_provisioned_local(monkeypatch, tmp_path) -> None:
    (tmp_path / "model-manifest.json").write_text('{"modelId":"stt","revision":"r1"}')
    monkeypatch.setattr(
        app_module,
        "settings",
        _cloud_settings(backend="local-model", model_dir=str(tmp_path)),
    )

    def _down(**kwargs):
        raise CloudLegFailed("unreachable")

    monkeypatch.setattr(cloud_module, "post_json", _down)
    response = _execute()
    assert response.status_code == 200
    assert response.json()["isFinal"] is True


def test_cloud_down_without_local_is_typed_unavailable(monkeypatch) -> None:
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


# --- privacy ---------------------------------------------------------------------


def test_strict_zone_blocks_cloud_with_privacy_blocked(monkeypatch) -> None:
    monkeypatch.setattr(app_module, "settings", _cloud_settings(privacy_zone="strict-local"))
    monkeypatch.setenv("OC_PRIVACY_ZONE", "strict-local")
    _ok_cloud(monkeypatch)
    response = _execute()
    assert response.status_code == 503
    assert response.json()["detail"] == "privacy_blocked"
    assert privacy_zone_strict() is True


def test_strict_zone_local_model_still_serves(monkeypatch, tmp_path) -> None:
    (tmp_path / "model-manifest.json").write_text('{"modelId":"stt","revision":"r1"}')
    monkeypatch.setattr(
        app_module,
        "settings",
        _cloud_settings(
            backend="local-model",
            model_dir=str(tmp_path),
            route_order="local-first",
            privacy_zone="strict-local",
        ),
    )
    monkeypatch.setenv("OC_PRIVACY_ZONE", "strict-local")
    response = _execute()
    assert response.status_code == 200


# --- startup validation ------------------------------------------------------------


def test_missing_key_with_cloud_url_fails_startup(monkeypatch) -> None:
    monkeypatch.setenv("TRANSCRIPTION_CLOUD_URL", CLOUD_URL)
    monkeypatch.delenv("TRANSCRIPTION_CLOUD_API_KEY", raising=False)
    with pytest.raises(CloudConfigError):
        Settings.from_environment()


@pytest.mark.parametrize(
    "url",
    [
        "http://stt.internal.example/v1",
        "https://localhost/v1",
        "https://127.0.0.1/v1",
        "https://169.254.169.254/latest",
        "https://user:pass@stt.internal.example/v1",
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
        "https://stt.internal.example/v1/execute",
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


# --- secret redaction --------------------------------------------------------------


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


def test_key_never_in_readiness_or_capabilities(monkeypatch) -> None:
    monkeypatch.setattr(app_module, "settings", _cloud_settings())
    ready_text = client.get("/health/ready").text
    assert "secret-mounted-key" not in ready_text
    caps = client.get("/internal/v1/capabilities", headers=AUTH)
    assert "secret-mounted-key" not in caps.text
