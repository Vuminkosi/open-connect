"""Model manifest handling: revision + tokenizer hash, local files only."""

from __future__ import annotations

import hashlib
import json
from dataclasses import dataclass
from pathlib import Path

# NLLB checkpoints carry a non-permissive research license family; the worker
# treats them as requiring explicit operator acceptance.
PRE_APPROVED_LICENSES = frozenset({"apache-2.0", "mit", "bsd-3-clause"})


@dataclass(frozen=True)
class ModelManifest:
    model_id: str
    revision: str
    checksum: str
    tokenizer_hash: str
    license: str


@dataclass(frozen=True)
class Readiness:
    ready: bool
    reason: str | None
    manifest: ModelManifest | None


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(65536), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _weights_present(model_dir: Path) -> Path | None:
    for pattern in ("*.bin", "*.safetensors"):
        matches = sorted(model_dir.glob(pattern))
        if matches:
            return matches[0]
    return None


def _tokenizer_present(model_dir: Path) -> Path | None:
    for name in ("tokenizer.json", "sentencepiece.bpe.model", "tokenizer.model"):
        candidate = model_dir / name
        if candidate.is_file():
            return candidate
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
            tokenizer_hash=str(data["tokenizerHash"]),
            license=str(data["license"]),
        )
    except KeyError:
        return None


def check_readiness(model_dir: Path, *, license_accepted: bool) -> Readiness:
    """Truthful readiness: manifest, weights, tokenizer hash, license."""
    manifest = load_manifest(model_dir)
    if manifest is None:
        return Readiness(ready=False, reason="model_unavailable", manifest=None)
    if _weights_present(model_dir) is None:
        return Readiness(ready=False, reason="model_unavailable", manifest=manifest)
    tokenizer = _tokenizer_present(model_dir)
    if tokenizer is None:
        return Readiness(ready=False, reason="model_unavailable", manifest=manifest)
    if sha256_file(tokenizer) != manifest.tokenizer_hash:
        return Readiness(ready=False, reason="tokenizer_mismatch", manifest=manifest)
    if manifest.license.lower() not in PRE_APPROVED_LICENSES and not license_accepted:
        return Readiness(ready=False, reason="license_denied", manifest=manifest)
    return Readiness(ready=True, reason=None, manifest=manifest)
