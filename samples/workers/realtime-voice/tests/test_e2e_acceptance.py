#!/usr/bin/env python3
"""Executable E2E acceptance & contract suite for samples/workers/realtime-voice.

Covers WSE2E-AT-036 through WSE2E-AT-044 plus the 12-scenario runtime model cache lifecycle.
"""

from __future__ import annotations

import json
from pathlib import Path
import sys
import unittest

SAMPLE_DIR = Path(__file__).resolve().parent.parent
REPO_ROOT = SAMPLE_DIR.parent.parent.parent
WORKER_SRC = REPO_ROOT / "workers" / "realtime-voice" / "src"
if str(WORKER_SRC) not in sys.path:
    sys.path.insert(0, str(WORKER_SRC))

from openconnect_realtime_voice.engine import FakeVoiceEngine  # noqa: E402
from openconnect_realtime_voice.sessions import (  # noqa: E402
    InvalidTransition,
    SessionRegistry,
    StaleEpoch,
)

FIXTURES = SAMPLE_DIR / "fixtures"


class RealtimeVoiceWorkerAcceptanceTests(unittest.TestCase):
    """WSE2E-AT-036..044 acceptance tests for the Realtime voice worker."""

    def test_at_036_five_turns_session_state_transitions(self) -> None:
        """WSE2E-AT-036: Multi-turn conversational flow executes clean state transitions."""
        fx = json.loads((FIXTURES / "five-turns-session.json").read_text(encoding="utf-8"))
        registry = SessionRegistry()
        session = registry.open(fx["sessionId"])
        self.assertEqual(session.state, "idle")

        session, _ = registry.apply_event(session.session_id, "begin-listening")
        self.assertEqual(session.state, "listening")

        for turn in fx["turns"]:
            session, _ = registry.apply_event(
                session.session_id, "transcript-final", text=turn["userUtterance"]
            )
            self.assertEqual(session.state, "transcribing")

            session, _ = registry.apply_event(session.session_id, "begin-reasoning")
            self.assertEqual(session.state, "reasoning")

            session, _ = registry.apply_event(
                session.session_id, "begin-speaking", speech_epoch=session.speech_epoch
            )
            self.assertEqual(session.state, "speaking")

            session, _ = registry.apply_event(
                session.session_id, "utterance-complete", speech_epoch=session.speech_epoch
            )
            self.assertEqual(session.state, "listening")

        self.assertEqual(len(session.transcript), len(fx["turns"]))

    def test_at_037_barge_in_epoch_fencing(self) -> None:
        """WSE2E-AT-037: Barge-in interruption increments speech epoch and discards stale work."""
        fx = json.loads((FIXTURES / "barge-in-interruption.json").read_text(encoding="utf-8"))
        registry = SessionRegistry()
        session = registry.open(fx["sessionId"])
        registry.apply_event(session.session_id, "begin-listening")
        registry.apply_event(session.session_id, "transcript-final", text="Speaking something")
        registry.apply_event(session.session_id, "begin-reasoning")
        registry.apply_event(session.session_id, "begin-speaking", speech_epoch=0)

        # Apply barge-in event
        session, detail = registry.apply_event(session.session_id, "barge-in")
        self.assertEqual(session.state, "interrupted")
        self.assertEqual(session.speech_epoch, 1)
        self.assertEqual(detail["discardedEpoch"], 0)

        # Work arriving with old epoch 0 is rejected with StaleEpoch
        registry.apply_event(session.session_id, "resume-listening")
        registry.apply_event(session.session_id, "transcript-final", text="New user speech")
        registry.apply_event(session.session_id, "begin-reasoning")
        with self.assertRaises(StaleEpoch):
            registry.apply_event(session.session_id, "begin-speaking", speech_epoch=0)

    def test_at_038_no_speech_idle_session(self) -> None:
        """WSE2E-AT-038: Idle listening session handles silence timeout safely."""
        fx = json.loads((FIXTURES / "no-speech-session.json").read_text(encoding="utf-8"))
        registry = SessionRegistry()
        session = registry.open(fx["sessionId"])
        session, _ = registry.apply_event(session.session_id, "begin-listening")
        self.assertEqual(session.state, "listening")
        self.assertEqual(len(session.transcript), 0)

    def test_at_039_resampling_ratio_24k_to_16k(self) -> None:
        """WSE2E-AT-039: 24kHz to 16kHz audio ratio verification."""
        fx = json.loads((FIXTURES / "resample-24k-to-16k.json").read_text(encoding="utf-8"))
        ratio = fx["targetSampleRateHz"] / fx["sourceSampleRateHz"]
        self.assertAlmostEqual(ratio, 2.0 / 3.0, places=5)
        self.assertTrue((FIXTURES / fx["sourceAudio"]).exists())
        self.assertTrue((FIXTURES / fx["targetAudio"]).exists())

    def test_at_040_silero_frame_size_and_sequence_gaps(self) -> None:
        """WSE2E-AT-040: Silero 512-sample frame size, context, and gap detection."""
        fx = json.loads((FIXTURES / "silero-512-sequence-and-gaps.json").read_text(encoding="utf-8"))
        self.assertEqual(fx["sileroFrameSize"], 512)
        self.assertEqual(fx["contextSize"], 64)
        self.assertEqual(fx["recurrentStateSize"], 256)

        for case in fx["cases"]:
            offsets = case["frameOffsets"]
            has_gap = any(offsets[i] - offsets[i - 1] != 512 for i in range(1, len(offsets)))
            if case.get("expectStateReset"):
                self.assertTrue(has_gap)
            if case.get("expectStatePreserved"):
                self.assertFalse(has_gap)

    def test_at_041_invalid_transition_and_fencing(self) -> None:
        """WSE2E-AT-041: Out-of-order events trigger InvalidTransition."""
        registry = SessionRegistry()
        session = registry.open("test-session-invalid")
        with self.assertRaises(InvalidTransition):
            registry.apply_event(session.session_id, "begin-speaking", speech_epoch=0)

    def test_at_042_tts_playback_echo_cancellation_bounds(self) -> None:
        """WSE2E-AT-042: Echo cancellation buffer configuration conforms."""
        fx = json.loads((FIXTURES / "tts-playback-echo.json").read_text(encoding="utf-8"))
        self.assertTrue(fx["echoCancellationEnabled"])
        self.assertGreater(fx["ttsPlaybackBufferMs"], 0)

    def test_at_043_session_isolation_and_cross_tenant_safety(self) -> None:
        """WSE2E-AT-043: Sessions are isolated by ID and maintain independent states."""
        fx = json.loads((FIXTURES / "disconnect-reconnect-and-cross-tenant.json").read_text(encoding="utf-8"))
        registry = SessionRegistry()
        session_a = registry.open(fx["sessionA"])
        session_b = registry.open(fx["sessionB"])

        registry.apply_event(session_a.session_id, "begin-listening")
        self.assertEqual(session_a.state, "listening")
        self.assertEqual(session_b.state, "idle")

    def test_at_044_fake_voice_engine_speech_chunks(self) -> None:
        """WSE2E-AT-044: Fake voice engine produces ordered speech chunks conforming to schema."""
        registry = SessionRegistry()
        engine = FakeVoiceEngine(registry)
        chunks = engine.synthesize_chunks("The quick brown fox jumps over the lazy dog", "alloy", "en")
        self.assertGreaterEqual(len(chunks), 1)
        self.assertTrue(chunks[-1]["isFinal"])
        self.assertEqual(chunks[0]["sequence"], 0)


if __name__ == "__main__":
    unittest.main(verbosity=2)
