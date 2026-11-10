"""Tests for the 12 runtime model cache scenarios in workers-samples-e2e-spec.md §4.2."""

from __future__ import annotations

import concurrent.futures
import hashlib
from pathlib import Path

from openconnect_classification.manifest import check_readiness
from openconnect_classification.runtime_cache import (
    RuntimeModelAsset,
    RuntimeModelCacheManager,
    RuntimeModelManifest,
    compute_manifest_fingerprint,
    verify_dockerfile_no_model_downloads,
)


def _make_manifest(
    revision: str = "rev-cls-1",
    weights: bytes = b"bart-mnli-weights-v1",
    config_bytes: bytes = b'{"labels":4}',
    license_id: str = "mit",
) -> tuple[RuntimeModelManifest, dict[str, bytes]]:
    w_hash = hashlib.sha256(weights).hexdigest()
    c_hash = hashlib.sha256(config_bytes).hexdigest()
    manifest = RuntimeModelManifest(
        workerKind="classification",
        modelId="facebook/bart-large-mnli",
        immutableRevision=revision,
        license=license_id,
        assets=[
            RuntimeModelAsset(
                relativePath="model.safetensors",
                sha256=w_hash,
                sizeBytes=len(weights),
                kind="weights",
            ),
            RuntimeModelAsset(
                relativePath="config.json",
                sha256=c_hash,
                sizeBytes=len(config_bytes),
                kind="config",
            ),
        ],
    )
    return manifest, {"model.safetensors": weights, "config.json": config_bytes}


def _fetcher(blobs: dict[str, bytes], calls: list[int]):
    def _run(asset: RuntimeModelAsset, target: Path) -> None:
        calls.append(1)
        target.write_bytes(blobs[asset.relativePath])

    return _run


def _no_network(_asset: RuntimeModelAsset, _target: Path) -> None:
    raise AssertionError("network download forbidden")


def test_scenario_01_dockerfile_no_build_downloads() -> None:
    dockerfile = Path(__file__).resolve().parents[2] / "Dockerfile"
    assert verify_dockerfile_no_model_downloads(dockerfile.read_text(encoding="utf-8")) == []


def test_scenario_02_disabled_worker_no_init(tmp_path: Path) -> None:
    mgr = RuntimeModelCacheManager(tmp_path, "classification")
    manifest, _ = _make_manifest()
    res = mgr.provision_or_verify(manifest, enabled=False, asset_fetcher=_no_network)
    assert res.ready is False and res.status == "disabled" and res.downloads_attempted == 0
    assert list(tmp_path.iterdir()) == []


def test_scenarios_03_to_07_cache_lifecycle(tmp_path: Path) -> None:
    mgr = RuntimeModelCacheManager(tmp_path, "classification")
    m1, b1 = _make_manifest(revision="rev-1", weights=b"weights-v1")
    fp1 = compute_manifest_fingerprint(m1)

    # Scenario 3: First enabled start
    calls: list[int] = []
    r1 = mgr.provision_or_verify(m1, asset_fetcher=_fetcher(b1, calls), image_tag="v1")
    assert r1.ready is True and r1.fingerprint == fp1 and len(calls) == 2
    readiness = check_readiness(tmp_path, license_accepted=False)
    assert readiness.ready is True and readiness.manifest is not None
    assert readiness.manifest.revision == "rev-1"

    # Scenario 4: Offline restart cache hit
    r_off = mgr.provision_or_verify(m1, offline=True, asset_fetcher=_no_network, image_tag="v1")
    assert r_off.ready is True and r_off.cache_hit is True and r_off.downloads_attempted == 0

    # Scenario 5: Image upgrade with same manifest
    r_up = mgr.provision_or_verify(m1, offline=True, asset_fetcher=_no_network, image_tag="v2")
    assert r_up.ready is True and r_up.downloads_attempted == 0

    # Scenario 6: Model revision change preserves v1
    m2, b2 = _make_manifest(revision="rev-2", weights=b"weights-v2")
    fp2 = compute_manifest_fingerprint(m2)
    r2 = mgr.provision_or_verify(m2, asset_fetcher=_fetcher(b2, []))
    assert r2.ready is True and r2.fingerprint == fp2
    paths = mgr.paths_for(m1.modelId)
    assert paths.revision_dir(fp1).is_dir() and paths.revision_dir(fp2).is_dir()
    assert check_readiness(tmp_path, license_accepted=False).manifest.revision == "rev-2"  # type: ignore[union-attr]

    # Scenario 7: Revert revision offline
    r_rev = mgr.provision_or_verify(m1, offline=True, asset_fetcher=_no_network)
    assert r_rev.ready is True and r_rev.fingerprint == fp1 and r_rev.downloads_attempted == 0
    assert check_readiness(tmp_path, license_accepted=False).manifest.revision == "rev-1"  # type: ignore[union-attr]


def test_scenarios_08_to_12_corruption_interruption_concurrency_request(tmp_path: Path) -> None:
    mgr = RuntimeModelCacheManager(tmp_path, "classification")
    m1, b1 = _make_manifest(revision="rev-1", weights=b"weights-v1")

    # Scenario 10: Concurrent startup
    calls: list[int] = []
    with concurrent.futures.ThreadPoolExecutor(max_workers=3) as pool:
        results = list(
            pool.map(
                lambda _: mgr.provision_or_verify(m1, asset_fetcher=_fetcher(b1, calls)),
                range(3),
            )
        )
    assert all(r.ready for r in results) and len(calls) == 2

    # Scenario 9: Interrupted download preserves v1
    m2, _ = _make_manifest(revision="rev-2", weights=b"weights-v2")

    def _fail(_a: RuntimeModelAsset, _p: Path) -> None:
        raise OSError("interrupted")

    r_fail = mgr.provision_or_verify(m2, asset_fetcher=_fail)
    assert r_fail.ready is False and r_fail.reason == "provision_failed"
    assert mgr.verify_for_request(m1).ready is True

    # Scenario 8: Corrupt file rejected
    assert results[0].fingerprint is not None
    w_path = mgr.paths_for(m1.modelId).assets_dir(results[0].fingerprint) / "model.safetensors"
    w_path.write_bytes(b"corrupted")
    r_corrupt = mgr.provision_or_verify(m1, offline=True, asset_fetcher=_no_network)
    assert r_corrupt.ready is False and r_corrupt.reason == "cache_corrupt"
    assert check_readiness(tmp_path, license_accepted=False).ready is False

    # Scenario 11 & 12: Offline missing & request handler never downloads
    m3, _ = _make_manifest(revision="rev-3", weights=b"weights-v3")
    assert mgr.provision_or_verify(m3, offline=True, asset_fetcher=_no_network).ready is False
    assert mgr.verify_for_request(m3).ready is False
