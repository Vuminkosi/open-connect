"""Tests for persistent multi-utterance local streaming STT (/internal/v2/realtime/local)."""

from __future__ import annotations

import asyncio
import dataclasses
import json
import threading
import time
from types import SimpleNamespace

import pytest
from fastapi.testclient import TestClient
from starlette.websockets import WebSocketDisconnect

import openconnect_transcription.app as app_module
from openconnect_transcription import realtime_local, realtime_v2, streaming_local
from openconnect_transcription.engine import FakeTranscriptionEngine

AUTH = {"Authorization": "Bearer local-token"}


def _speech_pcm(duration_ms: int = 120) -> bytes:
    """Return non-silent 24 kHz mono PCM16LE bytes for the requested duration."""
    samples = max(1, streaming_local.SAMPLE_RATE_HZ * duration_ms // 1000)
    return b"\x01\x02" * samples


def _silence_pcm(duration_ms: int = 120) -> bytes:
    """Return all-zero 24 kHz mono PCM16LE silence bytes."""
    samples = max(1, streaming_local.SAMPLE_RATE_HZ * duration_ms // 1000)
    return b"\x00\x00" * samples


@pytest.fixture
def local_worker(monkeypatch):
    settings = dataclasses.replace(
        app_module.settings,
        worker_token="local-token",
        backend="local-model",
        fake_engine=False,
        model_device="cpu",
        cloud_url="https://external.invalid",
        max_audio_bytes=512_000,
        max_duration_ms=10_000,
        streaming_partial_cadence_ms=25,
        streaming_window_ms=4_000,
    )
    monkeypatch.setattr(app_module, "settings", settings)
    monkeypatch.setattr(
        realtime_local.engine_mode,
        "device_info",
        lambda _: SimpleNamespace(gpu_required_missing=False, effective="cpu"),
    )
    monkeypatch.setattr(realtime_local.local_whisper, "load_model", lambda *_: object())
    monkeypatch.setattr(app_module.engine_mode, "device_info", lambda _: SimpleNamespace(
        gpu_required_missing=False, effective="cpu"
    ))
    monkeypatch.setattr(app_module.local_whisper, "load_model", lambda *_: object())
    return TestClient(app_module.app)


def test_reconciler_monotonic_revisions_and_non_append_only_replacement() -> None:
    reconciler = streaming_local.TranscriptReconciler()

    h1 = reconciler.reconcile_partial("  show   my  ")
    assert h1 == streaming_local.PartialHypothesis(
        revision=1, text="show my", stable_text="", unstable_text="show my"
    )

    h2 = reconciler.reconcile_partial("show my outstanding tasks")
    assert h2 == streaming_local.PartialHypothesis(
        revision=2,
        text="show my outstanding tasks",
        stable_text="show my",
        unstable_text="outstanding tasks",
    )

    # Non-append-only revision replacing an earlier word shrinks stable prefix.
    h3 = reconciler.reconcile_partial("show me outstanding tasks")
    assert h3 == streaming_local.PartialHypothesis(
        revision=3,
        text="show me outstanding tasks",
        stable_text="show",
        unstable_text="me outstanding tasks",
    )

    # Identical text promotes everything to stable.
    h4 = reconciler.reconcile_partial("show me outstanding tasks")
    assert h4 == streaming_local.PartialHypothesis(
        revision=4,
        text="show me outstanding tasks",
        stable_text="show me outstanding tasks",
        unstable_text="",
    )

    # Duplicate hypothesis with identical stable/unstable is suppressed.
    assert reconciler.reconcile_partial("show me outstanding tasks") is None

    final = reconciler.finalize("show me outstanding tasks.")
    assert final == streaming_local.FinalTranscript(
        revision=5, text="show me outstanding tasks.", is_empty=False
    )


def test_multi_utterance_persistent_session_without_reconnect(local_worker, monkeypatch) -> None:
    seen_lengths: list[int] = []
    utterance_texts = iter(["first turn", "second turn", "third turn"])

    def fake_transcribe(_, audio, locale):
        seen_lengths.append(len(audio.raw_pcm))
        return SimpleNamespace(transcript=next(utterance_texts))

    monkeypatch.setattr(streaming_local.local_whisper, "transcribe", fake_transcribe)

    with local_worker.websocket_connect("/internal/v2/realtime/local", headers=AUTH) as ws:
        ready = ws.receive_json()
        assert ready == {
            "type": "sessionReady",
            "version": 2,
            "supportsPartials": True,
            "sampleRateHz": 24000,
            "channels": 1,
        }

        for idx, expected in enumerate(["first turn", "second turn", "third turn"], start=1):
            ws.send_json(
                {
                    "type": "startUtterance",
                    "captureId": f"cap-{idx}",
                    "speechEpoch": idx,
                    "locale": "en",
                }
            )
            ws.send_bytes(_speech_pcm(50))
            ws.send_json(
                {
                    "type": "endUtterance",
                    "captureId": f"cap-{idx}",
                    "speechEpoch": idx,
                }
            )
            final = ws.receive_json()
            assert final == {
                "type": "transcript.final",
                "captureId": f"cap-{idx}",
                "speechEpoch": idx,
                "revision": 1,
                "text": expected,
                "isEmpty": False,
                "authoritative": True,
            }

        ws.send_json({"type": "closeSession"})
        with pytest.raises(WebSocketDisconnect) as closed:
            ws.receive_json()
        assert closed.value.code == 1000

    # Each utterance buffer was reset between turns (all 50ms = 2400 bytes).
    assert seen_lengths == [2400, 2400, 2400]


def test_streaming_partials_monotonic_revisions_and_single_flight(local_worker, monkeypatch) -> None:
    in_flight = 0
    max_concurrent = 0
    lock = threading.Lock()
    call_count = 0
    responses = [
        "hello world",
        "hello world how are",
        "hello world how are you",
    ]

    def fake_transcribe(_, audio, locale):
        nonlocal in_flight, max_concurrent, call_count
        with lock:
            in_flight += 1
            max_concurrent = max(max_concurrent, in_flight)
            idx = call_count
            call_count += 1
        try:
            time.sleep(0.03)
            text = responses[min(idx, len(responses) - 1)]
            return SimpleNamespace(transcript=text)
        finally:
            with lock:
                in_flight -= 1

    monkeypatch.setattr(streaming_local.local_whisper, "transcribe", fake_transcribe)

    with local_worker.websocket_connect("/internal/v2/realtime/local", headers=AUTH) as ws:
        assert ws.receive_json()["type"] == "sessionReady"
        ws.send_json(
            {"type": "startUtterance", "captureId": "cap-stream", "speechEpoch": 2, "locale": "en"}
        )

        ws.send_bytes(_speech_pcm(120))
        first_partial = ws.receive_json()
        assert first_partial == {
            "type": "transcript.partial",
            "captureId": "cap-stream",
            "speechEpoch": 2,
            "revision": 1,
            "text": "hello world",
            "stableText": "",
            "unstableText": "hello world",
            "authoritative": False,
        }

        ws.send_bytes(_speech_pcm(120))
        second_partial = ws.receive_json()
        assert second_partial == {
            "type": "transcript.partial",
            "captureId": "cap-stream",
            "speechEpoch": 2,
            "revision": 2,
            "text": "hello world how are",
            "stableText": "hello world",
            "unstableText": "how are",
            "authoritative": False,
        }

        ws.send_json({"type": "endUtterance", "captureId": "cap-stream", "speechEpoch": 2})
        final = ws.receive_json()
        assert final["type"] == "transcript.final"
        assert final["captureId"] == "cap-stream"
        assert final["speechEpoch"] == 2
        assert final["revision"] > second_partial["revision"]
        assert final["text"] == "hello world how are you"
        assert final["isEmpty"] is False
        assert final["authoritative"] is True

        ws.send_json({"type": "closeSession"})

    # Single-flight per session: never more than 1 concurrent decode.
    assert max_concurrent == 1


def test_silence_and_empty_utterances_emit_empty_final_without_closing(
    local_worker, monkeypatch
) -> None:
    calls = 0

    def fake_transcribe(_, audio, locale):
        nonlocal calls
        calls += 1
        return SimpleNamespace(transcript="   ")

    monkeypatch.setattr(streaming_local.local_whisper, "transcribe", fake_transcribe)

    with local_worker.websocket_connect("/internal/v2/realtime/local", headers=AUTH) as ws:
        assert ws.receive_json()["type"] == "sessionReady"

        # 1. Zero audio bytes.
        ws.send_json({"type": "startUtterance", "captureId": "cap-empty", "speechEpoch": 1})
        ws.send_json({"type": "endUtterance", "captureId": "cap-empty", "speechEpoch": 1})
        assert ws.receive_json() == {
            "type": "transcript.final",
            "captureId": "cap-empty",
            "speechEpoch": 1,
            "revision": 1,
            "text": "",
            "isEmpty": True,
            "authoritative": True,
        }

        # 2. All-zero digital silence PCM.
        ws.send_json({"type": "startUtterance", "captureId": "cap-silence", "speechEpoch": 2})
        ws.send_bytes(_silence_pcm(150))
        ws.send_json({"type": "endUtterance", "captureId": "cap-silence", "speechEpoch": 2})
        assert ws.receive_json() == {
            "type": "transcript.final",
            "captureId": "cap-silence",
            "speechEpoch": 2,
            "revision": 1,
            "text": "",
            "isEmpty": True,
            "authoritative": True,
        }
        assert calls == 0  # Neither empty nor pure silence invokes Whisper.

        # 3. Non-silent audio where Whisper returns whitespace.
        ws.send_json({"type": "startUtterance", "captureId": "cap-ws", "speechEpoch": 3})
        ws.send_bytes(_speech_pcm(50))
        ws.send_json({"type": "endUtterance", "captureId": "cap-ws", "speechEpoch": 3})
        assert ws.receive_json() == {
            "type": "transcript.final",
            "captureId": "cap-ws",
            "speechEpoch": 3,
            "revision": 1,
            "text": "",
            "isEmpty": True,
            "authoritative": True,
        }
        assert calls == 1

        ws.send_json({"type": "closeSession"})


def test_cancel_utterance_discards_inflight_partial_and_keeps_session_open(
    local_worker, monkeypatch
) -> None:
    partial_started = threading.Event()
    release_partial = threading.Event()

    def fake_transcribe(_, audio, locale):
        if not partial_started.is_set():
            partial_started.set()
            assert release_partial.wait(5)
            return SimpleNamespace(transcript="stale cancelled partial")
        return SimpleNamespace(transcript="fresh utterance")

    monkeypatch.setattr(streaming_local.local_whisper, "transcribe", fake_transcribe)

    with local_worker.websocket_connect("/internal/v2/realtime/local", headers=AUTH) as ws:
        assert ws.receive_json()["type"] == "sessionReady"

        ws.send_json({"type": "startUtterance", "captureId": "cap-cancelled", "speechEpoch": 1})
        ws.send_bytes(_speech_pcm(120))
        assert partial_started.wait(5)

        # Cancel while partial decode is blocked inside Whisper.
        ws.send_json({"type": "cancelUtterance", "captureId": "cap-cancelled", "speechEpoch": 1})
        release_partial.set()

        # Immediately start and finalize a new utterance on the same persistent WS.
        ws.send_json({"type": "startUtterance", "captureId": "cap-next", "speechEpoch": 2})
        ws.send_bytes(_speech_pcm(50))
        ws.send_json({"type": "endUtterance", "captureId": "cap-next", "speechEpoch": 2})

        msg = ws.receive_json()
        # Stale partial from cap-cancelled was discarded; first message is cap-next final.
        assert msg == {
            "type": "transcript.final",
            "captureId": "cap-next",
            "speechEpoch": 2,
            "revision": 1,
            "text": "fresh utterance",
            "isEmpty": False,
            "authoritative": True,
        }
        ws.send_json({"type": "closeSession"})


@pytest.mark.parametrize(
    "payload",
    [
        b"",
        b"\x01",
        b"\x01\x02" * (streaming_local.MAX_FRAME_BYTES // 2 + 1),
    ],
    ids=["empty", "odd_bytes", "oversized_frame"],
)
def test_invalid_frame_sizes_and_odd_bytes_rejected(local_worker, payload) -> None:
    with local_worker.websocket_connect("/internal/v2/realtime/local", headers=AUTH) as ws:
        assert ws.receive_json()["type"] == "sessionReady"
        ws.send_json({"type": "startUtterance", "captureId": "cap-bad", "speechEpoch": 1})
        ws.send_bytes(payload)
        with pytest.raises(WebSocketDisconnect) as error:
            ws.receive_json()
    assert error.value.code == 1003


def test_utterance_cap_enforced(local_worker, monkeypatch) -> None:
    monkeypatch.setattr(
        app_module,
        "settings",
        dataclasses.replace(app_module.settings, max_audio_bytes=4_800, max_duration_ms=100),
    )
    with local_worker.websocket_connect("/internal/v2/realtime/local", headers=AUTH) as ws:
        assert ws.receive_json()["type"] == "sessionReady"
        ws.send_json({"type": "startUtterance", "captureId": "cap-limit", "speechEpoch": 1})
        ws.send_bytes(_speech_pcm(60))  # 2880 bytes <= 4800
        ws.send_bytes(_speech_pcm(60))  # 5760 bytes > 4800
        with pytest.raises(WebSocketDisconnect) as error:
            ws.receive_json()
    assert error.value.code == 1003


@pytest.mark.parametrize("authorization", [None, "local-token", "Bearer wrong"])
def test_v2_route_requires_bearer_token(local_worker, authorization) -> None:
    headers = {"Authorization": authorization} if authorization else {}
    with pytest.raises(WebSocketDisconnect) as error:
        with local_worker.websocket_connect("/internal/v2/realtime/local", headers=headers):
            pass
    assert error.value.code == 1008


def test_v2_route_rejects_when_model_unavailable(local_worker, monkeypatch) -> None:
    monkeypatch.setattr(realtime_local.local_whisper, "load_model", lambda *_: None)
    with local_worker.websocket_connect("/internal/v2/realtime/local", headers=AUTH) as ws:
        with pytest.raises(WebSocketDisconnect) as error:
            ws.receive_json()
    assert error.value.code == 1013


def test_capabilities_advertise_streaming_local_without_claiming_native_streaming(
    local_worker, monkeypatch
) -> None:
    caps = local_worker.get("/internal/v1/capabilities", headers=AUTH)
    assert caps.status_code == 200
    body = caps.json()
    assert body["supportsPartials"] is False
    assert FakeTranscriptionEngine.supports_partials is False
    assert "transcribe-streaming-local" in body["capabilities"]
    assert body["streamingLocal"] == {
        "enabled": True,
        "endpoint": "/internal/v2/realtime/local",
        "recognitionMode": "rolling-window",
        "nativeStreaming": False,
        "supportsPartials": True,
        "supportsFinalization": True,
        "supportsReset": True,
        "modelReady": True,
        "sampleRateHz": 24000,
        "channels": 1,
        "cadenceMs": 25,
        "defaultLocale": "en",
    }

    # When model is unavailable, streamingLocal reports disabled and not ready.
    monkeypatch.setattr(realtime_local.local_whisper, "load_model", lambda *_: None)
    monkeypatch.setattr(app_module.local_whisper, "load_model", lambda *_: None)
    unavailable = local_worker.get("/internal/v1/capabilities", headers=AUTH).json()
    assert "transcribe-streaming-local" not in unavailable["capabilities"]
    assert unavailable["streamingLocal"]["enabled"] is False
    assert unavailable["streamingLocal"]["modelReady"] is False
    assert unavailable["streamingLocal"]["nativeStreaming"] is False


def test_v1_local_realtime_regression(local_worker, monkeypatch) -> None:
    monkeypatch.setattr(
        realtime_local.local_whisper,
        "transcribe",
        lambda *_: SimpleNamespace(transcript="v1 still works"),
    )
    with local_worker.websocket_connect("/internal/v1/realtime/local", headers=AUTH) as ws:
        assert ws.receive_json() == {"type": "sessionReady"}
        ws.send_bytes(_speech_pcm(50))
        ws.send_json({"type": "endStream"})
        assert ws.receive_json() == {"type": "speechComplete", "text": "v1 still works"}


class _FakeMuseUpstream:
    """In-memory mock for Meta Cloud Muse ENDPOINTING realtime WebSocket."""

    def __init__(self, on_audio_script: list[list[str]]) -> None:
        self.sent: list[bytes | str] = []
        self._incoming: asyncio.Queue[str | None] = asyncio.Queue()
        self._on_audio_script = list(on_audio_script)
        self._incoming.put_nowait(json.dumps({"type": "sessionReady"}))
        self.closed = False

    async def __aenter__(self) -> "_FakeMuseUpstream":
        return self

    async def __aexit__(self, *_exc) -> None:
        self.closed = True

    async def send(self, data: bytes | str) -> None:
        self.sent.append(data)
        if isinstance(data, bytes) and self._on_audio_script:
            for item in self._on_audio_script.pop(0):
                self._incoming.put_nowait(item)

    async def recv(self) -> str:
        item = await self._incoming.get()
        assert item is not None
        return item

    async def close(self) -> None:
        self.closed = True
        self._incoming.put_nowait(None)

    def __aiter__(self):
        return self

    async def __anext__(self) -> str:
        item = await self._incoming.get()
        if item is None:
            raise StopAsyncIteration
        return item


def test_v2_realtime_uses_cloud_muse_endpointing_as_primary(local_worker, monkeypatch) -> None:
    monkeypatch.setattr(
        app_module,
        "settings",
        dataclasses.replace(
            app_module.settings,
            cloud_url="https://api.meta.ai/v1/asr/transcribe",
            cloud_api_key="muse-test-key",
            model_id="muse-voice-transcribe-1.0",
            transcription_mode="ENDPOINTING",
        ),
    )
    monkeypatch.setattr(realtime_v2.cloud, "privacy_zone_strict", lambda: False)

    upstream = _FakeMuseUpstream(
        [
            [
                json.dumps({"type": "speechPartial", "text": "show my"}),
                json.dumps({"type": "speechComplete", "text": "show my tasks"}),
            ],
            [
                json.dumps({"type": "speechComplete", "text": "second turn"}),
            ],
        ]
    )
    monkeypatch.setattr(realtime_v2.websockets, "connect", lambda *a, **kw: upstream)

    caps = local_worker.get("/internal/v1/capabilities", headers=AUTH).json()
    assert caps["streamingRealtime"] == {
        "enabled": True,
        "endpoint": "/internal/v2/realtime",
        "primaryProvider": "cloud-muse",
        "fallbackAvailable": True,
        "transcriptionMode": "ENDPOINTING",
        "supportsPartials": True,
        "sampleRateHz": 24000,
        "channels": 1,
    }

    with local_worker.websocket_connect("/internal/v2/realtime", headers=AUTH) as ws:
        ready = ws.receive_json()
        assert ready == {
            "type": "sessionReady",
            "version": 2,
            "provider": "cloud-muse",
            "mode": "ENDPOINTING",
            "supportsPartials": True,
            "sampleRateHz": 24000,
            "channels": 1,
        }

        ws.send_json({"type": "startUtterance", "captureId": "muse-1", "speechEpoch": 1})
        ws.send_bytes(_speech_pcm(40))
        partial = ws.receive_json()
        assert partial["type"] == "transcript.partial"
        assert partial["captureId"] == "muse-1"
        assert partial["speechEpoch"] == 1
        assert partial["text"] == "show my"
        assert partial["revision"] == 1

        final_1 = ws.receive_json()
        assert final_1 == {
            "type": "transcript.final",
            "captureId": "muse-1",
            "speechEpoch": 1,
            "revision": 2,
            "text": "show my tasks",
            "isEmpty": False,
            "authoritative": True,
        }

        # Second utterance on the same persistent Muse ENDPOINTING dialogue socket.
        ws.send_json({"type": "startUtterance", "captureId": "muse-2", "speechEpoch": 2})
        ws.send_bytes(_speech_pcm(40))
        final_2 = ws.receive_json()
        assert final_2 == {
            "type": "transcript.final",
            "captureId": "muse-2",
            "speechEpoch": 2,
            "revision": 1,
            "text": "second turn",
            "isEmpty": False,
            "authoritative": True,
        }

        ws.send_json({"type": "closeSession"})

    handshake = json.loads(upstream.sent[0])
    assert handshake["model"] == "muse-voice-transcribe-1.0"
    assert handshake["mode"] == "ENDPOINTING"
    assert handshake["audioEncoding"] == "PCM_24KHZ"


def test_v2_realtime_falls_back_to_local_whisper_when_cloud_unavailable_or_strict(
    local_worker, monkeypatch
) -> None:
    monkeypatch.setattr(
        app_module,
        "settings",
        dataclasses.replace(
            app_module.settings,
            cloud_url="https://api.meta.ai/v1/asr/transcribe",
            cloud_api_key="muse-test-key",
            model_id="muse-voice-transcribe-1.0",
        ),
    )
    monkeypatch.setattr(
        streaming_local.local_whisper,
        "transcribe",
        lambda *_: SimpleNamespace(transcript="local fallback result"),
    )

    # 1. Strict-local privacy zone forces local Whisper without touching cloud.
    monkeypatch.setattr(realtime_v2.cloud, "privacy_zone_strict", lambda: True)
    with local_worker.websocket_connect("/internal/v2/realtime", headers=AUTH) as ws:
        ready = ws.receive_json()
        assert ready == {
            "type": "sessionReady",
            "version": 2,
            "supportsPartials": True,
            "sampleRateHz": 24000,
            "channels": 1,
        }
        ws.send_json({"type": "startUtterance", "captureId": "fb-1", "speechEpoch": 1})
        ws.send_bytes(_speech_pcm(50))
        ws.send_json({"type": "endUtterance", "captureId": "fb-1", "speechEpoch": 1})
        final = ws.receive_json()
        assert final["type"] == "transcript.final"
        assert final["text"] == "local fallback result"
        ws.send_json({"type": "closeSession"})

    # 2. Cloud connect failure falls back to local Whisper seamlessly.
    monkeypatch.setattr(realtime_v2.cloud, "privacy_zone_strict", lambda: False)

    def _failing_connect(*_a, **_kw):
        raise OSError("upstream unreachable")

    monkeypatch.setattr(realtime_v2.websockets, "connect", _failing_connect)
    with local_worker.websocket_connect("/internal/v2/realtime", headers=AUTH) as ws:
        ready = ws.receive_json()
        assert ready["type"] == "sessionReady"
        assert ready["version"] == 2
        ws.send_json({"type": "startUtterance", "captureId": "fb-2", "speechEpoch": 2})
        ws.send_bytes(_speech_pcm(50))
        ws.send_json({"type": "endUtterance", "captureId": "fb-2", "speechEpoch": 2})
        final = ws.receive_json()
        assert final["type"] == "transcript.final"
        assert final["text"] == "local fallback result"
        ws.send_json({"type": "closeSession"})

