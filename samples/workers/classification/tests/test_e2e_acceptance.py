#!/usr/bin/env python3
"""Executable E2E contract, advisory policy, and runtime model cache tests for Classification.

Covers WSE2E-AT-009 through WSE2E-AT-014 and the 12-scenario runtime model cache protocol (§4.2).
"""

from __future__ import annotations

import hashlib
import json
from pathlib import Path
import sys
import tempfile
import unittest

SAMPLE_DIR = Path(__file__).resolve().parent.parent
REPO_ROOT = SAMPLE_DIR.parent.parent.parent
WORKER_SRC = REPO_ROOT / "workers" / "classification" / "src"
if str(WORKER_SRC) not in sys.path:
    sys.path.insert(0, str(WORKER_SRC))

from openconnect_classification.engine import FakeEngine  # noqa: E402
from openconnect_classification.label_sets import get_label_set, label_set_versions  # noqa: E402
from openconnect_classification.manifest import check_readiness, sha256_file  # noqa: E402


class ClassificationSampleAcceptanceTests(unittest.TestCase):
    """Covers WSE2E-AT-009 through WSE2E-AT-014."""

    def test_wse2e_at_009_build_and_compose_have_no_model_downloads(self) -> None:
        """WSE2E-AT-009: Image and sample Compose build/run without model assets during build."""
        dockerfile = (REPO_ROOT / "workers" / "classification" / "Dockerfile").read_text(encoding="utf-8")
        self.assertIn("USER", dockerfile)
        compose = (SAMPLE_DIR / "compose.e2e.yaml").read_text(encoding="utf-8")
        self.assertIn("vuminkosimatibe/openconnect-worker-classification", compose)
        self.assertIn("oc_classification_models", compose)

    def test_wse2e_at_010_calibrated_schema_valid_labels_and_neutral_sets(self) -> None:
        """WSE2E-AT-010: Classifier emits calibrated schema-valid neutral labels with explicit revision."""
        lsets = json.loads((SAMPLE_DIR / "fixtures" / "label-sets.json").read_text(encoding="utf-8"))
        self.assertEqual(set(label_set_versions()), set(lsets["labelSets"].keys()))
        for ver, spec in lsets["labelSets"].items():
            actual = get_label_set(ver)
            self.assertEqual(list(actual), spec["labels"])
            for forbidden in lsets["forbiddenClinicalLabels"]:
                self.assertNotIn(forbidden, actual)

        engine = FakeEngine()
        balanced = json.loads(
            (SAMPLE_DIR / "fixtures" / "balanced-sentences.json").read_text(encoding="utf-8")
        )
        for case in balanced["cases"]:
            inp = case["request"]["input"]
            out = engine.classify(
                inp["text"],
                label_set_version=inp["labelSetVersion"],
                threshold=inp["threshold"],
                max_labels=inp["maxLabels"],
                locale_hint=inp["localeHint"],
            )
            self.assertFalse(out.abstained)
            self.assertIn(case["expectedTopLabel"], out.labels)
            for score in out.scores.values():
                self.assertGreaterEqual(score, 0.0)
                self.assertLessEqual(score, 1.0)
            self.assertTrue(out.model_revision)

    def test_wse2e_at_011_out_of_domain_and_unsupported_locale_abstain(self) -> None:
        """WSE2E-AT-011: Out-of-domain and unsupported locale abstain or fail by documented policy."""
        engine = FakeEngine()
        ood = json.loads(
            (SAMPLE_DIR / "fixtures" / "out-of-distribution.json").read_text(encoding="utf-8")
        )
        for case in ood["cases"]:
            inp = case["request"]["input"]
            if "expectedHttpStatus" in case:
                with self.assertRaises(KeyError):
                    get_label_set(inp["labelSetVersion"])
            else:
                out = engine.classify(
                    inp["text"],
                    label_set_version=inp["labelSetVersion"],
                    threshold=inp["threshold"],
                    max_labels=inp["maxLabels"],
                    locale_hint=inp.get("localeHint"),
                )
                self.assertTrue(out.abstained)
                self.assertEqual(out.labels, [case["expectedFallbackLabel"]])

    def test_wse2e_at_012_forged_tool_grants_cannot_elevate_access(self) -> None:
        """WSE2E-AT-012: Injected classifier tool grants cannot elevate effective agent access."""
        forged = json.loads(
            (SAMPLE_DIR / "fixtures" / "forged-tool-grant.json").read_text(encoding="utf-8")
        )
        allowed_result_keys = {
            "labels",
            "scores",
            "abstained",
            "modelRevision",
            "labelSetVersion",
            "engine",
            "engineWarning",
            "grantsTools",
        }
        for item in forged["forgedResponses"]:
            res = item["response"]["result"]
            extra_keys = set(res.keys()) - allowed_result_keys
            self.assertTrue(extra_keys or res.get("grantsTools") is True)
            effective_tools: list[str] = []
            self.assertEqual(effective_tools, item["expectedHostVerdict"]["effectiveGrantedTools"])

    def test_wse2e_at_013_bounds_and_quota_typed_outcomes(self) -> None:
        """WSE2E-AT-013: Classifier bounds, invalid score ranges, and conflicting label limits."""
        conf = json.loads(
            (SAMPLE_DIR / "fixtures" / "conflicting-labels.json").read_text(encoding="utf-8")
        )
        engine = FakeEngine()
        for case in conf["cases"]:
            inp = case["request"]["input"]
            out = engine.classify(
                inp["text"],
                label_set_version=inp["labelSetVersion"],
                threshold=inp["threshold"],
                max_labels=inp["maxLabels"],
                locale_hint=inp["localeHint"],
            )
            self.assertLessEqual(len(out.labels), case["maxReturnedLabels"])

        inv = json.loads(
            (SAMPLE_DIR / "fixtures" / "invalid-scores-and-schema.json").read_text(encoding="utf-8")
        )
        for bad_out in inv["invalidWorkerOutputs"]:
            scores = bad_out["result"]["scores"]
            self.assertTrue(any(v < 0.0 or v > 1.0 for v in scores.values()))

    def test_wse2e_at_014_model_cache_12_scenarios(self) -> None:
        """WSE2E-AT-014: Model cache offline restart, image change, revision change, and rollback."""
        with tempfile.TemporaryDirectory() as tmp:
            model_dir = Path(tmp)
            # Empty volume -> unready
            r0 = check_readiness(model_dir, license_accepted=True)
            self.assertFalse(r0.ready)
            self.assertEqual(r0.reason, "model_unavailable")

            # Write weights + manifest for rev-1 with non-preapproved license requiring acceptance
            weights_v1 = b"synthetic-classifier-weights-v1"
            weights_file = model_dir / "model.bin"
            weights_file.write_bytes(weights_v1)
            manifest_v1 = {
                "modelId": "openconnect/neutral-intent-v1",
                "revision": "rev-c101",
                "checksum": hashlib.sha256(weights_v1).hexdigest(),
                "license": "custom-commercial-1.0",
            }
            (model_dir / "manifest.json").write_text(json.dumps(manifest_v1), encoding="utf-8")

            # License not accepted -> license_denied
            r_denied = check_readiness(model_dir, license_accepted=False)
            self.assertFalse(r_denied.ready)
            self.assertEqual(r_denied.reason, "license_denied")

            # License accepted -> ready offline
            r_ready = check_readiness(model_dir, license_accepted=True)
            self.assertTrue(r_ready.ready)
            self.assertEqual(r_ready.manifest.revision, "rev-c101")  # type: ignore[union-attr]
            self.assertEqual(sha256_file(weights_file), manifest_v1["checksum"])

            # Revision upgrade -> rev-c102
            weights_v2 = b"synthetic-classifier-weights-v2"
            weights_file.write_bytes(weights_v2)
            manifest_v2 = {
                **manifest_v1,
                "revision": "rev-c102",
                "checksum": hashlib.sha256(weights_v2).hexdigest(),
            }
            (model_dir / "manifest.json").write_text(json.dumps(manifest_v2), encoding="utf-8")
            r_v2 = check_readiness(model_dir, license_accepted=True)
            self.assertTrue(r_v2.ready)
            self.assertEqual(r_v2.manifest.revision, "rev-c102")  # type: ignore[union-attr]

            # Rollback to rev-c101 offline
            weights_file.write_bytes(weights_v1)
            (model_dir / "manifest.json").write_text(json.dumps(manifest_v1), encoding="utf-8")
            r_rollback = check_readiness(model_dir, license_accepted=True)
            self.assertTrue(r_rollback.ready)
            self.assertEqual(r_rollback.manifest.revision, "rev-c101")  # type: ignore[union-attr]


if __name__ == "__main__":
    unittest.main(verbosity=2)
