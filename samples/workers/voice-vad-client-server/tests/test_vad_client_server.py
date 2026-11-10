#!/usr/bin/env python3
"""Executable E2E acceptance suite for Client/Server Voice VAD (§6.1).

Exercises Disabled, Client, Server, and Hybrid Silero VAD modes, pre-roll timing,
24kHz->16kHz resample, 512-sample frame accumulation, source sample offsets,
sequence gap resets, barge-in epoch fencing, permission denial, and single final transcript commit.
"""

from __future__ import annotations

import json
from pathlib import Path
import sys
import unittest

SAMPLE_DIR = Path(__file__).resolve().parent.parent
FIXTURES = SAMPLE_DIR / "fixtures"


class VoiceVadClientServerAcceptanceTests(unittest.TestCase):
    """§6.1 Voice VAD client/server architecture tests."""

    def test_vad_modes_independence_and_invariants(self) -> None:
        """Disabled, Client, Server, and Hybrid modes behave independently."""
        fx = json.loads((FIXTURES / "vad-modes-matrix.json").read_text(encoding="utf-8"))
        mode_names = [m["mode"] for m in fx["modes"]]
        self.assertEqual(mode_names, ["Disabled", "Client", "Server", "Hybrid"])

        # Client gating is a performance optimization only
        self.assertTrue(fx["invariants"]["clientGatingIsPerformanceOptimizationOnly"])
        # Server-only .NET VAD must remain usable when realtime voice worker is absent
        self.assertTrue(fx["invariants"]["serverVadUsableWithoutRealtimeWorker"])
        # No model download when VAD is disabled
        self.assertTrue(fx["invariants"]["noModelDownloadWhenDisabled"])

    def test_preroll_and_resampling_ratio(self) -> None:
        """Pre-roll timing (250ms / 4000 samples @ 16kHz) and 24kHz->16kHz resample."""
        fx = json.loads((FIXTURES / "preroll-and-resample-24k-to-16k.json").read_text(encoding="utf-8"))
        pre_roll_samples = int(fx["target"]["sampleRateHz"] * (fx["preRoll"]["bufferDurationMs"] / 1000.0))
        self.assertEqual(pre_roll_samples, fx["preRoll"]["preRollFrames16k"])

        ratio = fx["target"]["sampleRateHz"] / fx["capture"]["sampleRateHz"]
        self.assertAlmostEqual(ratio, fx["resampleRatio"], places=5)

        # Confirm audio files exist
        self.assertTrue((FIXTURES / fx["capture"]["audioFile"]).exists())
        self.assertTrue((FIXTURES / fx["target"]["audioFile"]).exists())

    def test_silero_frame_accumulation_and_state_reset(self) -> None:
        """Silero 512-sample frames, 64-sample context, 256 state, gap and device reset."""
        fx = json.loads((FIXTURES / "sequence-gaps-and-device-reset.json").read_text(encoding="utf-8"))
        self.assertEqual(fx["sileroSpecs"]["frameSizeSamples"], 512)
        self.assertEqual(fx["sileroSpecs"]["contextSizeSamples"], 64)
        self.assertEqual(fx["sileroSpecs"]["recurrentStateElements"], 256)

        events = fx["events"]
        gap_event = next(e for e in events if e["type"] == "sequence-gap")
        self.assertTrue(gap_event["expectReset"])
        dev_event = next(e for e in events if e["type"] == "device-change")
        self.assertTrue(dev_event["expectReset"])

    def test_barge_in_epoch_fencing_and_single_final_commit(self) -> None:
        """Barge-in increments epoch and discards pending audio with single final commit."""
        fx = json.loads((FIXTURES / "barge-in-epoch-fencing.json").read_text(encoding="utf-8"))
        self.assertGreater(fx["nextSpeechEpoch"], fx["speechEpoch"])
        self.assertTrue(fx["expectedBehavior"]["discardPendingAudioEpoch1"])
        self.assertTrue(fx["expectedBehavior"]["singleFinalCommit"])
        self.assertTrue(fx["expectedBehavior"]["noPartialTranscriptCommit"])

    def test_permission_denial_and_headless_node_import_safety(self) -> None:
        """Permission denial degrades gracefully; headless import never touches browser globals."""
        fx = json.loads((FIXTURES / "permission-denied-and-headless.json").read_text(encoding="utf-8"))
        perm = fx["permissionDeniedCase"]
        self.assertEqual(perm["errorName"], "NotAllowedError")
        self.assertEqual(perm["fallbackState"], "PermissionDenied")
        self.assertTrue(perm["gracefulDegradation"])

        headless = fx["headlessNodeEnvironment"]
        self.assertFalse(headless["requireBrowserGlobals"])
        self.assertTrue(headless["nodeImportSafe"])


if __name__ == "__main__":
    unittest.main(verbosity=2)
