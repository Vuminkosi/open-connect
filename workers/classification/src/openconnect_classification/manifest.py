"""Model manifest handling: local files only, never downloaded at request time."""

from __future__ import annotations

import hashlib
import json
from dataclasses import dataclass
from pathlib import Path

# Licenses that may run without explicit operator acceptance.
PRE_APPROVED_LICENSES = frozenset({"apache-2.0", "mit", "bsd-3-clause"})


@dataclass(frozen=True)
class ModelManifest:
    model_id: str
    revision: str
    checksum: str
    license: str


@dataclass(frozen=True)
class Readiness:
    ready: bool
    reason: str | None
    manifest: ModelManifest | None


def _weights_present(model_dir: Path) -> Path | None:
    for pattern in ("*.bin", "*.safetensors", "*.onnx"):
        matches = sorted(model_dir.glob(pattern))
        if matches:
            return matches[0]
    return None


def load_manifest(model_dir: Path) -> ModelManifest | None:
    path = model_dir / "manifest.json"
    if not path.is_file():
        return None
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return None
    try:
        return ModelManifest(
            model_id=str(data["modelId"]),
            revision=str(data["revision"]),
            checksum=str(data["checksum"]),
            license=str(data["license"]),
        )
    except KeyError:
        return None


def check_readiness(model_dir: Path, *, license_accepted: bool) -> Readiness:
    """Truthful readiness: missing/invalid manifest or weights => not ready."""
    manifest = load_manifest(model_dir)
    if manifest is None:
        return Readiness(ready=False, reason="model_unavailable", manifest=None)
    if not _weights_present(model_dir):
        return Readiness(ready=False, reason="model_unavailable", manifest=manifest)
    if manifest.license.lower() not in PRE_APPROVED_LICENSES and not license_accepted:
        return Readiness(ready=False, reason="license_denied", manifest=manifest)
    return Readiness(ready=True, reason=None, manifest=manifest)


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(65536), b""):
            digest.update(chunk)
    return digest.hexdigest()
