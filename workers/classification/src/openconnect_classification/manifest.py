"""Model manifest handling: local files only, never downloaded at request time."""

from __future__ import annotations

from dataclasses import dataclass
import hashlib
import json
from pathlib import Path

from openconnect_classification.runtime_cache import RuntimeModelCacheManager, RuntimeModelManifest

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


def _check_versioned_cache(model_dir: Path, *, license_accepted: bool) -> Readiness | None:
    worker_root = model_dir / "classification"
    if not worker_root.is_dir():
        return None
    mgr = RuntimeModelCacheManager(model_dir, "classification")
    for active_file in sorted(worker_root.glob("*/active.json")):
        try:
            active_data = json.loads(active_file.read_text(encoding="utf-8"))
            model_id = str(active_data["modelId"])
            fp = str(active_data["activeFingerprint"])
            lock_path = mgr.paths_for(model_id).manifest_lock_path(fp)
            lock_data = json.loads(lock_path.read_text(encoding="utf-8"))
            rt_manifest = RuntimeModelManifest.model_validate(lock_data["manifest"])
        except Exception:
            return Readiness(ready=False, reason="cache_corrupt", manifest=None)
        res = mgr.verify_for_request(rt_manifest, license_accepted=license_accepted)
        if not res.ready:
            return Readiness(ready=False, reason=res.reason, manifest=None)
        return Readiness(
            ready=True,
            reason=None,
            manifest=ModelManifest(
                model_id=rt_manifest.modelId,
                revision=rt_manifest.immutableRevision,
                checksum=fp,
                license=rt_manifest.license,
            ),
        )
    return None


def check_readiness(model_dir: Path, *, license_accepted: bool) -> Readiness:
    """Truthful readiness: missing/invalid manifest or weights => not ready."""
    versioned = _check_versioned_cache(model_dir, license_accepted=license_accepted)
    if versioned is not None:
        return versioned
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
