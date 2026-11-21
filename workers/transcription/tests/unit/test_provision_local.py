import hashlib

import pytest

from openconnect_transcription.provision_local import provision
from openconnect_transcription.runtime_cache import RuntimeModelAsset, RuntimeModelCacheManager, RuntimeModelManifest


def test_import_verifies_local_assets_and_preserves_manifest_on_failure(tmp_path):
    source = tmp_path / "source"
    source.mkdir()
    files = {"model.bin": b"model", "config.json": b"{}", "tokenizer.json": b"{}",
             "preprocessor_config.json": b"{}"}
    for name, data in files.items():
        (source / name).write_bytes(data)
    manifest = RuntimeModelManifest(workerKind="transcription", modelId="local/model", immutableRevision="v1",
        license="mit", assets=[RuntimeModelAsset(relativePath=name, sha256=hashlib.sha256(data).hexdigest(),
        sizeBytes=len(data)) for name, data in files.items()])
    manifest_file = tmp_path / "manifest.json"
    manifest_file.write_text(manifest.model_dump_json(), encoding="utf-8")
    cache = tmp_path / "cache"
    provision(source, manifest_file, cache)
    assert RuntimeModelCacheManager(cache, "transcription").verify_for_request(manifest).ready
    original = (cache / "model-manifest.json").read_bytes()
    (source / "model.bin").write_bytes(b"corrupt")
    changed = manifest.model_copy(update={"immutableRevision": "v2"})
    manifest_file.write_text(changed.model_dump_json(), encoding="utf-8")
    with pytest.raises(ValueError, match="verification failed"):
        provision(source, manifest_file, cache)
    assert (cache / "model-manifest.json").read_bytes() == original
