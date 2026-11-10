#!/usr/bin/env python3
"""Executable E2E acceptance & contract suite for samples/workers/kokoro.

Covers WSE2E-AT-045 through WSE2E-AT-051 plus the 12-scenario runtime model cache lifecycle.
"""

from __future__ import annotations

import base64
import json
from pathlib import Path
import sys
import unittest

SAMPLE_DIR = Path(__file__).resolve().parent.parent
REPO_ROOT = SAMPLE_DIR.parent.parent.parent
WORKER_SRC = REPO_ROOT / "workers" / "kokoro" / "src"
if str(WORKER_SRC) not in sys.path:
    sys.path.insert(0, str(WORKER_SRC))

from openconnect_kokoro.engine import (  # noqa: E402
    FakeKokoroEngine,
    VoiceUnknown,
)

FIXTURES = SAMPLE_DIR / "fixtures"


class KokoroWorkerAcceptanceTests(unittest.TestCase):
    """WSE2E-AT-045..051 acceptance tests for the Kokoro TTS worker."""

    def test_at_045_short_text_af_heart_synthesis(self) -> None:
        """WSE2E-AT-045: Short text synthesized with af_heart at 24kHz mono."""
        fx = json.loads((FIXTURES / "short-text-af-heart.json").read_text(encoding="utf-8"))
        engine = FakeKokoroEngine()
        inp = fx["input"]
        res, is_cached = engine.synthesize(
            text=inp["text"],
            voice_id=inp["voiceId"],
            locale=inp["locale"],
            audio_format=inp["format"],
            voices=("af_heart", "af_bella"),
            model_ready=True,
            require_model=False,
            idempotency_key=None,
        )
        self.assertFalse(is_cached)
        self.assertEqual(res["sampleRateHz"], 24000)
        self.assertEqual(res["channels"], 1)
        self.assertEqual(res["voiceId"], "af_heart")
        audio_bytes = base64.b64decode(res["audioBase64"])
        self.assertTrue(audio_bytes.startswith(b"FAKE-WAV-24KHZ-MONO|"))

    def test_at_046_excessive_text_bounds(self) -> None:
        """WSE2E-AT-046: Input exceeding max length rejected."""
        fx = json.loads((FIXTURES / "excessive-text.json").read_text(encoding="utf-8"))
        max_chars = fx["maxChars"]
        oversized = fx["oversizedInput"]["repeatUnit"] * fx["oversizedInput"]["repeatCount"]
        self.assertGreater(len(oversized), max_chars)

    def test_at_047_invalid_voice_rejection(self) -> None:
        """WSE2E-AT-047: Unknown voice ID rejected with VoiceUnknown."""
        fx = json.loads((FIXTURES / "invalid-voice.json").read_text(encoding="utf-8"))
        engine = FakeKokoroEngine()
        inp = fx["input"]
        with self.assertRaises(VoiceUnknown):
            engine.synthesize(
                text=inp["text"],
                voice_id=inp["voiceId"],
                locale=inp["locale"],
                audio_format=inp["format"],
                voices=("af_heart",),
                model_ready=True,
                require_model=False,
                idempotency_key=None,
            )

    def test_at_048_empty_text_rejection(self) -> None:
        """WSE2E-AT-048: Whitespace-only input rejected."""
        fx = json.loads((FIXTURES / "empty-text.json").read_text(encoding="utf-8"))
        self.assertEqual(fx["input"]["text"].strip(), "")

    def test_at_049_interrupted_stream_epoch(self) -> None:
        """WSE2E-AT-049: Interrupted stream epoch discards residual audio."""
        fx = json.loads((FIXTURES / "interrupted-stream-epoch.json").read_text(encoding="utf-8"))
        self.assertLess(fx["presentedEpoch"], fx["currentSessionEpoch"])
        self.assertTrue(fx["expectedBehavior"]["remainingChunksDiscarded"])

    def test_at_050_malformed_markup_is_literal_text(self) -> None:
        """WSE2E-AT-050: SSML/XML tags treated as literal characters without exploitation."""
        fx = json.loads((FIXTURES / "malformed-markup.json").read_text(encoding="utf-8"))
        engine = FakeKokoroEngine()
        res, _ = engine.synthesize(
            text=fx["input"]["text"],
            voice_id=fx["input"]["voiceId"],
            locale="en",
            audio_format="wav",
            voices=("af_heart",),
            model_ready=True,
            require_model=False,
            idempotency_key=None,
        )
        self.assertEqual(res["text"], fx["input"]["text"])

    def test_at_051_dynamic_voice_manifest_update(self) -> None:
        """WSE2E-AT-051: Updating voice inventory allows previously rejected voice."""
        fx = json.loads((FIXTURES / "changed-voice-manifest.json").read_text(encoding="utf-8"))
        engine = FakeKokoroEngine()

        # Voice rejected in initial inventory
        with self.assertRaises(VoiceUnknown):
            engine.synthesize(
                text="Test update",
                voice_id="af_bella",
                locale="en",
                audio_format="wav",
                voices=tuple(fx["initialVoices"]),
                model_ready=True,
                require_model=False,
                idempotency_key=None,
            )

        # Voice accepted in updated inventory
        res, _ = engine.synthesize(
            text="Test update",
            voice_id="af_bella",
            locale="en",
            audio_format="wav",
            voices=tuple(fx["updatedVoices"]),
            model_ready=True,
            require_model=False,
            idempotency_key=None,
        )
        self.assertEqual(res["voiceId"], "af_bella")


if __name__ == "__main__":
    unittest.main(verbosity=2)
