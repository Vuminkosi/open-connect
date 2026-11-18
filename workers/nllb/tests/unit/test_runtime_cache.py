"""12-scenario runtime model cache and verification tests for the NLLB worker."""

from __future__ import annotations

from concurrent.futures import ThreadPoolExecutor
import hashlib
from pathlib import Path

import pytest

from openconnect_nllb.config import Settings
from openconnect_nllb.engine_mode import (
    FakeNotAllowedInProductionError,
    is_production,
    parse_offline,
    resolve_environment,
)
from openconnect_nllb.manifest import check_readiness
from openconnect_nllb.runtime_cache import (
    RuntimeModelAsset,
    RuntimeModelCacheManager,
    RuntimeModelManifest,
    compute_manifest_fingerprint,
    verify_dockerfile_no_model_downloads,
)

WORKER_ROOT = Path(__file__).resolve().parents[2]
WEIGHTS_BYTES = b"nllb-200-distilled-600M-weights-v1"
TOKENIZER_BYTES = b'{"model":"nllb-sentencepiece-v1"}'


def _sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def _make_manifest(
    *,
    revision: str = "rev-2026-01",
    license_id: str = "cc-by-nc-4.0",
    weights_bytes: bytes = WEIGHTS_BYTES,
    tokenizer_bytes: bytes = TOKENIZER_BYTES,
) -> RuntimeModelManifest:
    return RuntimeModelManifest(
        workerKind="nllb",
        modelId="facebook/nllb-200-distilled-600M",
        immutableRevision=revision,
        license=license_id,
        assets=[
            RuntimeModelAsset(
                relativePath="model.safetensors",
                sha256=_sha256(weights_bytes),
                sizeBytes=len(weights_bytes),
                kind="weights",
            ),
            RuntimeModelAsset(
                relativePath="tokenizer.json",
                sha256=_sha256(tokenizer_bytes),
                sizeBytes=len(tokenizer_bytes),
                kind="tokenizer",
            ),
        ],
    )


def _fetcher_for(
    weights_bytes: bytes = WEIGHTS_BYTES,
    tokenizer_bytes: bytes = TOKENIZER_BYTES,
):
    payloads = {
        "model.safetensors": weights_bytes,
        "tokenizer.json": tokenizer_bytes,
    }

    def _fetch(asset: RuntimeModelAsset, target: Path) -> None:
        target.write_bytes(payloads[asset.relativePath])

    return _fetch


def test_scenario_01_dockerfile_and_compose_never_download_models() -> None:
    dockerfile_text = (WORKER_ROOT / "Dockerfile").read_text(encoding="utf-8")
    assert verify_dockerfile_no_model_downloads(dockerfile_text) == []
    assert "HF_HUB_OFFLINE=1" in dockerfile_text
    assert "TRANSFORMERS_OFFLINE=1" in dockerfile_text
    assert "USER 10001:10001" in dockerfile_text
    assert "HEALTHCHECK" in dockerfile_text

    compose_text = (WORKER_ROOT / "compose.yaml").read_text(encoding="utf-8")
    assert "vuminkosimatibe/openconnect-worker-nllb:${OC_IMAGE_TAG:-e2e-local}" in compose_text
    assert "OC_WORKER_ENGINE: ${OC_WORKER_ENGINE:-real}" in compose_text
    assert "OC_ENVIRONMENT: ${OC_ENVIRONMENT:-Production}" in compose_text
    assert "OC_MODEL_PROVISIONING: ${OC_MODEL_PROVISIONING:-first-start-if-missing}" in compose_text
    assert "OC_MODEL_CACHE_DIR: ${OC_MODEL_CACHE_DIR:-/models}" in compose_text
    assert "OC_MODEL_OFFLINE: ${OC_MODEL_OFFLINE:-false}" in compose_text
    assert "oc_nllb_models:" in compose_text


def test_scenario_02_disabled_worker_creates_zero_directories_and_zero_downloads(
    tmp_path: Path,
) -> None:
    manager = RuntimeModelCacheManager(tmp_path, "nllb")
    manifest = _make_manifest()
    called = 0

    def _ForbiddenFetcher(asset: RuntimeModelAsset, target: Path) -> None:
        nonlocal called
        called += 1

    res_disabled_worker = manager.provision_or_verify(
        manifest,
        enabled=False,
        license_accepted=True,
        asset_fetcher=_ForbiddenFetcher,
    )
    assert res_disabled_worker.ready is False
    assert res_disabled_worker.status == "disabled"
    assert res_disabled_worker.reason == "worker_disabled"
    assert res_disabled_worker.downloads_attempted == 0
    assert called == 0
    assert not (tmp_path / "nllb").exists()

    res_disabled_prov = manager.provision_or_verify(
        manifest,
        enabled=True,
        provisioning="disabled",
        license_accepted=True,
        asset_fetcher=_ForbiddenFetcher,
    )
    assert res_disabled_prov.ready is False
    assert res_disabled_prov.status == "disabled"
    assert res_disabled_prov.downloads_attempted == 0
    assert not (tmp_path / "nllb").exists()


def test_scenario_03_cold_start_first_start_if_missing_stages_verifies_and_activates(
    tmp_path: Path,
) -> None:
    manager = RuntimeModelCacheManager(tmp_path, "nllb")
    manifest = _make_manifest()
    result = manager.provision_or_verify(
        manifest,
        provisioning="first-start-if-missing",
        license_accepted=True,
        asset_fetcher=_fetcher_for(),
    )
    assert result.ready is True
    assert result.status == "ready"
    assert result.cache_hit is False
    assert result.downloads_attempted == 2
    assert result.revision_dir is not None and result.revision_dir.is_dir()

    active = manager.read_active(manifest.modelId)
    assert active is not None
    assert active["activeFingerprint"] == compute_manifest_fingerprint(manifest)

    # Check integration with nllb manifest.check_readiness
    readiness = check_readiness(tmp_path, license_accepted=True)
    assert readiness.ready is True
    assert readiness.manifest is not None
    assert readiness.manifest.revision == "rev-2026-01"


def test_scenario_04_warm_restart_reuses_verified_cache_without_network(tmp_path: Path) -> None:
    manager = RuntimeModelCacheManager(tmp_path, "nllb")
    manifest = _make_manifest()
    manager.provision_or_verify(
        manifest,
        license_accepted=True,
        asset_fetcher=_fetcher_for(),
    )

    def _no_network(asset: RuntimeModelAsset, target: Path) -> None:
        raise AssertionError("network fetcher must not be called on warm cache hit")

    warm = manager.provision_or_verify(
        manifest,
        license_accepted=True,
        asset_fetcher=_no_network,
    )
    assert warm.ready is True
    assert warm.cache_hit is True
    assert warm.downloads_attempted == 0


def test_scenario_05_image_upgrade_preserves_fingerprint_and_reuses_cache(tmp_path: Path) -> None:
    manager = RuntimeModelCacheManager(tmp_path, "nllb")
    manifest = _make_manifest()
    first = manager.provision_or_verify(
        manifest,
        license_accepted=True,
        asset_fetcher=_fetcher_for(),
        image_tag="v0.1.0",
    )
    second = manager.provision_or_verify(
        manifest,
        license_accepted=True,
        asset_fetcher=None,
        image_tag="v0.2.0",
    )
    assert first.fingerprint == second.fingerprint
    assert second.ready is True
    assert second.cache_hit is True
    assert second.downloads_attempted == 0


def test_scenario_06_model_revision_upgrade_keeps_previous_revision_for_rollback(
    tmp_path: Path,
) -> None:
    manager = RuntimeModelCacheManager(tmp_path, "nllb")
    v1 = _make_manifest(revision="rev-v1", weights_bytes=b"weights-v1")
    v2 = _make_manifest(revision="rev-v2", weights_bytes=b"weights-v2-updated")

    res_v1 = manager.provision_or_verify(
        v1,
        license_accepted=True,
        asset_fetcher=_fetcher_for(weights_bytes=b"weights-v1"),
    )
    res_v2 = manager.provision_or_verify(
        v2,
        license_accepted=True,
        asset_fetcher=_fetcher_for(weights_bytes=b"weights-v2-updated"),
    )
    assert res_v1.fingerprint != res_v2.fingerprint
    assert res_v2.ready is True
    assert manager.read_active(v2.modelId)["activeFingerprint"] == res_v2.fingerprint  # type: ignore[index]

    # Previous revision remains intact on disk for offline rollback.
    rollback = manager.verify_revision(v1, license_accepted=True)
    assert rollback.ready is True
    assert rollback.cache_hit is True


def test_scenario_07_corrupted_asset_or_tokenizer_fails_closed(tmp_path: Path) -> None:
    manager = RuntimeModelCacheManager(tmp_path, "nllb")
    manifest = _make_manifest()
    res = manager.provision_or_verify(
        manifest,
        license_accepted=True,
        asset_fetcher=_fetcher_for(),
    )
    assert res.ready is True and res.revision_dir is not None

    # Corrupt tokenizer -> tokenizer_mismatch
    tok_path = res.revision_dir / "assets" / "tokenizer.json"
    tok_path.write_bytes(b'{"model":"tampered-tokenizer"}')
    corrupt_tok = manager.verify_revision(manifest, license_accepted=True)
    assert corrupt_tok.ready is False
    assert corrupt_tok.reason == "tokenizer_mismatch"
    assert check_readiness(tmp_path, license_accepted=True).reason == "tokenizer_mismatch"

    # Restore tokenizer, corrupt weights -> cache_corrupt
    tok_path.write_bytes(TOKENIZER_BYTES)
    weights_path = res.revision_dir / "assets" / "model.safetensors"
    weights_path.write_bytes(b"corrupted")
    corrupt_weights = manager.verify_revision(manifest, license_accepted=True)
    assert corrupt_weights.ready is False
    assert corrupt_weights.reason == "cache_corrupt"


def test_scenario_08_interrupted_staging_download_never_promotes_or_corrupts_active(
    tmp_path: Path,
) -> None:
    manager = RuntimeModelCacheManager(tmp_path, "nllb")
    v1 = _make_manifest(revision="rev-good")
    ok = manager.provision_or_verify(
        v1,
        license_accepted=True,
        asset_fetcher=_fetcher_for(),
    )
    assert ok.ready is True

    v2 = _make_manifest(revision="rev-broken", weights_bytes=b"expected-weights")

    def _broken_fetcher(asset: RuntimeModelAsset, target: Path) -> None:
        target.write_bytes(b"truncated-partial-download")

    failed = manager.provision_or_verify(
        v2,
        license_accepted=True,
        asset_fetcher=_broken_fetcher,
    )
    assert failed.ready is False
    assert failed.reason == "cache_corrupt"
    assert not manager.paths_for(v2.modelId).revision_dir( failed.fingerprint or "" ).exists()
    # Active pointer still points to v1
    assert manager.read_active(v1.modelId)["activeFingerprint"] == ok.fingerprint  # type: ignore[index]


def test_scenario_09_offline_and_verify_existing_only_modes(tmp_path: Path) -> None:
    manager = RuntimeModelCacheManager(tmp_path, "nllb")
    manifest = _make_manifest()

    missing_offline = manager.provision_or_verify(
        manifest,
        offline=True,
        license_accepted=True,
        asset_fetcher=_fetcher_for(),
    )
    assert missing_offline.ready is False
    assert missing_offline.reason == "model_unavailable"
    assert missing_offline.downloads_attempted == 0

    missing_verify_only = manager.provision_or_verify(
        manifest,
        provisioning="verify-existing-only",
        license_accepted=True,
        asset_fetcher=_fetcher_for(),
    )
    assert missing_verify_only.ready is False
    assert missing_verify_only.reason == "model_unavailable"
    assert missing_verify_only.downloads_attempted == 0

    # Populate cache then verify in offline mode
    manager.provision_or_verify(manifest, license_accepted=True, asset_fetcher=_fetcher_for())
    warm_offline = manager.provision_or_verify(
        manifest,
        offline=True,
        provisioning="verify-existing-only",
        license_accepted=True,
        asset_fetcher=None,
    )
    assert warm_offline.ready is True
    assert warm_offline.cache_hit is True


def test_scenario_10_concurrent_startups_serialize_on_fingerprint_lock(tmp_path: Path) -> None:
    manager = RuntimeModelCacheManager(tmp_path, "nllb")
    manifest = _make_manifest()

    def _worker() -> bool:
        res = manager.provision_or_verify(
            manifest,
            license_accepted=True,
            asset_fetcher=_fetcher_for(),
        )
        return res.ready

    with ThreadPoolExecutor(max_workers=4) as pool:
        outcomes = list(pool.map(lambda _: _worker(), range(4)))

    assert outcomes == [True, True, True, True]
    assert manager.verify_revision(manifest, license_accepted=True).ready is True


def test_scenario_11_license_enforcement_blocks_unapproved_and_non_commercial(
    tmp_path: Path,
) -> None:
    manager = RuntimeModelCacheManager(tmp_path, "nllb")
    nc_manifest = _make_manifest(license_id="cc-by-nc-4.0")

    denied = manager.provision_or_verify(
        nc_manifest,
        license_accepted=False,
        asset_fetcher=_fetcher_for(),
    )
    assert denied.ready is False
    assert denied.reason == "license_denied"
    assert denied.downloads_attempted == 0

    commercial_denied = manager.provision_or_verify(
        nc_manifest,
        license_accepted=True,
        commercial_use=True,
        asset_fetcher=_fetcher_for(),
    )
    assert commercial_denied.ready is False
    assert commercial_denied.reason == "license_denied"
    assert commercial_denied.downloads_attempted == 0


def test_scenario_12_request_time_verification_never_downloads(tmp_path: Path) -> None:
    manager = RuntimeModelCacheManager(tmp_path, "nllb")
    manifest = _make_manifest()

    req_missing = manager.verify_for_request(manifest, license_accepted=True)
    assert req_missing.ready is False
    assert req_missing.reason == "model_unavailable"
    assert req_missing.downloads_attempted == 0

    manager.provision_or_verify(manifest, license_accepted=True, asset_fetcher=_fetcher_for())
    req_ready = manager.verify_for_request(manifest, license_accepted=True)
    assert req_ready.ready is True
    assert req_ready.cache_hit is True
    assert req_ready.downloads_attempted == 0


def test_oc_environment_and_offline_settings(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("OC_ENVIRONMENT", "Production")
    monkeypatch.setenv("OC_WORKER_ENGINE", "fake")
    assert is_production() is True
    assert resolve_environment() == "Production"
    with pytest.raises(FakeNotAllowedInProductionError):
        Settings.from_environment()

    monkeypatch.setenv("OC_ENVIRONMENT", "Development")
    monkeypatch.setenv("OC_MODEL_OFFLINE", "true")
    assert parse_offline() is True
    cfg = Settings.from_environment()
    assert cfg.environment == "Development"
    assert cfg.model_offline is True
