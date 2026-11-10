"""Tests for the 12 runtime model cache scenarios in workers-samples-e2e-spec.md §4.2."""

from __future__ import annotations

import concurrent.futures
import hashlib
import json
from pathlib import Path

import pytest

from openconnect_worker_common.models.runtime_cache import (
    RuntimeModelAsset,
    RuntimeModelCacheManager,
    RuntimeModelManifest,
    compute_manifest_fingerprint,
    verify_dockerfile_no_model_downloads,
)

EIGHT_PYTHON_WORKERS = (
    "security",
    "classification",
    "nllb",
    "inference",
    "transcription",
    "realtime-voice",
    "kokoro",
    "mms-tts",
)


def _asset(rel_path: str, content: bytes, kind: str = "weights") -> tuple[RuntimeModelAsset, bytes]:
    digest = hashlib.sha256(content).hexdigest()
    return (
        RuntimeModelAsset(
            relativePath=rel_path,
            sha256=digest,
            sizeBytes=len(content),
            kind=kind,
        ),
        content,
    )


def _manifest_and_blobs(
    worker_kind: str = "kokoro",
    model_id: str = "hexgrad/Kokoro-82M",
    revision: str = "rev-001",
    license_id: str = "apache-2.0",
    weights_bytes: bytes = b"kokoro-weights-v1",
    extra_bytes: bytes = b"af_heart-voice-v1",
    extra_path: str = "voices/af_heart.bin",
    extra_kind: str = "voice",
) -> tuple[RuntimeModelManifest, dict[str, bytes]]:
    w_asset, w_data = _asset("model.safetensors", weights_bytes, "weights")
    e_asset, e_data = _asset(extra_path, extra_bytes, extra_kind)
    manifest = RuntimeModelManifest(
        workerKind=worker_kind,
        modelId=model_id,
        immutableRevision=revision,
        license=license_id,
        assets=[w_asset, e_asset],
        voiceId="af_heart" if worker_kind == "kokoro" else None,
    )
    return manifest, {"model.safetensors": w_data, extra_path: e_data}


def _make_fetcher(blobs: dict[str, bytes], call_counter: list[int]):
    def _fetch(asset: RuntimeModelAsset, target: Path) -> None:
        call_counter.append(1)
        target.write_bytes(blobs[asset.relativePath])

    return _fetch


def _forbid_network(_asset: RuntimeModelAsset, _target: Path) -> None:
    raise AssertionError("network download must not be called in offline / cache-hit scenario")


# Scenario 1: Image build with empty cache & Dockerfile audit
@pytest.mark.parametrize("worker", EIGHT_PYTHON_WORKERS)
def test_scenario_01_dockerfile_no_build_downloads(worker: str) -> None:
    repo_root = Path(__file__).resolve().parents[3]
    dockerfile = repo_root / "workers" / worker / "Dockerfile"
    assert dockerfile.is_file()
    violations = verify_dockerfile_no_model_downloads(dockerfile.read_text(encoding="utf-8"))
    assert violations == []


# Scenario 2: Worker disabled -> zero directory creation, zero network calls
@pytest.mark.parametrize("worker", EIGHT_PYTHON_WORKERS)
def test_scenario_02_worker_disabled_creates_nothing(tmp_path: Path, worker: str) -> None:
    mgr = RuntimeModelCacheManager(tmp_path, worker)
    manifest, _ = _manifest_and_blobs(worker_kind=worker)
    res_disabled = mgr.provision_or_verify(
        manifest,
        enabled=False,
        asset_fetcher=_forbid_network,
    )
    assert res_disabled.ready is False
    assert res_disabled.status == "disabled"
    assert res_disabled.downloads_attempted == 0
    assert list(tmp_path.iterdir()) == []

    res_prov_disabled = mgr.provision_or_verify(
        manifest,
        enabled=True,
        provisioning="disabled",
        asset_fetcher=_forbid_network,
    )
    assert res_prov_disabled.ready is False
    assert res_prov_disabled.downloads_attempted == 0
    assert list(tmp_path.iterdir()) == []


# Scenario 3, 4, 5, 6, 7: First start, offline restart, image upgrade, revision change, offline rollback
@pytest.mark.parametrize("worker", EIGHT_PYTHON_WORKERS)
def test_scenarios_03_to_07_lifecycle(tmp_path: Path, worker: str) -> None:
    mgr = RuntimeModelCacheManager(tmp_path, worker)
    manifest_v1, blobs_v1 = _manifest_and_blobs(worker_kind=worker, revision="rev-001")
    fp_v1 = compute_manifest_fingerprint(manifest_v1)

    # Scenario 3: First enabled start
    calls_v1: list[int] = []
    res1 = mgr.provision_or_verify(
        manifest_v1,
        asset_fetcher=_make_fetcher(blobs_v1, calls_v1),
        image_tag="0.1.0",
    )
    assert res1.ready is True
    assert res1.fingerprint == fp_v1
    assert res1.downloads_attempted == 2
    assert len(calls_v1) == 2
    paths = mgr.paths_for(manifest_v1.modelId)
    assert paths.manifest_lock_path(fp_v1).is_file()
    assert paths.verification_path(fp_v1).is_file()
    assert paths.active_path.is_file()
    assert json.loads(paths.active_path.read_text(encoding="utf-8"))["activeFingerprint"] == fp_v1

    # Scenario 4: Offline restart same image -> cache hit, zero downloads
    res_restart = mgr.provision_or_verify(
        manifest_v1,
        offline=True,
        asset_fetcher=_forbid_network,
        image_tag="0.1.0",
    )
    assert res_restart.ready is True
    assert res_restart.cache_hit is True
    assert res_restart.downloads_attempted == 0
    assert res_restart.fingerprint == fp_v1

    # Scenario 5: Upgrade image tag with same manifest -> zero downloads
    res_upgrade = mgr.provision_or_verify(
        manifest_v1,
        offline=True,
        asset_fetcher=_forbid_network,
        image_tag="0.2.0-upgraded",
    )
    assert res_upgrade.ready is True
    assert res_upgrade.cache_hit is True
    assert res_upgrade.downloads_attempted == 0
    assert res_upgrade.fingerprint == fp_v1

    # Scenario 6: Model revision change -> preserves v1, promotes v2
    manifest_v2, blobs_v2 = _manifest_and_blobs(
        worker_kind=worker,
        revision="rev-002",
        weights_bytes=b"kokoro-weights-v2-updated",
    )
    fp_v2 = compute_manifest_fingerprint(manifest_v2)
    assert fp_v2 != fp_v1
    calls_v2: list[int] = []
    res_v2 = mgr.provision_or_verify(
        manifest_v2,
        asset_fetcher=_make_fetcher(blobs_v2, calls_v2),
    )
    assert res_v2.ready is True
    assert res_v2.revision == "rev-002"
    assert res_v2.fingerprint == fp_v2
    assert paths.revision_dir(fp_v1).is_dir()  # v1 preserved!
    assert paths.revision_dir(fp_v2).is_dir()
    assert json.loads(paths.active_path.read_text(encoding="utf-8"))["activeFingerprint"] == fp_v2

    # Scenario 7: Revert revision offline -> reuses preserved v1 with zero downloads
    res_revert = mgr.provision_or_verify(
        manifest_v1,
        offline=True,
        asset_fetcher=_forbid_network,
    )
    assert res_revert.ready is True
    assert res_revert.revision == "rev-001"
    assert res_revert.fingerprint == fp_v1
    assert res_revert.downloads_attempted == 0
    assert json.loads(paths.active_path.read_text(encoding="utf-8"))["activeFingerprint"] == fp_v1


# Scenario 8: Tamper / corrupt file rejected
def test_scenario_08_tamper_corrupt_rejected(tmp_path: Path) -> None:
    mgr = RuntimeModelCacheManager(tmp_path, "nllb")
    manifest, blobs = _manifest_and_blobs(
        worker_kind="nllb",
        model_id="facebook/nllb-200-distilled-600M",
        extra_path="tokenizer.json",
        extra_kind="tokenizer",
    )
    calls: list[int] = []
    res = mgr.provision_or_verify(manifest, asset_fetcher=_make_fetcher(blobs, calls))
    assert res.ready is True and res.fingerprint is not None

    # Corrupt tokenizer file in place
    paths = mgr.paths_for(manifest.modelId)
    tok_path = paths.assets_dir(res.fingerprint) / "tokenizer.json"
    tok_path.write_bytes(b"corrupted-tokenizer")

    corrupt_check = mgr.provision_or_verify(manifest, offline=True, asset_fetcher=_forbid_network)
    assert corrupt_check.ready is False
    assert corrupt_check.reason == "tokenizer_mismatch"

    # Corrupt weights file in place
    tok_path.write_bytes(blobs["tokenizer.json"])
    weights_path = paths.assets_dir(res.fingerprint) / "model.safetensors"
    weights_path.write_bytes(b"corrupted-weights")
    corrupt_weights = mgr.provision_or_verify(manifest, offline=True, asset_fetcher=_forbid_network)
    assert corrupt_weights.ready is False
    assert corrupt_weights.reason == "cache_corrupt"


# Scenario 9: Interrupted download / disk full preserves active prior revision
def test_scenario_09_interrupted_download_preserves_active(tmp_path: Path) -> None:
    mgr = RuntimeModelCacheManager(tmp_path, "classification")
    manifest_v1, blobs_v1 = _manifest_and_blobs(worker_kind="classification", revision="rev-1")
    res_v1 = mgr.provision_or_verify(manifest_v1, asset_fetcher=_make_fetcher(blobs_v1, []))
    assert res_v1.ready is True

    manifest_v2, _ = _manifest_and_blobs(
        worker_kind="classification",
        revision="rev-2",
        weights_bytes=b"new-weights",
    )

    def _failing_fetcher(_asset: RuntimeModelAsset, _target: Path) -> None:
        raise OSError("ENOSPC: simulated disk full / interrupted download")

    res_v2 = mgr.provision_or_verify(manifest_v2, asset_fetcher=_failing_fetcher)
    assert res_v2.ready is False
    assert res_v2.reason == "provision_failed"

    # Active revision is still v1 and usable offline!
    active = mgr.read_active(manifest_v1.modelId)
    assert active is not None and active["activeFingerprint"] == res_v1.fingerprint
    verify_v1 = mgr.verify_for_request(manifest_v1)
    assert verify_v1.ready is True and verify_v1.revision == "rev-1"


# Scenario 10: Concurrent startup uses lock so only one provisioner downloads
def test_scenario_10_concurrent_startup_single_writer(tmp_path: Path) -> None:
    mgr = RuntimeModelCacheManager(tmp_path, "inference")
    manifest, blobs = _manifest_and_blobs(worker_kind="inference", model_id="org/local-llm")
    calls: list[int] = []
    fetcher = _make_fetcher(blobs, calls)

    with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
        futures = [
            pool.submit(mgr.provision_or_verify, manifest, asset_fetcher=fetcher)
            for _ in range(4)
        ]
        results = [f.result() for f in futures]

    assert all(r.ready for r in results)
    # Only the winning contender downloaded the 2 assets; the other 3 hit the verified cache!
    assert sum(r.downloads_attempted for r in results) == 2
    assert len(calls) == 2


# Scenario 11: Offline with no matching revision -> typed unready, zero network calls
def test_scenario_11_offline_no_matching_revision(tmp_path: Path) -> None:
    mgr = RuntimeModelCacheManager(tmp_path, "transcription")
    manifest, _ = _manifest_and_blobs(worker_kind="transcription", model_id="org/whisper-small")
    res = mgr.provision_or_verify(manifest, offline=True, asset_fetcher=_forbid_network)
    assert res.ready is False
    assert res.reason == "model_unavailable"
    assert res.downloads_attempted == 0


# Scenario 12: Request handler never downloads models + license gate
def test_scenario_12_request_handler_never_downloads_and_license_gate(tmp_path: Path) -> None:
    mgr = RuntimeModelCacheManager(tmp_path, "mms-tts")
    manifest, _ = _manifest_and_blobs(
        worker_kind="mms-tts",
        model_id="facebook/mms-tts-eng",
        license_id="cc-by-nc-4.0",
    )
    # Request-time verification on empty cache returns model_unavailable with zero downloads
    req_res = mgr.verify_for_request(manifest, license_accepted=True, commercial_use=False)
    assert req_res.ready is False
    assert req_res.reason == "model_unavailable"
    assert req_res.downloads_attempted == 0

    # Commercial use with non-commercial license is blocked before provisioning
    lic_res = mgr.provision_or_verify(
        manifest,
        license_accepted=True,
        commercial_use=True,
        asset_fetcher=_forbid_network,
    )
    assert lic_res.ready is False
    assert lic_res.reason == "license_denied"
    assert lic_res.downloads_attempted == 0
