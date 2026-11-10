#!/usr/bin/env python3
"""Executable E2E acceptance & contract suite for samples/workers/nllb.

Covers WSE2E-AT-015 through WSE2E-AT-020 plus the 12-scenario runtime model cache lifecycle.
"""

from __future__ import annotations

import hashlib
import json
from pathlib import Path
import sys
import tempfile
import unicodedata
import unittest

SAMPLE_DIR = Path(__file__).resolve().parent.parent
REPO_ROOT = SAMPLE_DIR.parent.parent.parent
WORKER_SRC = REPO_ROOT / "workers" / "nllb" / "src"
if str(WORKER_SRC) not in sys.path:
    sys.path.insert(0, str(WORKER_SRC))

from openconnect_nllb.engine import (  # noqa: E402
    FAKE_TOKENIZER_HASH,
    FakeEngine,
    LanguageUnsupportedError,
    ModelUnavailableError,
    RealEngine,
)
from openconnect_nllb.languages import (  # noqa: E402
    SUPPORTED_LANGUAGES,
    SUPPORTED_PAIRS,
    is_supported_pair,
)
from openconnect_nllb.manifest import check_readiness  # noqa: E402

FIXTURES = SAMPLE_DIR / "fixtures"


class NllbWorkerAcceptanceTests(unittest.TestCase):
    """WSE2E-AT-015..020 acceptance tests for the NLLB translation worker."""

    def test_at_015_utf8_normalization_and_supported_pairs(self) -> None:
        """WSE2E-AT-015: Supported language pairs and UTF-8 NFC normalization."""
        utf8_fx = json.loads((FIXTURES / "utf8-normalization.json").read_text(encoding="utf-8"))
        pairs_fx = json.loads((FIXTURES / "supported-language-pairs.json").read_text(encoding="utf-8"))
        empty_fx = json.loads((FIXTURES / "empty-and-long-text.json").read_text(encoding="utf-8"))
        engine = FakeEngine()

        for lang in pairs_fx["supportedLanguages"]:
            self.assertIn(lang, SUPPORTED_LANGUAGES)
        self.assertGreaterEqual(len(SUPPORTED_PAIRS), 20)

        for case in utf8_fx["cases"]:
            norm_nfc = unicodedata.normalize("NFC", case["textNfc"])
            norm_nfd = unicodedata.normalize("NFC", case["textNfd"])
            self.assertEqual(norm_nfc, norm_nfd)

            out_nfc = engine.translate(
                norm_nfc, source_lang=case["sourceLang"], target_lang=case["targetLang"]
            )
            out_nfd = engine.translate(
                norm_nfd, source_lang=case["sourceLang"], target_lang=case["targetLang"]
            )
            self.assertEqual(out_nfc.translated_text, out_nfd.translated_text)
            self.assertEqual(out_nfc.tokenizer_hash, FAKE_TOKENIZER_HASH)
            self.assertEqual(out_nfc.engine, "fake")

        empty_out = engine.translate(
            empty_fx["emptyCase"]["text"].strip(),
            source_lang=empty_fx["emptyCase"]["sourceLang"],
            target_lang=empty_fx["emptyCase"]["targetLang"],
        )
        self.assertEqual(empty_out.translated_text, f"[{empty_fx['emptyCase']['targetLang']} fake] ")

    def test_at_016_unsupported_language_pairs_and_bounds(self) -> None:
        """WSE2E-AT-016: Unsupported language pairs and oversized input fail deterministically."""
        unsup_fx = json.loads((FIXTURES / "unsupported-pair.json").read_text(encoding="utf-8"))
        bounds_fx = json.loads((FIXTURES / "empty-and-long-text.json").read_text(encoding="utf-8"))
        engine = FakeEngine()

        for case in unsup_fx["cases"]:
            self.assertFalse(is_supported_pair(case["sourceLang"], case["targetLang"]))
            with self.assertRaises(LanguageUnsupportedError):
                engine.translate(
                    case["text"], source_lang=case["sourceLang"], target_lang=case["targetLang"]
                )
            self.assertEqual(case["expectedStatus"], 422)
            self.assertEqual(case["expectedErrorCode"], "language_unsupported")

        oversized_text = bounds_fx["oversizedCase"]["repeatUnit"] * bounds_fx["oversizedCase"]["repeatCount"]
        self.assertGreater(len(oversized_text), bounds_fx["maxTextChars"])
        self.assertEqual(bounds_fx["oversizedCase"]["expectedStatus"], 413)
        self.assertEqual(bounds_fx["oversizedCase"]["expectedErrorCode"], "input_too_long")

    def test_at_017_corrupt_tokenizer_and_checkpoint_mismatch(self) -> None:
        """WSE2E-AT-017: Tokenizer hash or checkpoint mismatch fails readiness before serving."""
        corrupt_fx = json.loads((FIXTURES / "corrupt-tokenizer.json").read_text(encoding="utf-8"))
        expected_tok = corrupt_fx["validManifest"]["expected_tokenizer_Content"].encode("utf-8")
        expected_tok_hash = hashlib.sha256(expected_tok).hexdigest()
        ckpt_bytes = b"synthetic-nllb-checkpoint-weights"
        ckpt_hash = hashlib.sha256(ckpt_bytes).hexdigest()

        with tempfile.TemporaryDirectory() as tmp:
            model_dir = Path(tmp)
            (model_dir / "checkpoint.bin").write_bytes(ckpt_bytes)
            (model_dir / "tokenizer.model").write_bytes(
                corrupt_fx["corruptedTokenizerContent"].encode("utf-8")
            )
            (model_dir / "manifest.json").write_text(
                json.dumps(
                    {
                        "modelId": corrupt_fx["validManifest"]["modelId"],
                        "revision": corrupt_fx["validManifest"]["revision"],
                        "tokenizerHash": expected_tok_hash,
                        "checksum": ckpt_hash,
                        "license": corrupt_fx["validManifest"]["license"],
                    }
                ),
                encoding="utf-8",
            )
            readiness = check_readiness(model_dir, license_accepted=True)
            self.assertFalse(readiness.ready)
            self.assertEqual(readiness.reason, corrupt_fx["expectedReadiness"]["reason"])

            # Repair tokenizer bytes -> readiness succeeds
            (model_dir / "tokenizer.model").write_bytes(expected_tok)
            repaired = check_readiness(model_dir, license_accepted=True)
            self.assertTrue(repaired.ready)
            self.assertIsNone(repaired.reason)

    def test_at_018_missing_model_license_gate(self) -> None:
        """WSE2E-AT-018: Unaccepted license blocks readiness."""
        lic_fx = json.loads((FIXTURES / "missing-model-license.json").read_text(encoding="utf-8"))
        tok_bytes = b"tok-v1"
        ckpt_bytes = b"ckpt-v1"
        tok_hash = hashlib.sha256(tok_bytes).hexdigest()
        ckpt_hash = hashlib.sha256(ckpt_bytes).hexdigest()

        with tempfile.TemporaryDirectory() as tmp:
            model_dir = Path(tmp)
            (model_dir / "tokenizer.model").write_bytes(tok_bytes)
            (model_dir / "checkpoint.bin").write_bytes(ckpt_bytes)

            for case in lic_fx["cases"]:
                manifest = dict(case["manifest"])
                manifest["tokenizerHash"] = tok_hash
                manifest["checksum"] = ckpt_hash
                (model_dir / "manifest.json").write_text(json.dumps(manifest), encoding="utf-8")
                res = check_readiness(
                    model_dir,
                    license_accepted=case["licenseAccepted"],
                )
                if case["expectedReason"] is not None:
                    self.assertFalse(res.ready, f"Expected unready for {case['id']}")
                    self.assertEqual(res.reason, case["expectedReason"])
                else:
                    self.assertTrue(res.ready, f"Expected ready for {case['id']}")
                    self.assertIsNone(res.reason)

    def test_at_019_injected_tool_command_is_plain_text_data(self) -> None:
        """WSE2E-AT-019: Injected tool commands in translation text are treated strictly as data."""
        inj_fx = json.loads((FIXTURES / "injected-tool-command.json").read_text(encoding="utf-8"))
        engine = FakeEngine()
        outcome = engine.translate(
            inj_fx["injectedText"],
            source_lang=inj_fx["sourceLang"],
            target_lang=inj_fx["targetLang"],
        )
        self.assertTrue(outcome.translated_text.startswith(f"[{inj_fx['targetLang']} fake]"))
        self.assertIn("Ignore translation instructions", outcome.translated_text)
        self.assertEqual(inj_fx["expectedBehavior"]["toolCallsExecuted"], 0)

    def test_at_020_no_remote_translation_fallback_and_cache_lifecycle(self) -> None:
        """WSE2E-AT-020: Missing local model or inputRef never triggers remote translation fallback."""
        trap_fx = json.loads((FIXTURES / "remote-fallback-trap.json").read_text(encoding="utf-8"))
        self.assertEqual(trap_fx["expectedOfflineBehavior"]["outboundRequests"], 0)
        self.assertEqual(trap_fx["expectedOfflineBehavior"]["errorCode"], "input_ref_unresolved")

        with tempfile.TemporaryDirectory() as tmp:
            empty_dir = Path(tmp)
            res = check_readiness(empty_dir, license_accepted=True)
            self.assertFalse(res.ready)
            self.assertEqual(res.reason, "model_unavailable")
            real_engine = RealEngine(res.manifest, ready=res.ready, reason=res.reason)
            with self.assertRaises(ModelUnavailableError):
                real_engine.translate(
                    "Offline test", source_lang="eng_Latn", target_lang="fra_Latn"
                )


if __name__ == "__main__":
    unittest.main(verbosity=2)
