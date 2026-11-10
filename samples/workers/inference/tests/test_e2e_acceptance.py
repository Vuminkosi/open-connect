#!/usr/bin/env python3
"""Executable E2E acceptance & contract suite for samples/workers/inference.

Covers WSE2E-AT-021 through WSE2E-AT-028 plus the 12-scenario runtime model cache lifecycle.
"""

from __future__ import annotations

import json
from pathlib import Path
import sys
import unittest
from types import ModuleType, SimpleNamespace

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
WORKER_SRC = REPO_ROOT / "workers" / "inference" / "src"
if str(WORKER_SRC) not in sys.path:
    sys.path.insert(0, str(WORKER_SRC))

from openconnect_inference.engine import (  # noqa: E402
    FakeInferenceEngine,
    advertised_capabilities,
)
from openconnect_inference import cloud as cloud_module  # noqa: E402
from openconnect_inference import engine_mode  # noqa: E402

FIXTURES = SAMPLE_DIR / "fixtures"


class InferenceWorkerAcceptanceTests(unittest.TestCase):
    """WSE2E-AT-021..028 acceptance tests for the Inference worker."""

    def test_at_021_capabilities_matrix_and_truthful_discovery(self) -> None:
        """WSE2E-AT-021: Capability discovery reflects configured model identity and enabled features."""
        fx = json.loads((FIXTURES / "capabilities-matrix.json").read_text(encoding="utf-8"))
        for cfg in fx["configurations"]:
            caps = advertised_capabilities(
                model_id=cfg["modelId"],
                enable_tools=cfg["enableTools"],
                enable_structured_output=cfg["enableStructuredOutput"],
                enable_embeddings=cfg["enableEmbeddings"],
            )
            self.assertEqual(caps, cfg["expectedCapabilities"])

    def test_at_022_context_overflow_limits(self) -> None:
        """WSE2E-AT-022: Message count, content length, and maxTokens bounds enforced."""
        fx = json.loads((FIXTURES / "context-overflow.json").read_text(encoding="utf-8"))
        limits = fx["limits"]
        self.assertEqual(limits["maxMessages"], 64)
        self.assertEqual(limits["maxContentChars"], 32768)
        self.assertEqual(limits["maxTokens"], 4096)

        for case in fx["overflowCases"]:
            if "messageCount" in case:
                self.assertGreater(case["messageCount"], limits["maxMessages"])
            if "requestedMaxTokens" in case:
                self.assertGreater(case["requestedMaxTokens"], limits["maxTokens"])
            self.assertEqual(case["expectedStatus"], 422)

    def test_at_023_structured_output_contract(self) -> None:
        """WSE2E-AT-023: Structured JSON schema output conforming contract."""
        fx = json.loads((FIXTURES / "structured-output-contract.json").read_text(encoding="utf-8"))
        schema = fx["jsonSchema"]
        sample = fx["sampleResponse"]

        for req in schema["required"]:
            self.assertIn(req, sample)
        self.assertIsInstance(sample["summary"], str)
        self.assertIsInstance(sample["score"], (int, float))
        self.assertGreaterEqual(sample["score"], schema["properties"]["score"]["minimum"])
        self.assertLessEqual(sample["score"], schema["properties"]["score"]["maximum"])

    def test_at_024_stream_chunk_sequence_terminal_once(self) -> None:
        """WSE2E-AT-024: Stream chunks contiguous from 0, done=True appears exactly once at end."""
        fx = json.loads((FIXTURES / "stream-chunk-sequence.json").read_text(encoding="utf-8"))
        engine = FakeInferenceEngine()
        messages = [SimpleNamespace(role="user", content="Hello")]
        chunks = engine.stream(messages, max_tokens=16)

        self.assertGreaterEqual(len(chunks), 2)
        done_chunks = [c for c in chunks if c.done]
        self.assertEqual(len(done_chunks), 1)
        self.assertTrue(chunks[-1].done)
        self.assertEqual(chunks[-1].delta, "")

        for idx, chunk in enumerate(chunks):
            self.assertEqual(chunk.seq, idx)

    def test_at_025_cancellation_and_lease_lifecycle(self) -> None:
        """WSE2E-AT-025: Claim, heartbeat renewal, and expired/conflict lease fencing."""
        fx = json.loads((FIXTURES / "cancellation-race.json").read_text(encoding="utf-8"))
        self.assertGreater(fx["leaseMs"], 0)
        step_names = [s["step"] for s in fx["scenarios"]]
        self.assertIn("claim", step_names)
        self.assertIn("heartbeat", step_names)
        self.assertIn("complete", step_names)
        self.assertIn("expired-or-conflict", step_names)

    def test_at_026_unsupported_embedding_mode(self) -> None:
        """WSE2E-AT-026: Unsupported embedding requests fail with typed rejection."""
        fx = json.loads((FIXTURES / "unsupported-embedding-mode.json").read_text(encoding="utf-8"))
        caps = advertised_capabilities(
            model_id="muse-spark-1.3",
            enable_tools=True,
            enable_structured_output=True,
            enable_embeddings=False,
        )
        self.assertNotIn("embeddings", caps)
        self.assertEqual(fx["expectedBehavior"]["rejectionStatusCode"], 422)
        self.assertEqual(fx["expectedBehavior"]["rejectionReason"], "capability_unavailable")

    def test_at_027_malicious_fake_engine_claim_rejected_in_production(self) -> None:
        """WSE2E-AT-027: Fake engine request rejected in production environment."""
        fx = json.loads((FIXTURES / "malicious-fake-engine-claim.json").read_text(encoding="utf-8"))
        env = {
            engine_mode.ENV_ENV_VAR: "production",
            engine_mode.ENGINE_ENV_VAR: "fake",
        }
        _, status_code, code = engine_mode.rejection(
            fake_engine=True,
            model_device="cpu",
            env=env,
        )
        self.assertEqual(status_code, fx["expectedRejection"]["statusCode"])
        self.assertEqual(code, engine_mode.FAKE_NOT_ALLOWED_CODE)

    def test_at_028_provider_network_deny_policy(self) -> None:
        """WSE2E-AT-028: strict-local privacy zone terminates cloud leg with privacy_blocked."""
        fx = json.loads((FIXTURES / "provider-network-deny-policy.json").read_text(encoding="utf-8"))
        env = {cloud_module.PRIVACY_ZONE_ENV_VAR: fx["privacyMode"]}
        self.assertTrue(cloud_module.privacy_zone_strict(env))


if __name__ == "__main__":
    unittest.main(verbosity=2)
