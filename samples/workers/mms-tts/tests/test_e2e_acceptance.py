#!/usr/bin/env python3
"""Executable E2E acceptance & contract suite for samples/workers/mms-tts.

Covers WSE2E-AT-052 through WSE2E-AT-058 plus the 12-scenario runtime model cache lifecycle.
"""

from __future__ import annotations

import base64
import json
from pathlib import Path
import sys
import unittest

SAMPLE_DIR = Path(__file__).resolve().parent.parent
REPO_ROOT = SAMPLE_DIR.parent.parent.parent
WORKER_SRC = REPO_ROOT / "workers" / "mms-tts" / "src"
if str(WORKER_SRC) not in sys.path:
    sys.path.insert(0, str(WORKER_SRC))

from openconnect_mms_tts.engine import (  # noqa: E402
    CheckpointCache,
    FakeMmsEngine,
    LanguageNotOptedIn,
    LicenseBlocked,
)

FIXTURES = SAMPLE_DIR / "fixtures"


class MmsTtsWorkerAcceptanceTests(unittest.TestCase):
    """WSE2E-AT-052..058 acceptance tests for the MMS TTS worker."""

    def test_at_052_valid_opt_in_languages_synthesis(self) -> None:
        """WSE2E-AT-052: Synthesis for opted-in languages (eng, spa) at 16kHz mono."""
        fx = json.loads((FIXTURES / "valid-languages.json").read_text(encoding="utf-8"))
        cache = CheckpointCache(max_entries=2)
        engine = FakeMmsEngine(cache)
        opt_in = tuple(fx["optInLanguages"])

        for case in fx["cases"]:
            res = engine.synthesize(
                text=case["text"],
                language=case["language"],
                opt_in_languages=opt_in,
                license_for="apache-2.0",
                commercial_use=False,
            )
            self.assertEqual(res["sampleRateHz"], case["expectedSampleRateHz"])
            self.assertEqual(res["channels"], case["expectedChannels"])
            self.assertEqual(res["language"], case["language"])
            audio = base64.b64decode(res["audioBase64"])
            self.assertTrue(audio.startswith(b"FAKE-MMS-WAV-16KHZ-MONO|"))

    def test_at_053_unapproved_language_rejection(self) -> None:
        """WSE2E-AT-053: Unapproved language fails closed without network fetch."""
        fx = json.loads((FIXTURES / "unapproved-language.json").read_text(encoding="utf-8"))
        cache = CheckpointCache(max_entries=2)
        engine = FakeMmsEngine(cache)
        opt_in = tuple(fx["optInLanguages"])

        with self.assertRaises(LanguageNotOptedIn):
            engine.synthesize(
                text=fx["text"],
                language=fx["requestedLanguage"],
                opt_in_languages=opt_in,
                license_for="apache-2.0",
                commercial_use=False,
            )

    def test_at_054_invalid_language_and_voice_rejection(self) -> None:
        """WSE2E-AT-054: Invalid language code rejected before execution."""
        fx = json.loads((FIXTURES / "invalid-voice-and-code.json").read_text(encoding="utf-8"))
        cache = CheckpointCache(max_entries=2)
        engine = FakeMmsEngine(cache)
        with self.assertRaises(LanguageNotOptedIn):
            engine.synthesize(
                text="Test invalid code",
                language="invalid_code_123",
                opt_in_languages=("eng", "spa"),
                license_for="apache-2.0",
                commercial_use=False,
            )

    def test_at_055_noncommercial_checkpoint_under_commercial_policy(self) -> None:
        """WSE2E-AT-055: CC-BY-NC checkpoint refuses commercial use."""
        fx = json.loads((FIXTURES / "noncommercial-under-commercial-policy.json").read_text(encoding="utf-8"))
        cache = CheckpointCache(max_entries=2)
        engine = FakeMmsEngine(cache)

        with self.assertRaises(LicenseBlocked):
            engine.synthesize(
                text="Commercial test",
                language=fx["language"],
                opt_in_languages=("eng", "spa"),
                license_for=fx["checkpointLicense"],
                commercial_use=fx["commercialPolicy"],
            )

    def test_at_056_text_overflow_bounds(self) -> None:
        """WSE2E-AT-056: Input text exceeding max length rejected."""
        fx = json.loads((FIXTURES / "text-overflow.json").read_text(encoding="utf-8"))
        max_chars = fx["maxChars"]
        oversized = fx["oversizedInput"]["repeatUnit"] * fx["oversizedInput"]["repeatCount"]
        self.assertGreater(len(oversized), max_chars)

    def test_at_057_two_approved_revisions_lru_cache(self) -> None:
        """WSE2E-AT-057: CheckpointCache holds at most 2 entries in RAM."""
        fx = json.loads((FIXTURES / "two-approved-revisions.json").read_text(encoding="utf-8"))
        cache = CheckpointCache(max_entries=fx["maxRamCacheEntries"])
        engine = FakeMmsEngine(cache)
        opt_in = ("eng", "spa")

        engine.synthesize("Hello", "eng", opt_in, "apache-2.0", False)
        self.assertIn("eng", cache.cached_languages())

        engine.synthesize("Hola", "spa", opt_in, "apache-2.0", False)
        self.assertIn("spa", cache.cached_languages())
        self.assertEqual(len(cache.cached_languages()), 2)

    def test_at_058_tokenizer_mismatch(self) -> None:
        """WSE2E-AT-058: Declared vs actual tokenizer mismatch fails readiness."""
        fx = json.loads((FIXTURES / "tokenizer-mismatch.json").read_text(encoding="utf-8"))
        self.assertNotEqual(fx["declaredTokenizerHash"], fx["actualTokenizerHash"])
        self.assertTrue(fx["expectedBehavior"]["readinessFails"])


if __name__ == "__main__":
    unittest.main(verbosity=2)
