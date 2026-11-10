"""Runtime model cache and OC_SECURITY_* contract tests for the security worker."""

from __future__ import annotations

import concurrent.futures
import hashlib
from pathlib import Path

from fastapi.testclient import TestClient
import pytest

from openconnect_security import app as app_module
from openconnect_security import engine_mode
from openconnect_security.app import app
from openconnect_security.config import MODEL_BACKED_DETECTORS, Settings
from openconnect_security.runtime_cache import (
    RuntimeModelAsset,
    RuntimeModelCacheManager,
    RuntimeModelManifest,
    compute_manifest_fingerprint,
    verify_dockerfile_no_model_downloads,
)

client = TestClient(app)
AUTH = {"Authorization": "Bearer local-test-token"}


@pytest.fixture(autouse=True)
def _reset_env(monkeypatch):
    engine_mode._legacy_warned.clear()
    for var in (
        "OC_WORKER_ENGINE",
        "OPENCONNECT_FAKE_ENGINE",
        "OPENCONNECT_ENV",
        "OC_ENVIRONMENT",
        "OC_MODEL_DEVICE",
        "OC_GPU_ALLOWED",
        "OC_MODEL_PROVISIONING",
        "OC_MODEL_CACHE_DIR",
        "MODEL_DIR",
        "OC_MODEL_OFFLINE",
        "OC_SECURITY_RULES_ENABLED",
        "OC_SECURITY_GLINER_ENABLED",
        "OC_SECURITY_DEBERTA_ENABLED",
        "OC_SECURITY_QWEN3GUARD_ENABLED",
        "OC_SECURITY_PADDLEOCR_ENABLED",
        "OC_SECURITY_YUNET_ENABLED",
    ):
        monkeypatch.delenv(var, raising=False)
    yield


def _make_manifest(
    model_id: str = MODEL_BACKED_DETECTORS["gliner-pii"],
    revision: str = "rev-gliner-1",
    weights: bytes = b"gliner-weights-v1",
) -> tuple[RuntimeModelManifest, dict[str, bytes]]:
    digest = hashlib.sha256(weights).hexdigest()
    manifest = RuntimeModelManifest(
        workerKind="security",
        modelId=model_id,
        immutableRevision=revision,
        license="apache-2.0",
        assets=[
            RuntimeModelAsset(
                relativePath="model.safetensors",
                sha256=digest,
                sizeBytes=len(weights),
                kind="weights",
            )
        ],
    )
    return manifest, {"model.safetensors": weights}


def _fetcher(blobs: dict[str, bytes], calls: list[int]):
    def _run(asset: RuntimeModelAsset, target: Path) -> None:
        calls.append(1)
        target.write_bytes(blobs[asset.relativePath])

    return _run


def _no_network(_asset: RuntimeModelAsset, _target: Path) -> None:
    raise AssertionError("network must not be called")


def test_scenario_01_dockerfile_no_model_downloads() -> None:
    dockerfile = Path(__file__).resolve().parents[2] / "Dockerfile"
    assert verify_dockerfile_no_model_downloads(dockerfile.read_text(encoding="utf-8")) == []


def test_scenario_02_disabled_detectors_no_init_or_advertisement(
    monkeypatch, tmp_path: Path
) -> None:
    mgr = RuntimeModelCacheManager(tmp_path, "security")
    manifest, _ = _make_manifest()
    res = mgr.provision_or_verify(manifest, enabled=False, asset_fetcher=_no_network)
    assert res.ready is False and res.status == "disabled" and res.downloads_attempted == 0
    assert list(tmp_path.iterdir()) == []

    monkeypatch.setenv("OPENCONNECT_SCAN_TOKEN", "local-test-token")
    monkeypatch.setattr(app_module, "settings", Settings.from_environment())
    ready_body = client.get("/health/ready").json()
    assert ready_body == {"status": "ready", "capabilities": ["rules"]}


def test_scenarios_03_to_07_gliner_detector_cache_lifecycle(
    monkeypatch, tmp_path: Path
) -> None:
    mgr = RuntimeModelCacheManager(tmp_path, "security")
    manifest_v1, blobs_v1 = _make_manifest(revision="rev-1", weights=b"weights-v1")
    fp_v1 = compute_manifest_fingerprint(manifest_v1)

    # Scenario 3: First enabled start provisions and promotes atomically
    calls: list[int] = []
    r1 = mgr.provision_or_verify(
        manifest_v1, asset_fetcher=_fetcher(blobs_v1, calls), image_tag="v1"
    )
    assert r1.ready is True and r1.fingerprint == fp_v1 and len(calls) == 1

    # Verify security worker reports ready for gliner-pii when cache is populated
    monkeypatch.setenv("OPENCONNECT_SCAN_TOKEN", "local-test-token")
    monkeypatch.setenv("OC_MODEL_CACHE_DIR", str(tmp_path))
    monkeypatch.setenv("OC_SECURITY_GLINER_ENABLED", "true")
    monkeypatch.setattr(app_module, "settings", Settings.from_environment())
    ready_body = client.get("/health/ready").json()
    assert ready_body["status"] == "ready"
    assert "gliner-pii" in ready_body["capabilities"]

    # Scenario 4: Offline restart cache hit (zero network calls)
    r_offline = mgr.provision_or_verify(
        manifest_v1, offline=True, asset_fetcher=_no_network, image_tag="v1"
    )
    assert r_offline.ready is True and r_offline.cache_hit is True
    assert r_offline.downloads_attempted == 0

    # Scenario 5: Image upgrade with same manifest -> zero downloads
    r_upgrade = mgr.provision_or_verify(
        manifest_v1, offline=True, asset_fetcher=_no_network, image_tag="v2"
    )
    assert r_upgrade.ready is True and r_upgrade.downloads_attempted == 0

    # Scenario 6: Model revision change -> preserves v1, activates v2
    manifest_v2, blobs_v2 = _make_manifest(revision="rev-2", weights=b"weights-v2")
    fp_v2 = compute_manifest_fingerprint(manifest_v2)
    r2 = mgr.provision_or_verify(manifest_v2, asset_fetcher=_fetcher(blobs_v2, []))
    assert r2.ready is True and r2.fingerprint == fp_v2
    paths = mgr.paths_for(manifest_v1.modelId)
    assert paths.revision_dir(fp_v1).is_dir()
    assert paths.revision_dir(fp_v2).is_dir()

    # Scenario 7: Offline revert to v1 -> zero downloads
    r_revert = mgr.provision_or_verify(manifest_v1, offline=True, asset_fetcher=_no_network)
    assert r_revert.ready is True and r_revert.fingerprint == fp_v1
    assert r_revert.downloads_attempted == 0


def test_scenarios_08_to_12_corruption_interruption_concurrency_and_request_guard(
    monkeypatch, tmp_path: Path
) -> None:
    mgr = RuntimeModelCacheManager(tmp_path, "security")
    manifest_v1, blobs_v1 = _make_manifest(revision="rev-1", weights=b"weights-v1")

    # Scenario 10: Concurrent startup lock
    calls: list[int] = []
    with concurrent.futures.ThreadPoolExecutor(max_workers=3) as pool:
        results = list(
            pool.map(
                lambda _: mgr.provision_or_verify(
                    manifest_v1, asset_fetcher=_fetcher(blobs_v1, calls)
                ),
                range(3),
            )
        )
    assert all(r.ready for r in results)
    assert len(calls) == 1

    # Scenario 9: Interrupted download for v2 preserves active v1
    manifest_v2, _ = _make_manifest(revision="rev-2", weights=b"weights-v2")

    def _fail_fetch(_a: RuntimeModelAsset, _p: Path) -> None:
        raise OSError("disk full")

    r_fail = mgr.provision_or_verify(manifest_v2, asset_fetcher=_fail_fetch)
    assert r_fail.ready is False and r_fail.reason == "provision_failed"
    assert mgr.verify_for_request(manifest_v1).ready is True

    # Scenario 8: Corrupt file rejected
    assert results[0].fingerprint is not None
    asset_file = (
        mgr.paths_for(manifest_v1.modelId).assets_dir(results[0].fingerprint) / "model.safetensors"
    )
    asset_file.write_bytes(b"corrupted")
    r_corrupt = mgr.provision_or_verify(manifest_v1, offline=True, asset_fetcher=_no_network)
    assert r_corrupt.ready is False and r_corrupt.reason == "cache_corrupt"

    # Scenario 11 & 12: Offline missing revision & request handler never downloads
    manifest_v3, _ = _make_manifest(revision="rev-3", weights=b"weights-v3")
    r_miss = mgr.provision_or_verify(manifest_v3, offline=True, asset_fetcher=_no_network)
    assert r_miss.ready is False and r_miss.reason == "model_unavailable"

    monkeypatch.setenv("OPENCONNECT_SCAN_TOKEN", "local-test-token")
    monkeypatch.setenv("OC_MODEL_CACHE_DIR", str(tmp_path))
    monkeypatch.setenv("OC_SECURITY_DEBERTA_ENABLED", "true")
    monkeypatch.setattr(app_module, "settings", Settings.from_environment())
    scan_res = client.post(
        "/scan",
        json={
            "schemaVersion": "openconnect.v1",
            "requestId": "123e4567-e89b-42d3-a456-426614174000",
            "text": "test",
            "detectors": ["deberta-injection"],
        },
        headers=AUTH,
    )
    assert scan_res.status_code == 503
