from __future__ import annotations

import base64
from dataclasses import dataclass
from pathlib import Path
import threading
import time

from fastapi.testclient import TestClient
import numpy as np
import pytest
from starlette.testclient import WebSocketDenialResponse

import openconnect_kokoro.app as app_module
from openconnect_kokoro.config import DEFAULT_VOICE_ID, VERIFIED_VOICES, Settings
from openconnect_kokoro.engine import (
    KokoroEngine,
    MAX_SAMPLES,
    ModelUnavailable,
    NATIVE_SAMPLE_RATE_HZ,
    OverloadError,
    VoiceUnknown,
)

AUTH = {"Authorization": "Bearer local-test-token"}


@dataclass
class _FakeResult:
    audio: np.ndarray | None


def _configure_auth(monkeypatch, engine: KokoroEngine | None = None) -> None:
    monkeypatch.setattr(
        app_module,
        "settings",
        Settings(
            worker_token="local-test-token",
            model_dir="/models",
            model_device="cpu",
            voices=("af_heart",),
            default_voice="af_heart",
        ),
    )
    monkeypatch.setattr(app_module, "configured_client", lambda: None)
    if engine is not None:
        monkeypatch.setattr(app_module, "synth_engine", engine)


def _make_engine(
    tmp_path: Path,
    pipeline,
    *,
    max_pending_streams: int = 4,
    inference_wait_timeout_seconds: float = 5.0,
) -> KokoroEngine:
    engine = KokoroEngine(
        str(tmp_path),
        max_pending_streams=max_pending_streams,
        inference_wait_timeout_seconds=inference_wait_timeout_seconds,
    )
    voice_path = tmp_path / "voices" / "af_heart.pt"
    voice_path.parent.mkdir(parents=True, exist_ok=True)
    voice_path.write_bytes(b"fake-voice")
    engine._pipeline = pipeline
    engine._voice_file = voice_path
    engine._voice_files = {DEFAULT_VOICE_ID: voice_path}
    return engine


def test_verified_voice_registry_defaults_and_rejections(tmp_path: Path) -> None:
    entry = VERIFIED_VOICES["af_heart"]
    assert entry.voice_id == "af_heart"
    assert entry.locale == "en-US"
    assert entry.default_speed == pytest.approx(0.95)
    assert entry.min_speed == pytest.approx(0.7)
    assert entry.max_speed == pytest.approx(1.4)
    assert len(entry.sha256) == 64

    engine = _make_engine(tmp_path, lambda text, voice, speed: iter([]))

    for bad_voice in ("bf_emma", "../af_heart", "/etc/passwd", "voices/af_heart.pt", "af_heart.pt", ""):
        with pytest.raises(VoiceUnknown):
            engine.validate_stream_request("hello", voice_id=bad_voice, locale="en-US", speed=0.95)

    for bad_speed in (0.69, 1.41, 0.0, -1.0, float("nan"), float("inf"), True):
        with pytest.raises(ValueError, match="speed"):
            engine.validate_stream_request("hello", voice_id="af_heart", locale="en-US", speed=bad_speed)  # type: ignore[arg-type]

    for bad_locale in ("fr-FR", "", "   ", "../en-US"):
        with pytest.raises(ValueError, match="locale"):
            engine.validate_stream_request("hello", voice_id="af_heart", locale=bad_locale, speed=0.95)

    for bad_text in (
        "",
        "   ",
        "a" * 2001,
        "<speak>hello</speak>",
        "<script>alert(1)</script>",
        "hello <unclosed",
        "hello\x00world",
    ):
        with pytest.raises(ValueError):
            engine.validate_stream_request(bad_text, voice_id="af_heart", locale="en-US", speed=0.95)

    voice, locale, speed = engine.validate_stream_request(
        "3 < 5 and 5 > 3", voice_id="af_heart", locale="en-us", speed=1.2
    )
    assert voice.voice_id == "af_heart"
    assert locale == "en-US"
    assert speed == pytest.approx(1.2)


def test_engine_incremental_streaming_emits_before_completion_and_no_cache(tmp_path: Path) -> None:
    allow_second = threading.Event()
    second_started = threading.Event()
    seen_calls: list[tuple[str, str, float]] = []

    def fake_pipeline(text: str, voice: str, speed: float):
        seen_calls.append((text, voice, speed))
        yield _FakeResult(np.full(480, 0.25, dtype=np.float32))
        second_started.set()
        assert allow_second.wait(timeout=3.0), "test should release second chunk"
        yield _FakeResult(np.full(480, 0.5, dtype=np.float32))

    engine = _make_engine(tmp_path, fake_pipeline)
    stream = engine.synthesize_stream("hello streaming world", voice_id="af_heart", locale="en-US", speed=1.1)

    first_chunk = next(stream)
    assert len(first_chunk) == 480 * 2
    assert not allow_second.is_set()

    allow_second.set()
    remaining = list(stream)
    assert len(remaining) == 1
    assert len(remaining[0]) == 480 * 2
    assert second_started.is_set()
    assert len(seen_calls) == 1
    assert seen_calls[0][2] == pytest.approx(1.1)
    assert len(engine._idempotency) == 0


def test_engine_mid_stream_cancellation_and_pre_cancel(tmp_path: Path) -> None:
    calls = 0
    allow_second = threading.Event()

    def fake_pipeline(text: str, voice: str, speed: float):
        nonlocal calls
        calls += 1
        yield _FakeResult(np.full(240, 0.2, dtype=np.float32))
        allow_second.wait(timeout=3.0)
        yield _FakeResult(np.full(240, 0.2, dtype=np.float32))

    engine = _make_engine(tmp_path, fake_pipeline)

    pre_cancel = threading.Event()
    pre_cancel.set()
    assert list(engine.synthesize_stream("never runs", cancel_event=pre_cancel)) == []
    assert calls == 0

    cancel_event = threading.Event()
    gen = engine.synthesize_stream("stop mid way", cancel_event=cancel_event)
    chunk1 = next(gen)
    assert len(chunk1) == 240 * 2
    cancel_event.set()
    allow_second.set()
    assert list(gen) == []
    assert calls == 1


def test_engine_validates_waveform_duration_and_normalizes_without_clicks(tmp_path: Path) -> None:
    bad_nan_engine = _make_engine(
        tmp_path, lambda text, voice, speed: iter([_FakeResult(np.array([0.1, np.nan], dtype=np.float32))])
    )
    with pytest.raises(ModelUnavailable, match="invalid waveform"):
        list(bad_nan_engine.synthesize_stream("bad waveform"))

    bad_2d_engine = _make_engine(
        tmp_path, lambda text, voice, speed: iter([_FakeResult(np.zeros((2, 10), dtype=np.float32))])
    )
    with pytest.raises(ModelUnavailable, match="invalid waveform"):
        list(bad_2d_engine.synthesize_stream("stereo waveform"))

    def too_long_pipeline(text: str, voice: str, speed: float):
        yield _FakeResult(np.zeros(MAX_SAMPLES, dtype=np.float32))
        yield _FakeResult(np.zeros(1, dtype=np.float32))

    long_engine = _make_engine(tmp_path, too_long_pipeline)
    with pytest.raises(ValueError, match="120 seconds"):
        list(long_engine.synthesize_stream("too long"))

    def hot_step_pipeline(text: str, voice: str, speed: float):
        yield _FakeResult(np.full(32, -0.8, dtype=np.float32))
        yield _FakeResult(np.full(32, 1.5, dtype=np.float32))

    norm_engine = _make_engine(tmp_path, hot_step_pipeline)
    pcm_chunks = list(norm_engine.synthesize_stream("step test"))
    assert len(pcm_chunks) == 2
    s1 = np.frombuffer(pcm_chunks[0], dtype="<i2").astype(np.float32) / 32767.0
    s2 = np.frombuffer(pcm_chunks[1], dtype="<i2").astype(np.float32) / 32767.0
    assert s2.size == 32
    assert np.max(np.abs(s2)) <= 1.0
    # First sample of chunk 2 should be smoothly ramped from last sample of chunk 1, avoiding a raw jump.
    assert abs(float(s2[0]) - float(s1[-1])) < 0.05


def test_capabilities_advertises_stream_locally_and_hides_in_cloud(monkeypatch) -> None:
    _configure_auth(monkeypatch)
    client = TestClient(app_module.app)

    local_resp = client.get("/internal/v1/capabilities", headers=AUTH)
    assert local_resp.status_code == 200
    assert "speech.synthesize" in local_resp.json()["capabilities"]
    assert "speech.synthesize.stream" in local_resp.json()["capabilities"]

    monkeypatch.setattr(app_module, "configured_client", lambda: object())
    cloud_resp = client.get("/internal/v1/capabilities", headers=AUTH)
    assert cloud_resp.status_code == 200
    assert cloud_resp.json()["capabilities"] == ["speech.synthesize"]


def test_ws_requires_auth_and_blocks_cloud_mode_with_503(monkeypatch) -> None:
    _configure_auth(monkeypatch)
    client = TestClient(app_module.app)

    with pytest.raises(WebSocketDenialResponse) as unauth_exc:
        with client.websocket_connect("/internal/v2/speech/stream"):
            pass
    assert unauth_exc.value.status_code == 401

    monkeypatch.setattr(app_module, "configured_client", lambda: object())
    with pytest.raises(WebSocketDenialResponse) as cloud_exc:
        with client.websocket_connect("/internal/v2/speech/stream", headers=AUTH):
            pass
    assert cloud_exc.value.status_code == 503
    assert cloud_exc.value.json()["detail"] == "cloud_streaming_unsupported"


def test_ws_incremental_streaming_and_96kb_frame_chunking(monkeypatch, tmp_path: Path) -> None:
    allow_second = threading.Event()
    # 60,000 samples = 120,000 bytes (> 96 KB) to verify transport-safe chunk splitting.
    first_samples = 60000
    second_samples = 1200

    def fake_pipeline(text: str, voice: str, speed: float):
        yield _FakeResult(np.full(first_samples, 0.1, dtype=np.float32))
        assert allow_second.wait(timeout=3.0)
        yield _FakeResult(np.full(second_samples, 0.2, dtype=np.float32))

    engine = _make_engine(tmp_path, fake_pipeline)
    _configure_auth(monkeypatch, engine)
    client = TestClient(app_module.app)

    with client.websocket_connect("/internal/v2/speech/stream", headers=AUTH) as ws:
        ws.send_json(
            {
                "type": "synthesize",
                "text": "hello live voice",
                "voice": "af_heart",
                "locale": "en-US",
                "speed": 0.95,
                "responseId": "resp-1",
                "segmentId": "seg-1",
                "speechEpoch": 1,
            }
        )
        ready = ws.receive_json()
        assert ready == {
            "type": "stream.ready",
            "version": 2,
            "responseId": "resp-1",
            "segmentId": "seg-1",
            "speechEpoch": 1,
            "sampleRateHz": NATIVE_SAMPLE_RATE_HZ,
            "channels": 1,
            "format": "pcm16le",
        }

        # Receive first chunk's split binary frames BEFORE releasing second chunk.
        frame1 = ws.receive_bytes()
        frame2 = ws.receive_bytes()
        assert len(frame1) == 96 * 1024
        assert len(frame2) == first_samples * 2 - 96 * 1024
        assert len(frame1) % 2 == 0 and len(frame2) % 2 == 0

        allow_second.set()
        frame3 = ws.receive_bytes()
        assert len(frame3) == second_samples * 2

        completed = ws.receive_json()
        assert completed == {
            "type": "stream.completed",
            "responseId": "resp-1",
            "segmentId": "seg-1",
            "speechEpoch": 1,
            "totalSamples": first_samples + second_samples,
        }


def test_ws_mid_stream_cancellation_stops_emission(monkeypatch, tmp_path: Path) -> None:
    allow_second = threading.Event()

    def fake_pipeline(text: str, voice: str, speed: float):
        yield _FakeResult(np.full(480, 0.25, dtype=np.float32))
        allow_second.wait(timeout=3.0)
        yield _FakeResult(np.full(480, 0.25, dtype=np.float32))

    engine = _make_engine(tmp_path, fake_pipeline)
    _configure_auth(monkeypatch, engine)
    client = TestClient(app_module.app)

    with client.websocket_connect("/internal/v2/speech/stream", headers=AUTH) as ws:
        ws.send_json(
            {
                "type": "synthesize",
                "text": "cancel me mid stream",
                "voice": "af_heart",
                "locale": "en-US",
                "speed": 0.95,
                "responseId": "resp-cancel",
                "segmentId": "seg-1",
                "speechEpoch": 3,
            }
        )
        ready = ws.receive_json()
        assert ready["type"] == "stream.ready"
        frame1 = ws.receive_bytes()
        assert len(frame1) == 480 * 2

        ws.send_json({"type": "cancel", "responseId": "resp-cancel", "speechEpoch": 3})
        allow_second.set()

        terminal = ws.receive_json()
        assert terminal == {
            "type": "stream.cancelled",
            "responseId": "resp-cancel",
            "segmentId": "seg-1",
            "speechEpoch": 3,
            "totalSamples": 480,
        }


def test_ws_invalid_voice_speed_and_markup_emit_stream_error(monkeypatch, tmp_path: Path) -> None:
    engine = _make_engine(tmp_path, lambda text, voice, speed: iter([]))
    _configure_auth(monkeypatch, engine)
    client = TestClient(app_module.app)

    with client.websocket_connect("/internal/v2/speech/stream", headers=AUTH) as ws:
        ws.send_json(
            {
                "type": "synthesize",
                "text": "hello",
                "voice": "../evil",
                "locale": "en-US",
                "speed": 0.95,
                "responseId": "r1",
                "segmentId": "s1",
                "speechEpoch": 1,
            }
        )
        err1 = ws.receive_json()
        assert err1["type"] == "stream.error"
        assert err1["status"] == 422

        ws.send_json(
            {
                "type": "synthesize",
                "text": "hello",
                "voice": "af_heart",
                "locale": "en-US",
                "speed": 2.5,
                "responseId": "r2",
                "segmentId": "s2",
                "speechEpoch": 2,
            }
        )
        err2 = ws.receive_json()
        assert err2["type"] == "stream.error"
        assert err2["status"] == 422

        ws.send_json(
            {
                "type": "synthesize",
                "text": "<speak>bad</speak>",
                "voice": "af_heart",
                "locale": "en-US",
                "speed": 0.95,
                "responseId": "r3",
                "segmentId": "s3",
                "speechEpoch": 3,
            }
        )
        err3 = ws.receive_json()
        assert err3["type"] == "stream.error"
        assert err3["status"] == 422


def test_overload_rejection_queue_limit_and_wait_timeout(monkeypatch, tmp_path: Path) -> None:
    in_inference = threading.Event()
    release_inference = threading.Event()

    def slow_pipeline(text: str, voice: str, speed: float):
        in_inference.set()
        release_inference.wait(timeout=3.0)
        yield _FakeResult(np.full(240, 0.1, dtype=np.float32))

    # 1. Queue limit exceeded (max_pending_streams=1)
    engine_queue = _make_engine(
        tmp_path, slow_pipeline, max_pending_streams=1, inference_wait_timeout_seconds=1.0
    )
    _configure_auth(monkeypatch, engine_queue)

    holder_results: list[bytes] = []
    holder = threading.Thread(
        target=lambda: holder_results.extend(engine_queue.synthesize_stream("first"))
    )
    holder.start()
    try:
        assert in_inference.wait(timeout=2.0)
        with pytest.raises(OverloadError):
            list(engine_queue.synthesize_stream("second"))

        client = TestClient(app_module.app)
        with client.websocket_connect("/internal/v2/speech/stream", headers=AUTH) as ws:
            ws.send_json(
                {
                    "type": "synthesize",
                    "text": "overloaded ws",
                    "voice": "af_heart",
                    "locale": "en-US",
                    "speed": 0.95,
                    "responseId": "r-ov",
                    "segmentId": "s-ov",
                    "speechEpoch": 1,
                }
            )
            err = ws.receive_json()
            assert err["type"] == "stream.error"
            assert err["code"] == "overloaded"
            assert err["status"] == 503
    finally:
        release_inference.set()
        holder.join(timeout=3.0)
    assert len(holder_results) == 1

    # 2. Wait timeout exceeded (max_pending_streams=2, short wait timeout)
    in_inference.clear()
    release_inference.clear()
    engine_timeout = _make_engine(
        tmp_path, slow_pipeline, max_pending_streams=2, inference_wait_timeout_seconds=0.05
    )
    holder2 = threading.Thread(target=lambda: list(engine_timeout.synthesize_stream("first")))
    holder2.start()
    try:
        assert in_inference.wait(timeout=2.0)
        with pytest.raises(OverloadError, match="timeout"):
            list(engine_timeout.synthesize_stream("second"))
    finally:
        release_inference.set()
        holder2.join(timeout=3.0)


def test_ws_disconnect_triggers_cancellation_and_releases_lock(monkeypatch, tmp_path: Path) -> None:
    chunk1_emitted = threading.Event()
    pipeline_exited = threading.Event()

    def slow_streaming_pipeline(text: str, voice: str, speed: float):
        try:
            yield _FakeResult(np.full(240, 0.2, dtype=np.float32))
            chunk1_emitted.set()
            for _ in range(50):
                time.sleep(0.02)
                yield _FakeResult(np.full(240, 0.2, dtype=np.float32))
        finally:
            pipeline_exited.set()

    engine = _make_engine(tmp_path, slow_streaming_pipeline)
    _configure_auth(monkeypatch, engine)
    client = TestClient(app_module.app)

    with client.websocket_connect("/internal/v2/speech/stream", headers=AUTH) as ws:
        ws.send_json(
            {
                "type": "synthesize",
                "text": "disconnect mid stream",
                "voice": "af_heart",
                "locale": "en-US",
                "speed": 0.95,
                "responseId": "r-disc",
                "segmentId": "s-disc",
                "speechEpoch": 1,
            }
        )
        assert ws.receive_json()["type"] == "stream.ready"
        assert len(ws.receive_bytes()) == 240 * 2
        assert chunk1_emitted.wait(timeout=2.0)
        # Closing the context disconnects the WebSocket mid-stream.

    assert pipeline_exited.wait(timeout=3.0)
    assert engine._inference_lock.acquire(blocking=False)
    engine._inference_lock.release()


def test_v1_execute_and_buffered_synthesize_regression(monkeypatch, tmp_path: Path) -> None:
    calls = 0

    def fake_pipeline(text: str, voice: str, speed: float):
        nonlocal calls
        calls += 1
        assert speed == pytest.approx(0.95)
        yield _FakeResult(np.full(480, 0.25, dtype=np.float32))

    engine = _make_engine(tmp_path, fake_pipeline)
    _configure_auth(monkeypatch, engine)
    client = TestClient(app_module.app)

    payload = {
        "schemaVersion": "openconnect.v1",
        "taskId": "t-v1-regression",
        "kind": "speech.synthesize",
        "idempotencyKey": "idem-1",
        "payload": {
            "schemaVersion": "openconnect.v1",
            "text": "legacy buffered path",
            "voiceId": "af_heart",
            "locale": "en-US",
            "format": "pcm",
            "speechEpoch": 2,
        },
    }
    first = client.post("/internal/v1/execute", json=payload, headers=AUTH)
    assert first.status_code == 200
    body1 = first.json()
    assert body1["idempotentReplay"] is False
    assert body1["result"]["sampleRateHz"] == NATIVE_SAMPLE_RATE_HZ
    assert body1["result"]["speechEpoch"] == 2
    assert len(base64.b64decode(body1["result"]["audioBase64"])) == 480 * 2

    second = client.post("/internal/v1/execute", json=payload, headers=AUTH)
    assert second.status_code == 200
    assert second.json()["idempotentReplay"] is True
    assert calls == 1
