"""Resolve an explicitly active, verified local checkpoint for a future decoder."""

from __future__ import annotations

import json
import re
from dataclasses import dataclass
from pathlib import Path

from openconnect_nllb.runtime_cache import (
    RuntimeModelCacheManager,
    RuntimeModelManifest,
    compute_manifest_fingerprint,
)

_REQUIRED = {
    "config.json": "config",
    "model.safetensors": "weights",
    "tokenizer.json": "tokenizer",
    "tokenizer_config.json": "config",
}
_OPTIONAL = {"generation_config.json", "special_tokens_map.json", "added_tokens.json"}


@dataclass(frozen=True)
class DecoderBundle:
    assets_dir: Path
    model_id: str
    revision: str
    fingerprint: str
    tokenizer_hash: str


def resolve_decoder_bundle(
    cache_root: Path, model_id: str, *, license_accepted: bool, commercial_use: bool = False
) -> tuple[DecoderBundle | None, str | None]:
    """Resolve one active single-file safetensors bundle; never provision or fetch."""
    manager = RuntimeModelCacheManager(cache_root, "nllb")
    try:
        paths = manager.paths_for(model_id)
        active = manager.read_active(model_id)
        if active is None:
            return None, "model_unavailable"
        fingerprint = active.get("activeFingerprint")
        if (
            active.get("workerKind") != "nllb"
            or active.get("modelId") != model_id
            or not isinstance(fingerprint, str)
            or not re.fullmatch(r"[0-9a-f]{64}", fingerprint)
        ):
            return None, "cache_corrupt"
        revision_dir = paths.revision_dir(fingerprint)
        revision_dir.resolve().relative_to(cache_root.resolve())
        if revision_dir.is_symlink() or paths.assets_dir(fingerprint).is_symlink():
            return None, "cache_corrupt"
        data = json.loads(paths.manifest_lock_path(fingerprint).read_text(encoding="utf-8"))
        manifest = RuntimeModelManifest.model_validate(data["manifest"])
        if (
            manifest.workerKind != "nllb"
            or manifest.modelId != model_id
            or active.get("immutableRevision") != manifest.immutableRevision
            or compute_manifest_fingerprint(manifest) != fingerprint
        ):
            return None, "cache_corrupt"
        names = [asset.relativePath for asset in manifest.assets]
        if len(names) != len(set(names)) or not set(_REQUIRED).issubset(names):
            return None, "cache_corrupt"
        if not set(names).issubset(set(_REQUIRED) | _OPTIONAL):
            return None, "cache_corrupt"
        if any(
            asset.kind != _REQUIRED[asset.relativePath]
            for asset in manifest.assets
            if asset.relativePath in _REQUIRED
        ):
            return None, "cache_corrupt"
        verified = manager.verify_revision(
            manifest, license_accepted=license_accepted, commercial_use=commercial_use
        )
        if not verified.ready or verified.revision_dir is None:
            return None, verified.reason or "model_unavailable"
        assets_dir = verified.revision_dir / "assets"
        entries = list(assets_dir.iterdir())
        if any(entry.is_symlink() or not entry.is_file() for entry in entries):
            return None, "cache_corrupt"
        if {entry.name for entry in entries} != set(names):
            return None, "cache_corrupt"
        tokenizer_hash = next(
            asset.sha256.lower()
            for asset in manifest.assets
            if asset.relativePath == "tokenizer.json"
        )
        return DecoderBundle(assets_dir, model_id, manifest.immutableRevision, fingerprint, tokenizer_hash), None
    except (OSError, ValueError, KeyError, TypeError):
        return None, "cache_corrupt"
