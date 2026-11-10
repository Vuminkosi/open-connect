"""Tests for the 12 runtime model cache scenarios in workers-samples-e2e-spec.md §4.2."""

from __future__ import annotations

import concurrent.futures
import hashlib
from pathlib import Path

from openconnect_kokoro.config import Settings
from openconnect_kokoro.runtime_cache import (
    RuntimeModelAsset,
    RuntimeModelCacheManager,
    RuntimeModelManifest,
    compute_manifest_fingerprint,
    verify_dockerfile_no_model_downloads,
)


def _make_manifest(
    revision: str = "rev-kokoro-1",
    weights: bytes = b"kokoro-82m-weights-v1",
    voice_bytes: bytes = b"af_heart-voice-v1",
) -> tuple[RuntimeModelManifest, dict[str, bytes]]:
    w_hash = hashlib.sha256(weights).hexdigest()
    v_hash = hashlib.sha256(voice_bytes).hexdigest()
    manifest = RuntimeModelManifest(
        workerKind="kokoro",
        modelId="hexgrad/Kokoro-82M",
        immutableRevision=revision,
        license="apache-2.0",
        voiceId="af_heart",
        assets=[
            RuntimeModelAsset(
                relativePath="kokoro-v1_0.pth",
                sha256=w_hash,
                sizeBytes=len(weights),
                kind="weights",
            ),
            RuntimeModelAsset(
                relativePath="voices/af_heart.pt",
                sha256=v_hash,
                sizeBytes=len(voice_bytes),
                kind="voice",
            ),
        ],
    )
    return manifest, {"kokoro-v1_0.pth": weights, "voices/af_heart.pt": voice_bytes}


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
    mgr = RuntimeModelCacheManager(tmp_path, "kokoro")
    manifest, _ = _make_manifest()
    res = mgr.provision_or_verify(manifest, enabled=False, asset_fetcher=_no_network)
    assert res.ready is False and res.status == "disabled" and res.downloads_attempted == 0
    assert list(tmp_path.iterdir()) == []


def test_scenarios_03_to_07_kokoro_cache_lifecycle(monkeypatch, tmp_path: Path) -> None:
    mgr = RuntimeModelCacheManager(tmp_path, "kokoro")
    m1, b1 = _make_manifest(revision="rev-1", voice_bytes=b"af_heart-v1")
    fp1 = compute_manifest_fingerprint(m1)

    # Scenario 3: First enabled start
    calls: list[int] = []
    r1 = mgr.provision_or_verify(m1, asset_fetcher=_fetcher(b1, calls), image_tag="v1")
    assert r1.ready is True and r1.fingerprint == fp1 and len(calls) == 2

    monkeypatch.setenv("OC_MODEL_CACHE_DIR", str(tmp_path))
    monkeypatch.setenv("OC_KOKORO_MODEL", "hexgrad/Kokoro-82M")
    monkeypatch.setenv("OC_KOKORO_VOICE", "af_heart")
    s = Settings.from_environment()
    assert s.kokoro_model == "hexgrad/Kokoro-82M"
    assert s.default_voice == "af_heart"
    assert s.verified_cache_ready() is True

    # Scenario 4: Offline restart cache hit
    r_off = mgr.provision_or_verify(m1, offline=True, asset_fetcher=_no_network, image_tag="v1")
    assert r_off.ready is True and r_off.cache_hit is True and r_off.downloads_attempted == 0

    # Scenario 5: Image upgrade with same manifest
    r_up = mgr.provision_or_verify(m1, offline=True, asset_fetcher=_no_network, image_tag="v2")
    assert r_up.ready is True and r_up.downloads_attempted == 0

    # Scenario 6: Voice asset SHA change creates new immutable fingerprint, preserves v1
    m2, b2 = _make_manifest(revision="rev-1", voice_bytes=b"af_heart-v2-updated-voice")
    fp2 = compute_manifest_fingerprint(m2)
    assert fp2 != fp1
    r2 = mgr.provision_or_verify(m2, asset_fetcher=_fetcher(b2, []))
    assert r2.ready is True and r2.fingerprint == fp2
    paths = mgr.paths_for(m1.modelId)
    assert paths.revision_dir(fp1).is_dir() and paths.revision_dir(fp2).is_dir()

    # Scenario 7: Offline revert to prior voice revision
    r_rev = mgr.provision_or_verify(m1, offline=True, asset_fetcher=_no_network)
    assert r_rev.ready is True and r_rev.fingerprint == fp1 and r_rev.downloads_attempted == 0


def test_scenarios_08_to_12_corruption_interruption_concurrency_request(tmp_path: Path) -> None:
    mgr = RuntimeModelCacheManager(tmp_path, "kokoro")
    m1, b1 = _make_manifest(revision="rev-1")

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
        raise OSError("disk full")

    r_fail = mgr.provision_or_verify(m2, asset_fetcher=_fail)
    assert r_fail.ready is False and r_fail.reason == "provision_failed"
    assert mgr.verify_for_request(m1).ready is True

    # Scenario 8: Corrupt voice file rejected
    assert results[0].fingerprint is not None
    v_path = (
        mgr.paths_for(m1.modelId).assets_dir(results[0].fingerprint) / "voices/af_heart.pt"
    )
    v_path.write_bytes(b"corrupt")
    r_corrupt = mgr.provision_or_verify(m1, offline=True, asset_fetcher=_no_network)
    assert r_corrupt.ready is False and r_corrupt.reason == "cache_corrupt"

    # Scenario 11 & 12: Offline missing & request handler never downloads
    m3, _ = _make_manifest(revision="rev-3", weights=b"weights-v3")
    assert mgr.provision_or_verify(m3, offline=True, asset_fetcher=_no_network).ready is False
    assert mgr.verify_for_request(m3).ready is False
