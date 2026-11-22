import asyncio
import dataclasses
import threading
from types import SimpleNamespace

import pytest
from fastapi.testclient import TestClient
from starlette.websockets import WebSocketDisconnect

import openconnect_transcription.app as app_module
from openconnect_transcription import realtime_local


@pytest.fixture
def local_worker(monkeypatch):
    settings = dataclasses.replace(
        app_module.settings, worker_token="local-token", backend="local-model", fake_engine=False,
        model_device="cpu", cloud_url="https://external.invalid", max_audio_bytes=4800,
        max_duration_ms=100,
    )
    monkeypatch.setattr(app_module, "settings", settings)
    monkeypatch.setattr(realtime_local.engine_mode, "device_info", lambda _: SimpleNamespace(
        gpu_required_missing=False, effective="cpu"))
    monkeypatch.setattr(realtime_local.local_whisper, "load_model", lambda *_: object())
    return TestClient(app_module.app)


def test_final_audio_is_transcribed_locally_even_when_cloud_is_configured(local_worker, monkeypatch):
    seen = []

    def transcribe(_, audio, locale):
        seen.append((audio, locale))
        return SimpleNamespace(transcript="local final")

    monkeypatch.setattr(realtime_local.local_whisper, "transcribe", transcribe)
    with local_worker.websocket_connect(
        "/internal/v1/realtime/local", headers={"Authorization": "Bearer local-token"}
    ) as socket:
        assert socket.receive_json() == {"type": "sessionReady"}
        socket.send_bytes(b"\x00\x00" * 2400)
        socket.send_json({"type": "endStream"})
        assert socket.receive_json() == {"type": "speechComplete", "text": "local final"}
    audio, _ = seen[0]
    assert (audio.sample_rate_hz, audio.channels, audio.bits_per_sample, audio.duration_ms) == (
        24000, 1, 16, 100
    )


def test_local_model_readiness_is_authenticated_and_never_uses_cloud(local_worker, monkeypatch):
    path = "/internal/v1/realtime/local/model-ready"
    assert local_worker.get(path).status_code == 401
    assert local_worker.get(path, headers={"Authorization": "Bearer wrong"}).status_code == 401
    ready = local_worker.get(path, headers={"Authorization": "Bearer local-token"})
    assert ready.status_code == 200
    assert ready.json() == {"modelReady": True}
    monkeypatch.setattr(realtime_local.local_whisper, "load_model", lambda *_: None)
    unavailable = local_worker.get(path, headers={"Authorization": "Bearer local-token"})
    assert unavailable.status_code == 503
    assert unavailable.json() == {"detail": "model_unavailable"}


@pytest.mark.parametrize("overrides", [
    {"backend": "local-fake", "fake_engine": True},
    {"backend": "local-model", "fake_engine": True},
    {"backend": "local-model", "max_audio_bytes": 2},
])
def test_local_model_readiness_rejects_ineligible_configuration(local_worker, monkeypatch, overrides):
    monkeypatch.setattr(app_module, "settings", dataclasses.replace(app_module.settings, **overrides))
    response = local_worker.get(
        "/internal/v1/realtime/local/model-ready", headers={"Authorization": "Bearer local-token"}
    )
    assert response.status_code == 503


@pytest.mark.parametrize("authorization", [None, "local-token", "Bearer wrong"])
def test_local_route_requires_bearer(local_worker, authorization):
    headers = {"Authorization": authorization} if authorization else {}
    with pytest.raises(WebSocketDisconnect) as error:
        with local_worker.websocket_connect("/internal/v1/realtime/local", headers=headers):
            pass
    assert error.value.code == 1008


@pytest.mark.parametrize("path", ["/internal/v1/realtime", "/internal/v1/realtime/local"])
@pytest.mark.parametrize(
    "raw",
    [
        "Bearer t\u00f6k\u00e9n".encode("latin-1"),
        "Bearer t\u00f6k\u00e9n".encode("utf-8"),
        b"Bearer ",
        b"Bearer " + b"x" * 70_000,
    ],
)
def test_non_ascii_empty_or_oversized_bearer_closes_websocket(local_worker, path, raw):
    with pytest.raises(WebSocketDisconnect) as error:
        with local_worker.websocket_connect(path, headers={"Authorization": raw}):
            pass
    assert error.value.code == 1008


def test_unavailable_model_cannot_fall_back_to_cloud(local_worker, monkeypatch):
    monkeypatch.setattr(realtime_local.local_whisper, "load_model", lambda *_: None)
    with local_worker.websocket_connect(
        "/internal/v1/realtime/local", headers={"Authorization": "Bearer local-token"}
    ) as socket:
        with pytest.raises(WebSocketDisconnect) as error:
            socket.receive_json()
    assert error.value.code == 1013


@pytest.mark.parametrize("payload", [b"\x00", b"\x00\x00" * 2401])
def test_invalid_or_oversized_pcm_is_rejected(local_worker, payload):
    with local_worker.websocket_connect(
        "/internal/v1/realtime/local", headers={"Authorization": "Bearer local-token"}
    ) as socket:
        socket.receive_json()
        socket.send_bytes(payload)
        with pytest.raises(WebSocketDisconnect) as error:
            socket.receive_json()
    assert error.value.code == 1003


def test_cancellation_waits_for_inference_thread_before_releasing_capacity():
    started = threading.Event()
    finish = threading.Event()

    def work():
        started.set()
        assert finish.wait(5)

    async def exercise():
        task = asyncio.create_task(realtime_local._run_blocking(work))
        assert await asyncio.to_thread(started.wait, 5)
        task.cancel()
        await asyncio.sleep(0)
        assert not task.done()
        finish.set()
        with pytest.raises(asyncio.CancelledError):
            await task

    asyncio.run(exercise())
