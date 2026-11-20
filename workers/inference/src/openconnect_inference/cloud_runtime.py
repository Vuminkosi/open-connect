"""Startup-only loading of an operator-approved Cloud inference model."""
from __future__ import annotations

import os
from pathlib import Path

from openconnect_inference.cloud_api import cloud_enabled, configured_client
from openconnect_inference.cloud_llama_runtime import CloudLlamaRuntime, load_verified_runtime


def load_cloud_runtime() -> CloudLlamaRuntime | None:
    """Never download a model or expose configuration errors to callers."""
    if (not cloud_enabled() or os.environ.get("OC_WORKER_ENGINE", "real").lower() != "real" or
            os.environ.get("OPENCONNECT_FAKE_ENGINE", "0").lower() not in {"0", "false"} or
            os.environ.get("OPENCONNECT_CLOUD_MODEL_FORMAT", "") != "chatml" or configured_client() is None):
        return None
    try:
        return load_verified_runtime(
            Path(os.environ["OPENCONNECT_CLOUD_MODEL_PATH"]), os.environ["OPENCONNECT_CLOUD_MODEL_ID"],
            os.environ["OPENCONNECT_CLOUD_MODEL_REVISION"], os.environ["OPENCONNECT_CLOUD_MODEL_DIGEST"],
            int(os.environ.get("OPENCONNECT_CLOUD_MODEL_CONTEXT_TOKENS", "4096")),
            int(os.environ.get("OPENCONNECT_CLOUD_MODEL_THREADS", "2")),
            license_accepted=os.environ.get("OPENCONNECT_CLOUD_MODEL_LICENSE_ACCEPTED", "false").lower() == "true",
            artifact_reviewed=os.environ.get("OPENCONNECT_CLOUD_MODEL_ARTIFACT_REVIEWED", "false").lower() == "true")
    except Exception:
        return None
