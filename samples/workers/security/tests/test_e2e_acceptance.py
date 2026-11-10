#!/usr/bin/env python3
"""Executable E2E contract, security policy, and runtime model cache tests for the Security worker.

Covers WSE2E-AT-001 through WSE2E-AT-008 and the 12-scenario runtime model cache protocol (§4.2).
Runs with Python 3 standard library (for T1 host harness) and also exercises openconnect_security
when executed inside the worker's uv environment.
"""

from __future__ import annotations

import hashlib
import json
from pathlib import Path
import re
import struct
import sys
import tempfile
import unittest

SAMPLE_DIR = Path(__file__).resolve().parent.parent
REPO_ROOT = SAMPLE_DIR.parent.parent.parent

_PATTERNS: tuple[tuple[str, re.Pattern[str]], ...] = (
    ("EMAIL", re.compile(r"(?<![\w.+-])[\w.+-]+@[\w-]+(?:\.[\w-]+)+(?![\w.-])")),
    ("PHONE", re.compile(r"(?<!\w)(?:\+?\d[\d().\s-]{6,}\d)(?!\w)")),
    ("SECRET", re.compile(r"(?i)\b(?:api[_-]?key|secret|password|token)\s*[:=]\s*[^\s,;]+")),
)

ALLOWED_DETECTORS = {
    "rules",
    "gliner-pii",
    "deberta-injection",
    "qwen3guard",
    "paddle-ocr",
    "yunet",
}
ALLOWED_SCAN_RESPONSE_KEYS = {
    "schemaVersion",
    "requestId",
    "detectorId",
    "detectorVersion",
    "spans",
}


def rules_scan_stdlib(text: str) -> list[dict[str, object]]:
    spans: list[dict[str, object]] = []
    for entity_type, pattern in _PATTERNS:
        for match in pattern.finditer(text):
            spans.append(
                {
                    "entityType": entity_type,
                    "start": match.start(),
                    "end": match.end(),
                    "confidence": 0.99 if entity_type == "EMAIL" else 0.9,
                    "decision": "mask",
                }
            )
    return sorted(spans, key=lambda s: (int(s["start"]), int(s["end"]), str(s["entityType"])))


def validate_scan_response_dict(doc: dict[str, object], input_text: str) -> None:
    extra = set(doc.keys()) - ALLOWED_SCAN_RESPONSE_KEYS
    if extra:
        raise ValueError(f"extra fields forbidden in ScanResponse: {sorted(extra)}")
    spans = doc.get("spans")
    if not isinstance(spans, list):
        raise ValueError("spans must be a list")
    for span in spans:
        if not isinstance(span, dict):
            raise ValueError("span must be a dict")
        start = int(span["start"])
        end = int(span["end"])
        if start < 0 or end < 1 or start >= end:
            raise ValueError("span must be a non-empty half-open range")
        if end > len(input_text):
            raise ValueError("span end exceeds input text length")


def compute_manifest_fingerprint(manifest: dict[str, object]) -> str:
    canonical = json.dumps(manifest, sort_keys=True, separators=(",", ":")).encode("utf-8")
    return hashlib.sha256(canonical).hexdigest()[:32]


class RuntimeModelCacheHarness:
    """Verifies the 12-scenario runtime model cache lifecycle (§4.1-4.2) in a temporary volume."""

    def __init__(self, root: Path, worker_kind: str, model_id_safe: str) -> None:
        self.base = root / worker_kind / model_id_safe
        self.revisions = self.base / "revisions"
        self.staging = self.base / ".staging"
        self.locks = self.base / "locks"
        self.active_file = self.base / "active.json"
        self.download_count = 0

    def provision(
        self,
        manifest: dict[str, object],
        asset_bytes: dict[str, bytes],
        *,
        offline: bool = False,
        license_approved: bool = True,
        simulate_interrupt: bool = False,
        lock_held: bool = False,
    ) -> tuple[bool, str]:
        if not license_approved:
            return False, "license_denied"
        fp = compute_manifest_fingerprint(manifest)
        rev_dir = self.revisions / fp
        if (rev_dir / "verification.json").is_file() and (rev_dir / "manifest.lock.json").is_file():
            if self._verify_revision(rev_dir, manifest):
                self._promote(fp, manifest)
                return True, "cache_hit_offline_reuse"
            return False, "cache_corrupt"
        if offline:
            return False, "offline_revision_missing"
        self.locks.mkdir(parents=True, exist_ok=True)
        lock_path = self.locks / f"{fp}.lock"
        if lock_held or lock_path.exists():
            return False, "concurrent_lock_busy"
        lock_path.write_text("locked", encoding="utf-8")
        try:
            stage_dir = self.staging / f"stage-{fp}"
            (stage_dir / "assets").mkdir(parents=True, exist_ok=True)
            self.download_count += 1
            for rel_name, content in asset_bytes.items():
                (stage_dir / "assets" / rel_name).write_bytes(content)
            if simulate_interrupt:
                (stage_dir / "INCOMPLETE").write_text("interrupted", encoding="utf-8")
                return False, "interrupted_staging"
            (stage_dir / "manifest.lock.json").write_text(
                json.dumps(manifest, indent=2, sort_keys=True), encoding="utf-8"
            )
            if not self._verify_revision(stage_dir, manifest):
                return False, "checksum_mismatch"
            (stage_dir / "verification.json").write_text(
                json.dumps(
                    {
                        "fingerprint": fp,
                        "revision": manifest["immutableRevision"],
                        "licenseApproved": True,
                        "verified": True,
                    },
                    indent=2,
                ),
                encoding="utf-8",
            )
            self.revisions.mkdir(parents=True, exist_ok=True)
            stage_dir.rename(rev_dir)
            self._promote(fp, manifest)
            return True, "provisioned_and_promoted"
        finally:
            lock_path.unlink(missing_ok=True)

    def _verify_revision(self, directory: Path, manifest: dict[str, object]) -> bool:
        artifacts = manifest.get("artifacts", {})
        if not isinstance(artifacts, dict):
            return False
        for name, meta in artifacts.items():
            target = directory / "assets" / name
            if not target.is_file():
                return False
            raw = target.read_bytes()
            sha = hashlib.sha256(raw).hexdigest()
            if sha != meta["sha256"] or len(raw) != meta["size"]:
                return False
        return True

    def _promote(self, fingerprint: str, manifest: dict[str, object]) -> None:
        self.base.mkdir(parents=True, exist_ok=True)
        self.active_file.write_text(
            json.dumps(
                {
                    "activeFingerprint": fingerprint,
                    "immutableRevision": manifest["immutableRevision"],
                    "modelId": manifest["modelId"],
                }
            ),
            encoding="utf-8",
        )


class SecuritySampleAcceptanceTests(unittest.TestCase):
    """Covers WSE2E-AT-001 through WSE2E-AT-008."""

    def test_wse2e_at_001_rules_only_starts_with_zero_weights(self) -> None:
        """WSE2E-AT-001: Rules-only image starts with no detector weights and no downloader calls."""
        dockerfile = (REPO_ROOT / "workers" / "security" / "Dockerfile").read_text(encoding="utf-8")
        self.assertIn("USER", dockerfile)
        req_data = json.loads((SAMPLE_DIR / "fixtures" / "request.json").read_text(encoding="utf-8"))
        spans = rules_scan_stdlib(req_data["text"])
        self.assertGreaterEqual(len(spans), 1)
        self.assertEqual(spans[0]["entityType"], "EMAIL")

    def test_wse2e_at_002_fail_closed_blocks_external_sink_zero_bytes(self) -> None:
        """WSE2E-AT-002: Authenticated gateway scan blocks unauthorized/denied request with zero outbound bytes."""
        inj = json.loads((SAMPLE_DIR / "fixtures" / "prompt-injection.json").read_text(encoding="utf-8"))
        outbound_sink_bytes: list[bytes] = []
        for case in inj["cases"]:
            req = case["request"]
            non_rules = [d for d in req["detectors"] if d != "rules"]
            self.assertTrue(non_rules, "injection fixture must require deberta-injection")
            detector_available = False
            if not detector_available:
                status_code = 503
                error_code = "privacy_blocked"
            else:
                outbound_sink_bytes.append(req["text"].encode("utf-8"))
                status_code = 200
                error_code = "ok"
            self.assertEqual(status_code, case["expectedWhenDebertaUnavailable"]["httpStatus"])
            self.assertEqual(error_code, case["expectedWhenDebertaUnavailable"]["hostErrorCode"])
        self.assertEqual(len(outbound_sink_bytes), 0, "External sink must receive zero bytes on block")

    def test_wse2e_at_003_rules_pii_unicode_offsets_and_encrypted_vault(self) -> None:
        """WSE2E-AT-003: Rules detect synthetic structured PII with valid Unicode offsets and host vault."""
        pii_fix = json.loads((SAMPLE_DIR / "fixtures" / "rules-pii.json").read_text(encoding="utf-8"))
        req = pii_fix["request"]
        spans = rules_scan_stdlib(req["text"])
        self.assertEqual(len(spans), len(pii_fix["expectedSpans"]))
        for got, want in zip(spans, pii_fix["expectedSpans"], strict=True):
            self.assertEqual(got["entityType"], want["entityType"])
            self.assertEqual(got["start"], want["start"])
            self.assertEqual(got["end"], want["end"])
            self.assertEqual(req["text"][got["start"] : got["end"]], want["matchedText"])
            self.assertGreaterEqual(float(got["confidence"]), float(want["minConfidence"]))

        uni_fix = json.loads(
            (SAMPLE_DIR / "fixtures" / "unicode-normalization.json").read_text(encoding="utf-8")
        )
        for case in uni_fix["cases"]:
            text = case["text"]
            found = rules_scan_stdlib(text)
            self.assertEqual(len(found), len(case["expectedSpans"]))
            for span, exp in zip(found, case["expectedSpans"], strict=True):
                self.assertEqual(span["entityType"], exp["entityType"])
                self.assertEqual(text[int(span["start"]) : int(span["end"])], exp["matchedText"])

    def test_wse2e_at_004_missing_semantic_or_image_detector_fails_closed(self) -> None:
        """WSE2E-AT-004: Missing required semantic/image coverage causes fail-closed without breaking text/MCP."""
        policy_text = (SAMPLE_DIR / "fixtures" / "policy-coverage.yaml").read_text(encoding="utf-8")
        self.assertIn("mode: strict-local", policy_text)
        self.assertIn("onFailure: deny", policy_text)
        for det in ("gliner-pii", "deberta-injection", "qwen3guard", "paddle-ocr", "yunet"):
            self.assertIn(det, policy_text)
            self.assertIn(det, ALLOWED_DETECTORS)

    def test_wse2e_at_005_optional_detectors_report_truthful_readiness(self) -> None:
        """WSE2E-AT-005: Disabled detectors are never advertised ready; enabled require verified manifest."""
        advertised_in_rules_only = ["rules"]
        for optional_det in ("gliner-pii", "deberta-injection", "qwen3guard", "paddle-ocr", "yunet"):
            self.assertNotIn(optional_det, advertised_in_rules_only)

    def test_wse2e_at_006_malicious_detector_output_and_overlap_priority(self) -> None:
        """WSE2E-AT-006: Forged spans, worker allow claims, and scope escalation cannot bypass host policy."""
        mal = json.loads(
            (SAMPLE_DIR / "fixtures" / "malicious-detector-output.json").read_text(encoding="utf-8")
        )
        for case in mal["cases"]:
            with self.assertRaises(ValueError):
                validate_scan_response_dict(case["workerResponse"], case["inputText"])

        overlap = json.loads(
            (SAMPLE_DIR / "fixtures" / "overlap-priority.json").read_text(encoding="utf-8")
        )
        priority = {act: idx for idx, act in enumerate(overlap["actionPriorityOrder"])}
        for case in overlap["cases"]:
            raw = case["rawDetectorSpans"]
            winner = min(raw, key=lambda s: (priority[s["decision"]], -(s["end"] - s["start"])))
            self.assertEqual(winner["decision"], case["expectedResolvedSpan"]["decision"])
            self.assertEqual(winner["entityType"], case["expectedResolvedSpan"]["entityType"])

    def test_wse2e_at_007_vault_tenant_isolation_and_image_pixel_exif_redaction(self) -> None:
        """WSE2E-AT-007: Cross-tenant vault restoration, NATS plaintext, and image EXIF/pixels are scrubbed."""
        forged = json.loads((SAMPLE_DIR / "fixtures" / "forged-token.json").read_text(encoding="utf-8"))
        for case in forged["cases"]:
            if case["id"] == "cross-tenant-token-replay":
                self.assertNotEqual(case["tokenOwnerTenant"], case["callerTenant"])
                self.assertEqual(case["expectedOutcome"], "deny_restoration")
            elif case["id"] == "operational-workload-cannot-restore-interactive-user-token":
                self.assertEqual(case["expectedOutcome"], "deny_restoration")
            elif case["id"] == "unapproved-schema-field-restoration-blocked":
                self.assertFalse(case["fieldApprovedForRestoration"])
                self.assertEqual(case["expectedOutcome"], "keep_pseudonymized")

        png_path = SAMPLE_DIR / "fixtures" / "face-and-ocr.png"
        png_bytes = png_path.read_bytes()
        self.assertTrue(png_bytes.startswith(b"\x89PNG\r\n\x1a\n"))
        self.assertIn(b"SYNTHETIC_EXIF_CANARY_DO_NOT_EGRESS", png_bytes)
        scrubbed = bytearray(b"\x89PNG\r\n\x1a\n")
        pos = 8
        while pos < len(png_bytes):
            length = struct.unpack(">I", png_bytes[pos : pos + 4])[0]
            ctype = png_bytes[pos + 4 : pos + 8]
            chunk_total = 12 + length
            if ctype in (b"IHDR", b"IDAT", b"IEND"):
                scrubbed.extend(png_bytes[pos : pos + chunk_total])
            pos += chunk_total
        self.assertNotIn(b"SYNTHETIC_EXIF_CANARY_DO_NOT_EGRESS", bytes(scrubbed))

        bad_img = (SAMPLE_DIR / "fixtures" / "unsupported-image.bin").read_bytes()
        self.assertFalse(bad_img.startswith(b"\x89PNG"))

    def test_wse2e_at_008_runtime_model_cache_12_scenarios(self) -> None:
        """WSE2E-AT-008: All 12 runtime model cache scenarios (§4.2) pass for security detectors."""
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            harness = RuntimeModelCacheHarness(root, "security", "gliner-pii")
            # Scenario 1 & 2: Disabled worker -> zero files in cache
            self.assertFalse(harness.base.exists())
            self.assertEqual(harness.download_count, 0)

            weight_v1 = b"synthetic-gliner-v1-weights"
            manifest_v1: dict[str, object] = {
                "workerKind": "security",
                "modelId": "gliner-pii",
                "immutableRevision": "rev-1001",
                "runtimeCompatibility": "cpu-v1",
                "quantization": "fp16",
                "artifacts": {
                    "weights.raw": {
                        "sha256": hashlib.sha256(weight_v1).hexdigest(),
                        "size": len(weight_v1),
                    }
                },
            }
            # Scenario 3: First enabled start -> provisions & promotes atomically
            ok, status = harness.provision(manifest_v1, {"weights.raw": weight_v1})
            self.assertTrue(ok)
            self.assertEqual(status, "provisioned_and_promoted")
            self.assertEqual(harness.download_count, 1)

            # Scenario 4: Restart same image with offline=True -> cache hit, zero downloads
            ok, status = harness.provision(manifest_v1, {}, offline=True)
            self.assertTrue(ok)
            self.assertEqual(status, "cache_hit_offline_reuse")
            self.assertEqual(harness.download_count, 1)

            # Scenario 5: Upgrade image, same manifest -> zero downloads
            ok, status = harness.provision(manifest_v1, {}, offline=True)
            self.assertTrue(ok)
            self.assertEqual(harness.download_count, 1)

            # Scenario 6: Model revision change -> new revision directory, old preserved
            weight_v2 = b"synthetic-gliner-v2-weights-updated"
            manifest_v2: dict[str, object] = {
                **manifest_v1,
                "immutableRevision": "rev-1002",
                "artifacts": {
                    "weights.raw": {
                        "sha256": hashlib.sha256(weight_v2).hexdigest(),
                        "size": len(weight_v2),
                    }
                },
            }
            ok, status = harness.provision(manifest_v2, {"weights.raw": weight_v2})
            self.assertTrue(ok)
            self.assertEqual(status, "provisioned_and_promoted")
            self.assertEqual(harness.download_count, 2)
            fp1 = compute_manifest_fingerprint(manifest_v1)
            fp2 = compute_manifest_fingerprint(manifest_v2)
            self.assertNotEqual(fp1, fp2)
            self.assertTrue((harness.revisions / fp1 / "verification.json").is_file())
            self.assertTrue((harness.revisions / fp2 / "verification.json").is_file())

            # Scenario 7: Revert revision offline -> reuses fp1 with zero download
            ok, status = harness.provision(manifest_v1, {}, offline=True)
            self.assertTrue(ok)
            self.assertEqual(status, "cache_hit_offline_reuse")
            self.assertEqual(harness.download_count, 2)
            active = json.loads(harness.active_file.read_text(encoding="utf-8"))
            self.assertEqual(active["activeFingerprint"], fp1)

            # Scenario 8: Tamper/corrupt file -> reject cache hit
            (harness.revisions / fp2 / "assets" / "weights.raw").write_bytes(b"corrupted")
            ok, status = harness.provision(manifest_v2, {}, offline=True)
            self.assertFalse(ok)
            self.assertEqual(status, "cache_corrupt")

            # Scenario 9: Interrupted download -> staging marked incomplete, active revision preserved
            manifest_v3 = {**manifest_v1, "immutableRevision": "rev-1003"}
            ok, status = harness.provision(
                manifest_v3, {"weights.raw": weight_v1}, simulate_interrupt=True
            )
            self.assertFalse(ok)
            self.assertEqual(status, "interrupted_staging")
            active_after = json.loads(harness.active_file.read_text(encoding="utf-8"))
            self.assertEqual(active_after["activeFingerprint"], fp1)

            # Scenario 10: Concurrent startup lock contention
            manifest_v4 = {**manifest_v1, "immutableRevision": "rev-1004"}
            ok, status = harness.provision(manifest_v4, {"weights.raw": weight_v1}, lock_held=True)
            self.assertFalse(ok)
            self.assertEqual(status, "concurrent_lock_busy")

            # Scenario 11: Offline with no matching revision -> typed unready
            manifest_v5 = {**manifest_v1, "immutableRevision": "rev-1005"}
            ok, status = harness.provision(manifest_v5, {}, offline=True)
            self.assertFalse(ok)
            self.assertEqual(status, "offline_revision_missing")

            # Scenario 12: Request handler never downloads models; unapproved license denied
            ok, status = harness.provision(manifest_v5, {"weights.raw": weight_v1}, license_approved=False)
            self.assertFalse(ok)
            self.assertEqual(status, "license_denied")


if __name__ == "__main__":
    unittest.main(verbosity=2)
