"""Model manifest handling: revision + tokenizer hash, local files only."""

from __future__ import annotations

import hashlib
import json
from dataclasses import dataclass
from pathlib import Path

from openconnect_nllb.runtime_cache import (
    RuntimeModelCacheManager,
    RuntimeModelManifest,
    is_license_approved,
)

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


def _check_versioned_readiness(
    model_dir: Path,
    *,
    license_accepted: bool,
    commercial_use: bool = False,
) -> Readiness:
    nllb_root = model_dir / "nllb"
    manager = RuntimeModelCacheManager(model_dir, "nllb")
    model_roots = sorted(p for p in nllb_root.iterdir() if p.is_dir())
    if not model_roots:
        return Readiness(ready=False, reason="model_unavailable", manifest=None)

    for model_root in model_roots:
        revisions_dir = model_root / "revisions"
        candidate_rev_dirs: list[Path] = []
        active_path = model_root / "active.json"
        if active_path.is_file():
            try:
                active_data = json.loads(active_path.read_text(encoding="utf-8"))
                active_fp = active_data.get("activeFingerprint")
                if isinstance(active_fp, str) and active_fp:
                    candidate_rev_dirs.append(revisions_dir / active_fp)
            except (OSError, ValueError):
                return Readiness(ready=False, reason="cache_corrupt", manifest=None)
        if revisions_dir.is_dir():
            for rev_dir in sorted(p for p in revisions_dir.iterdir() if p.is_dir()):
                if rev_dir not in candidate_rev_dirs:
                    candidate_rev_dirs.append(rev_dir)

        for rev_dir in candidate_rev_dirs:
            lock_path = rev_dir / "manifest.lock.json"
            if not lock_path.is_file():
                return Readiness(ready=False, reason="cache_corrupt", manifest=None)
            try:
                lock_data = json.loads(lock_path.read_text(encoding="utf-8"))
                rt_manifest = RuntimeModelManifest.model_validate(lock_data["manifest"])
            except Exception:
                return Readiness(ready=False, reason="cache_corrupt", manifest=None)

            res = manager.verify_revision(
                rt_manifest,
                license_accepted=license_accepted,
                commercial_use=commercial_use,
            )
            weights_sha = next(
                (a.sha256 for a in rt_manifest.assets if a.kind == "weights"),
                rt_manifest.assets[0].sha256,
            )
            tok_sha = next(
                (a.sha256 for a in rt_manifest.assets if a.kind == "tokenizer"),
                rt_manifest.assets[0].sha256,
            )
            manifest = ModelManifest(
                model_id=rt_manifest.modelId,
                revision=rt_manifest.immutableRevision,
                checksum=weights_sha,
                tokenizer_hash=tok_sha,
                license=rt_manifest.license,
            )
            return Readiness(ready=res.ready, reason=res.reason, manifest=manifest)

    return Readiness(ready=False, reason="model_unavailable", manifest=None)


def check_readiness(
    model_dir: Path,
    *,
    license_accepted: bool,
    commercial_use: bool = False,
) -> Readiness:
    """Truthful readiness: supports versioned RuntimeModelCacheManager layout and flat layout."""
    if (model_dir / "nllb").is_dir():
        return _check_versioned_readiness(
            model_dir,
            license_accepted=license_accepted,
            commercial_use=commercial_use,
        )
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
    approved = (
        manifest.license.lower() in PRE_APPROVED_LICENSES or license_accepted
    ) and is_license_approved(
        manifest.license,
        license_accepted=license_accepted,
        commercial_use=commercial_use,
    )
    if not approved:
        return Readiness(ready=False, reason="license_denied", manifest=manifest)
    return Readiness(ready=True, reason=None, manifest=manifest)
