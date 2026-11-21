"""Import operator-supplied model files into the verified cache, without networking."""

from __future__ import annotations

import argparse
import os
import shutil
import uuid
from pathlib import Path

from openconnect_transcription.runtime_cache import RuntimeModelCacheManager, RuntimeModelManifest


def provision(source: Path, manifest_file: Path, cache: Path) -> None:
    source = source.resolve(strict=True)
    if not source.is_dir() or manifest_file.stat().st_size > 1_048_576:
        raise ValueError("invalid model source or manifest")
    manifest = RuntimeModelManifest.model_validate_json(manifest_file.read_bytes())
    assets = {asset.relativePath for asset in manifest.assets}
    required = {"model.bin", "config.json", "tokenizer.json", "preprocessor_config.json"}
    if manifest.workerKind != "transcription" or not required.issubset(assets):
        raise ValueError("manifest does not describe a compatible transcription model")

    def copy_asset(asset, target: Path) -> None:
        original = (source / asset.relativePath).resolve(strict=True)
        if not original.is_relative_to(source) or not original.is_file():
            raise ValueError("model asset is outside the source directory")
        with original.open("rb") as reader, target.open("xb") as writer:
            shutil.copyfileobj(reader, writer, length=1024 * 1024)

    result = RuntimeModelCacheManager(cache, "transcription").provision_or_verify(
        manifest, asset_fetcher=copy_asset)
    if not result.ready:
        raise ValueError(f"model verification failed: {result.reason}")
    cache.mkdir(parents=True, exist_ok=True)
    temporary = cache / f".model-manifest-{uuid.uuid4().hex}.tmp"
    try:
        temporary.write_text(manifest.model_dump_json(), encoding="utf-8")
        os.replace(temporary, cache / "model-manifest.json")
    finally:
        temporary.unlink(missing_ok=True)


def main() -> None:
    parser = argparse.ArgumentParser(description="Import a local transcription model into the verified cache.")
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--cache", type=Path, required=True)
    args = parser.parse_args()
    provision(args.source, args.manifest, args.cache)
    print("Verified transcription model imported.")


if __name__ == "__main__":
    main()
