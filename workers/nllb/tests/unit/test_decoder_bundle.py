import hashlib
import json

from openconnect_nllb.decoder_bundle import resolve_decoder_bundle
from openconnect_nllb.runtime_cache import RuntimeModelAsset, RuntimeModelCacheManager, RuntimeModelManifest

MODEL = "test/nllb"
ASSETS = {
    "config.json": (b"{}", "config"),
    "model.safetensors": (b"weights", "weights"),
    "tokenizer.json": (b"tokenizer", "tokenizer"),
    "tokenizer_config.json": (b"{}", "config"),
}


def provision(tmp_path, assets=ASSETS, license_id="mit"):
    manifest = RuntimeModelManifest(
        workerKind="nllb",
        modelId=MODEL,
        immutableRevision="revision-1",
        license=license_id,
        assets=[
            RuntimeModelAsset(
                relativePath=name,
                sha256=hashlib.sha256(content).hexdigest(),
                sizeBytes=len(content),
                kind=kind,
            )
            for name, (content, kind) in assets.items()
        ],
    )

    def copy_asset(asset, target):
        target.write_bytes(assets[asset.relativePath][0])

    manager = RuntimeModelCacheManager(tmp_path, "nllb")
    result = manager.provision_or_verify(manifest, license_accepted=True, asset_fetcher=copy_asset)
    assert result.ready
    return manager.paths_for(MODEL), result


def test_only_explicit_active_complete_verified_bundle_is_resolved(tmp_path):
    _, provisioned = provision(tmp_path)
    bundle, reason = resolve_decoder_bundle(tmp_path, MODEL, license_accepted=False)
    assert reason is None and bundle is not None
    assert bundle.fingerprint == provisioned.fingerprint
    assert bundle.assets_dir == provisioned.revision_dir / "assets"
    assert bundle.tokenizer_hash == hashlib.sha256(b"tokenizer").hexdigest()


def test_missing_decoder_configuration_is_rejected(tmp_path):
    provision(tmp_path, {name: value for name, value in ASSETS.items() if name != "config.json"})
    assert resolve_decoder_bundle(tmp_path, MODEL, license_accepted=False) == (None, "cache_corrupt")


def test_unlisted_or_changed_asset_is_rejected(tmp_path):
    _, result = provision(tmp_path)
    extra = result.revision_dir / "assets" / "unlisted.json"
    extra.write_text("{}", encoding="utf-8")
    assert resolve_decoder_bundle(tmp_path, MODEL, license_accepted=False) == (None, "cache_corrupt")
    extra.unlink()
    (result.revision_dir / "assets" / "model.safetensors").write_bytes(b"tampered")
    assert resolve_decoder_bundle(tmp_path, MODEL, license_accepted=False) == (None, "cache_corrupt")


def test_active_identity_mismatch_is_rejected(tmp_path):
    paths, _ = provision(tmp_path)
    active = json.loads(paths.active_path.read_text(encoding="utf-8"))
    active["immutableRevision"] = "different"
    paths.active_path.write_text(json.dumps(active), encoding="utf-8")
    assert resolve_decoder_bundle(tmp_path, MODEL, license_accepted=False) == (None, "cache_corrupt")


def test_noncommercial_license_is_not_approved_for_commercial_use(tmp_path):
    provision(tmp_path, license_id="cc-by-nc-4.0")
    assert resolve_decoder_bundle(tmp_path, MODEL, license_accepted=True, commercial_use=True) == (
        None, "license_denied"
    )


def test_missing_active_model_does_not_create_cache(tmp_path):
    assert resolve_decoder_bundle(tmp_path, MODEL, license_accepted=False) == (None, "model_unavailable")
    assert not (tmp_path / "nllb").exists()
