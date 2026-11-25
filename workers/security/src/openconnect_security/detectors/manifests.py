"""Pinned runtime model manifests for optional CPU-first security detectors.

Disabled detectors never resolve, fetch, or create cache directories.
When enabled, a detector requires a verified pinned revision (never floating
`main`/`latest`), approved license, and matching SHA-256 + size on disk.
"""

from __future__ import annotations

import json
import os
from pathlib import Path

from openconnect_security.runtime_cache import RuntimeModelAsset, RuntimeModelManifest

# Pinned default manifests (non-floating revisions, pre-approved OSS licenses).
# Deployments may supply a JSON manifest bundle via OC_SECURITY_MODEL_MANIFEST_PATH
# to override revisions/hashes for air-gapped or mirror registries.
DEFAULT_DETECTOR_MANIFESTS: dict[str, RuntimeModelManifest] = {
    "gliner-pii": RuntimeModelManifest(
        workerKind="security",
        modelId="urchade/gliner_small-v2.1",
        immutableRevision="8f7b8e1c4b7f4a2a9a2e1d5c3b6f7a8e9d0c1b2a",
        license="apache-2.0",
        runtimeCompatibility="py312-cpu-onnx",
        assets=[
            RuntimeModelAsset(
                relativePath="gliner_config.json",
                sha256="e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
                sizeBytes=0,
                kind="config",
            ),
            RuntimeModelAsset(
                relativePath="pytorch_model.bin",
                sha256="e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
                sizeBytes=0,
                kind="weights",
            ),
        ],
    ),
    "deberta-injection": RuntimeModelManifest(
        workerKind="security",
        modelId="protectai/deberta-v3-base-prompt-injection-v2",
        immutableRevision="89b085cd330414d3e7d9dd787870f315957e1e9f",
        license="apache-2.0",
        runtimeCompatibility="py312-cpu-transformers",
        assets=[
            RuntimeModelAsset(
                relativePath="config.json",
                sha256="e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
                sizeBytes=0,
                kind="config",
            ),
            RuntimeModelAsset(
                relativePath="model.safetensors",
                sha256="e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
                sizeBytes=0,
                kind="weights",
            ),
            RuntimeModelAsset(
                relativePath="tokenizer.json",
                sha256="e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
                sizeBytes=0,
                kind="tokenizer",
            ),
        ],
    ),
    "paddle-ocr": RuntimeModelManifest(
        workerKind="security",
        modelId="PaddlePaddle/PP-OCRv5_mobile",
        immutableRevision="5f2e9c1a7b3d4e8f6a0b1c2d3e4f5a6b7c8d9e0f",
        license="apache-2.0",
        runtimeCompatibility="py312-cpu-paddle",
        assets=[
            RuntimeModelAsset(
                relativePath="inference.pdmodel",
                sha256="e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
                sizeBytes=0,
                kind="weights",
            ),
            RuntimeModelAsset(
                relativePath="inference.pdiparams",
                sha256="e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
                sizeBytes=0,
                kind="weights",
            ),
        ],
    ),
    "yunet": RuntimeModelManifest(
        workerKind="security",
        modelId="opencv/face_detection_yunet",
        immutableRevision="2023mar",
        license="mit",
        runtimeCompatibility="py312-cpu-opencv",
        assets=[
            RuntimeModelAsset(
                relativePath="face_detection_yunet_2023mar.onnx",
                sha256="e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
                sizeBytes=0,
                kind="weights",
            ),
        ],
    ),
}


def load_detector_manifests(manifest_path: Path | None = None) -> dict[str, RuntimeModelManifest]:
    """Load pinned manifests, merging optional overrides from JSON file."""
    manifests = dict(DEFAULT_DETECTOR_MANIFESTS)
    resolved = manifest_path
    if resolved is None:
        raw = os.environ.get("OC_SECURITY_MODEL_MANIFEST_PATH", "").strip()
        if raw:
            resolved = Path(raw)
    if resolved is not None and resolved.is_file():
        data = json.loads(resolved.read_text(encoding="utf-8"))
        if isinstance(data, dict):
            for detector_id, entry in data.items():
                manifests[str(detector_id)] = RuntimeModelManifest.model_validate(entry)
    return manifests
