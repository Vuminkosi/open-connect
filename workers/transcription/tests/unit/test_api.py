"""API tests for the transcription worker (health, auth, execute, tasks)."""

import base64
import struct
import uuid

import pytest
from fastapi.testclient import TestClient

from openconnect_transcription import app as app_module
from openconnect_transcription.app import app
from openconnect_transcription.config import Settings

client = TestClient(app)

REQUEST_ID = "123e4567-e89b-42d3-a456-426614174000"


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
        "fake_engine": True,
        "is_production": False,
        "model_device": "auto",
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


@pytest.fixture(autouse=True)
def _configured(monkeypatch) -> None:
    monkeypatch.setattr(app_module, "settings", _settings())
    app_module._tasks.clear()
    app_module._idempotency.clear()


def _auth() -> dict[str, str]:
    return {"Authorization": "Bearer local-test-token"}


def _execute(**overrides) -> dict:
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
    body["input"].update(overrides)
    return body


def test_live_and_ready_are_distinct() -> None:
    assert client.get("/health/live").json() == {"status": "live"}
    body = client.get("/health/ready").json()
    assert body["status"] == "ready"
    assert body["capabilities"] == ["transcribe-final", "transcribe-recorded"]


def test_protected_routes_require_auth() -> None:
    assert client.get("/internal/v1/capabilities").status_code == 401
    assert client.post("/internal/v1/execute", json=_execute()).status_code == 401
    assert client.post("/internal/v1/tasks/claim", json={}).status_code == 401


def test_missing_token_fails_closed() -> None:
    app_module.settings = _settings(worker_token=None)
    response = client.get("/internal/v1/capabilities", headers=_auth())
    assert response.status_code == 503
    assert response.json()["detail"] == "worker unavailable"


def test_capabilities_report_engine_and_privacy() -> None:
    body = client.get("/internal/v1/capabilities", headers=_auth()).json()
    assert body["worker"] == "transcription"
    assert body["engine"] == "fake-bounded"
    assert body["privacyMode"] == "strict-local"
    assert body["remoteBlocked"] is False
    assert body["supportsPartials"] is False
    assert 16_000 in body["sampleRatesHz"]


def test_execute_final_transcript_with_revision() -> None:
    response = client.post("/internal/v1/execute", json=_execute(), headers=_auth())
    assert response.status_code == 200
    body = response.json()
    assert body["isFinal"] is True
    assert body["revision"] == 1
    assert body["durationMs"] == 100
    assert body["locale"] == "en"
    assert len(body["segments"]) == 1
    assert body["segments"][0]["startMs"] == 0
    assert body["segments"][0]["endMs"] == 100
    # Deterministic for identical audio.
    again = client.post("/internal/v1/execute", json=_execute(), headers=_auth())
    assert again.json()["transcript"] == body["transcript"]


def test_execute_rejects_bad_wav() -> None:
    body = _execute(audioBase64=base64.b64encode(b"junk" * 32).decode("ascii"))
    response = client.post("/internal/v1/execute", json=body, headers=_auth())
    assert response.status_code == 400
    assert response.json()["detail"] == "invalid_request"


def test_execute_rejects_codec_mismatch() -> None:
    body = _execute(mimeType="audio/L16", sampleRateHz=8_000)
    response = client.post("/internal/v1/execute", json=body, headers=_auth())
    assert response.status_code == 400


def test_partials_only_when_supported() -> None:
    body = _execute(partialsRequested=True)
    response = client.post("/internal/v1/execute", json=body, headers=_auth())
    assert response.status_code == 503
    assert response.json()["detail"] == "capability_unavailable"


def test_strict_local_blocks_remote_stt() -> None:
    app_module.settings = _settings(backend="remote-endpoint")
    caps = client.get("/internal/v1/capabilities", headers=_auth()).json()
    assert caps["remoteBlocked"] is True
    response = client.post("/internal/v1/execute", json=_execute(), headers=_auth())
    assert response.status_code == 403
    assert response.json()["detail"] == "privacy_blocked"


def test_local_model_without_manifest_unavailable(tmp_path, monkeypatch) -> None:
    monkeypatch.setattr(
        app_module, "settings", _settings(backend="local-model", model_dir=str(tmp_path))
    )
    assert client.get("/health/ready").status_code == 503
    response = client.post("/internal/v1/execute", json=_execute(), headers=_auth())
    assert response.status_code == 503
    assert response.json()["detail"] == "model_unavailable"


def test_task_lifecycle_claim_complete_fail() -> None:
    claim = client.post("/internal/v1/tasks/claim", json={}, headers=_auth()).json()
    complete = client.post(
        f"/internal/v1/tasks/{claim['taskId']}/complete",
        json={"leaseId": claim["leaseId"]},
        headers=_auth(),
    )
    assert complete.json()["state"] == "completed"
    # Terminal-once.
    assert (
        client.post(
            f"/internal/v1/tasks/{claim['taskId']}/fail",
            json={"leaseId": claim["leaseId"], "error": "late"},
            headers=_auth(),
        ).status_code
        == 409
    )
    other = client.post("/internal/v1/tasks/claim", json={}, headers=_auth()).json()
    heartbeat = client.post(
        f"/internal/v1/tasks/{other['taskId']}/heartbeat",
        json={"leaseId": other["leaseId"]},
        headers=_auth(),
    )
    assert heartbeat.status_code == 200
    fail = client.post(
        f"/internal/v1/tasks/{other['taskId']}/fail",
        json={"leaseId": other["leaseId"], "error": "decode-error"},
        headers=_auth(),
    )
    assert fail.json()["state"] == "failed"
    assert (
        client.post(
            f"/internal/v1/tasks/{uuid.uuid4()}/heartbeat",
            json={"leaseId": str(uuid.uuid4())},
            headers=_auth(),
        ).status_code
        == 404
    )
