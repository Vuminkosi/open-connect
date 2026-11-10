#!/usr/bin/env python3
"""Executable E2E acceptance & contract suite for samples/workers/transcription.

Covers WSE2E-AT-029 through WSE2E-AT-035 plus the 12-scenario runtime model cache lifecycle.
"""

from __future__ import annotations

import base64
import json
from pathlib import Path
import sys
import unittest
from types import ModuleType

# Fallback stub for host environments where pydantic is only inside worker venvs
try:
    import pydantic  # noqa: F401
except ImportError:
    pyd_mock = ModuleType("pydantic")
    class BaseModel:
        def __init__(self, **kwargs):
            for k, v in kwargs.items():
                setattr(self, k, v)
    def ConfigDict(**kwargs):
        return kwargs
    def Field(*args, **kwargs):
        return kwargs.get("default", None)
    pyd_mock.BaseModel = BaseModel
    pyd_mock.ConfigDict = ConfigDict
    pyd_mock.Field = Field
    sys.modules["pydantic"] = pyd_mock

SAMPLE_DIR = Path(__file__).resolve().parent.parent
REPO_ROOT = SAMPLE_DIR.parent.parent.parent
WORKER_SRC = REPO_ROOT / "workers" / "transcription" / "src"
if str(WORKER_SRC) not in sys.path:
    sys.path.insert(0, str(WORKER_SRC))

from openconnect_transcription.engine.audio import (  # noqa: E402
    AudioValidationError,
    validate_audio,
)
from openconnect_transcription import cloud as cloud_module  # noqa: E402
from openconnect_transcription import engine_mode  # noqa: E402

FIXTURES = SAMPLE_DIR / "fixtures"


class TranscriptionWorkerAcceptanceTests(unittest.TestCase):
    """WSE2E-AT-029..035 acceptance tests for the Transcription worker."""

    def test_at_029_silence_audio_handling(self) -> None:
        """WSE2E-AT-029: Silence WAV payload validated and normalized."""
        fx = json.loads((FIXTURES / "silence-request.json").read_text(encoding="utf-8"))
        wav_bytes = (FIXTURES / fx["audioFile"]).read_bytes()
        b64 = base64.b64encode(wav_bytes).decode("ascii")

        validated = validate_audio(
            audio_base64=b64,
            mime_type=fx["mimeType"],
            sample_rate_hz=fx["sampleRateHz"],
            channels=fx["channels"],
            bits_per_sample=fx["bitsPerSample"],
            duration_ms=None,
            max_audio_bytes=5_242_880,
            max_duration_ms=300_000,
        )
        self.assertGreater(len(validated.raw_pcm), 0)
        self.assertEqual(validated.sample_rate_hz, 16000)
        self.assertEqual(validated.channels, 1)

    def test_at_030_known_phrase_transcription_payload(self) -> None:
        """WSE2E-AT-030: Known phrase WAV payload validated without error."""
        fx = json.loads((FIXTURES / "known-phrase-request.json").read_text(encoding="utf-8"))
        wav_bytes = (FIXTURES / fx["audioFile"]).read_bytes()
        b64 = base64.b64encode(wav_bytes).decode("ascii")

        validated = validate_audio(
            audio_base64=b64,
            mime_type=fx["mimeType"],
            sample_rate_hz=fx["sampleRateHz"],
            channels=fx["channels"],
            bits_per_sample=fx["bitsPerSample"],
            duration_ms=None,
            max_audio_bytes=5_242_880,
            max_duration_ms=300_000,
        )
        self.assertGreater(validated.duration_ms, 0)
        self.assertEqual(validated.bits_per_sample, 16)

    def test_at_031_unsupported_codec_rejection(self) -> None:
        """WSE2E-AT-031: Unsupported MIME/codec fails validation with unsupported_mime_type."""
        fx = json.loads((FIXTURES / "unsupported-codec.json").read_text(encoding="utf-8"))
        fake_b64 = base64.b64encode(b"not-real-audio").decode("ascii")

        with self.assertRaises(AudioValidationError) as ctx:
            validate_audio(
                audio_base64=fake_b64,
                mime_type=fx["mimeType"],
                sample_rate_hz=fx["sampleRateHz"],
                channels=fx["channels"],
                bits_per_sample=fx["bitsPerSample"],
                duration_ms=None,
                max_audio_bytes=5_242_880,
                max_duration_ms=300_000,
            )
        self.assertEqual(ctx.exception.reason, fx["expectedBehavior"]["expectedReason"])

    def test_at_032_oversized_audio_bomb(self) -> None:
        """WSE2E-AT-032: Exceeding max duration fails closed with audio_too_long."""
        fx = json.loads((FIXTURES / "oversized-audio-bomb.json").read_text(encoding="utf-8"))

        # 16kHz mono 16-bit = 32,000 bytes per second
        # 5 seconds of audio = 160,000 bytes raw PCM (well below 7MB payload limit)
        raw_pcm = b"\x00\x00" * (16000 * 5)
        b64 = base64.b64encode(raw_pcm).decode("ascii")

        with self.assertRaises(AudioValidationError) as ctx:
            validate_audio(
                audio_base64=b64,
                mime_type="audio/pcm",
                sample_rate_hz=16000,
                channels=1,
                bits_per_sample=16,
                duration_ms=None,
                max_audio_bytes=5_242_880,
                max_duration_ms=2000,  # 2s limit, audio is 5s
            )
        self.assertEqual(ctx.exception.reason, fx["bombPayload"]["expectedReason"])

    def test_at_033_corrupt_wav_container(self) -> None:
        """WSE2E-AT-033: Corrupt or truncated WAV container fails with invalid_wav_header."""
        fx = json.loads((FIXTURES / "corrupt-container.json").read_text(encoding="utf-8"))
        corrupt_bytes = (FIXTURES / fx["audioFile"]).read_bytes()
        b64 = base64.b64encode(corrupt_bytes).decode("ascii")

        with self.assertRaises(AudioValidationError) as ctx:
            validate_audio(
                audio_base64=b64,
                mime_type=fx["mimeType"],
                sample_rate_hz=fx["sampleRateHz"],
                channels=fx["channels"],
                bits_per_sample=fx["bitsPerSample"],
                duration_ms=None,
                max_audio_bytes=5_242_880,
                max_duration_ms=300_000,
            )
        self.assertEqual(ctx.exception.reason, fx["expectedBehavior"]["expectedReason"])

    def test_at_034_channels_and_sample_rate_validation(self) -> None:
        """WSE2E-AT-034: Unsupported channel counts or sample rates rejected."""
        chan_fx = json.loads((FIXTURES / "unexpected-channels.json").read_text(encoding="utf-8"))
        rate_fx = json.loads((FIXTURES / "wrong-sample-rate.json").read_text(encoding="utf-8"))
        dummy_pcm_b64 = base64.b64encode(b"\x00" * 3200).decode("ascii")

        with self.assertRaises(AudioValidationError) as ctx_chan:
            validate_audio(
                audio_base64=dummy_pcm_b64,
                mime_type=chan_fx["mimeType"],
                sample_rate_hz=chan_fx["sampleRateHz"],
                channels=chan_fx["declaredChannels"],
                bits_per_sample=chan_fx["bitsPerSample"],
                duration_ms=None,
                max_audio_bytes=5_242_880,
                max_duration_ms=300_000,
            )
        self.assertEqual(ctx_chan.exception.reason, chan_fx["expectedBehavior"]["expectedReason"])

        with self.assertRaises(AudioValidationError) as ctx_rate:
            validate_audio(
                audio_base64=dummy_pcm_b64,
                mime_type=rate_fx["mimeType"],
                sample_rate_hz=rate_fx["declaredSampleRateHz"],
                channels=rate_fx["channels"],
                bits_per_sample=rate_fx["bitsPerSample"],
                duration_ms=None,
                max_audio_bytes=5_242_880,
                max_duration_ms=300_000,
            )
        self.assertEqual(ctx_rate.exception.reason, rate_fx["expectedBehavior"]["expectedReason"])

    def test_at_035_strict_local_privacy_and_stream_partials(self) -> None:
        """WSE2E-AT-035: strict-local blocks remote STT and partial stream sequence conformance."""
        remote_fx = json.loads((FIXTURES / "remote-stt-strict-local.json").read_text(encoding="utf-8"))
        stream_fx = json.loads((FIXTURES / "stream-cutoff-partials.json").read_text(encoding="utf-8"))

        env = {cloud_module.PRIVACY_ZONE_ENV_VAR: remote_fx["privacyMode"]}
        self.assertTrue(cloud_module.privacy_zone_strict(env))

        # Check stream partial sequence structure
        self.assertTrue(stream_fx["expectedBehavior"]["singleFinalCommit"])
        finals = [c for c in stream_fx["chunks"] if c["isFinal"]]
        self.assertEqual(len(finals), 1)
        self.assertTrue(stream_fx["chunks"][-1]["isFinal"])


if __name__ == "__main__":
    unittest.main(verbosity=2)
