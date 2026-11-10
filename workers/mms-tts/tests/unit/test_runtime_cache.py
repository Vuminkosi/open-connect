"""Tests for the 12 runtime model cache scenarios in workers-samples-e2e-spec.md §4.2."""

from __future__ import annotations

import concurrent.futures
import hashlib
from pathlib import Path

from openconnect_mms_tts.engine import CheckpointCache
from openconnect_mms_tts.runtime_cache import (
    RuntimeModelAsset,
    RuntimeModelCacheManager,
    RuntimeModelManifest,
    compute_manifest_fingerprint,
    verify_dockerfile_no_model_downloads,
)


def _make_manifest(
    lang: str = "eng",
    revision: str = "rev-mms-1",
    weights: bytes = b"mms-tts-eng-weights-v1",
    tok_bytes: bytes = b'{"vocab":"eng"}',
    license_id: str = "cc-by-nc-4.0",
) -> tuple[RuntimeModelManifest, dict[str, bytes]]:
    w_hash = hashlib.sha256(weights).hexdigest()
    t_hash = hashlib.sha256(tok_bytes).hexdigest()
    manifest = RuntimeModelManifest(
        workerKind="mms-tts",
        modelId=f"facebook/mms-tts-{lang}",
        immutableRevision=revision,
        license=license_id,
        languageCode=lang,
        assets=[
            RuntimeModelAsset(
                relativePath="model.safetensors",
                sha256=w_hash,
                sizeBytes=len(weights),
                kind="weights",
            ),
            RuntimeModelAsset(
                relativePath="tokenizer.json",
                sha256=t_hash,
                sizeBytes=len(tok_bytes),
                kind="tokenizer",
            ),
        ],
    )
    return manifest, {"model.safetensors": weights, "tokenizer.json": tok_bytes}


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


def test_scenario_02_disabled_worker_and_license_gate(tmp_path: Path) -> None:
    mgr = RuntimeModelCacheManager(tmp_path, "mms-tts")
    manifest, _ = _make_manifest()
    res = mgr.provision_or_verify(manifest, enabled=False, asset_fetcher=_no_network)
    assert res.ready is False and res.status == "disabled" and res.downloads_attempted == 0
    assert list(tmp_path.iterdir()) == []

    # Commercial policy blocks CC-BY-NC-4.0 before provisioning
    lic = mgr.provision_or_verify(
        manifest,
        license_accepted=True,
        commercial_use=True,
        asset_fetcher=_no_network,
    )
    assert lic.ready is False and lic.reason == "license_denied"


def test_scenarios_03_to_07_multi_language_cache_and_lru(tmp_path: Path) -> None:
    mgr = RuntimeModelCacheManager(tmp_path, "mms-tts")
    m_eng_v1, b_eng_v1 = _make_manifest(lang="eng", revision="rev-eng-1")
    m_fra_v1, b_fra_v1 = _make_manifest(lang="fra", revision="rev-fra-1")
    fp_eng_v1 = compute_manifest_fingerprint(m_eng_v1)
    fp_fra_v1 = compute_manifest_fingerprint(m_fra_v1)

    # Scenario 3: First enabled start for eng and fra
    calls: list[int] = []
    r_eng = mgr.provision_or_verify(
        m_eng_v1, license_accepted=True, asset_fetcher=_fetcher(b_eng_v1, calls), image_tag="v1"
    )
    r_fra = mgr.provision_or_verify(
        m_fra_v1, license_accepted=True, asset_fetcher=_fetcher(b_fra_v1, calls), image_tag="v1"
    )
    assert r_eng.ready is True and r_fra.ready is True and len(calls) == 4

    # LRU RAM eviction does NOT delete persistent verified weights on disk
    ram_cache = CheckpointCache(max_entries=1)
    ram_cache.load("eng", "CC-BY-NC-4.0")
    ram_cache.load("fra", "CC-BY-NC-4.0")
    assert ram_cache.cached_languages() == ["fra"]
    assert mgr.paths_for(m_eng_v1.modelId).revision_dir(fp_eng_v1).is_dir()

    # Scenario 4 & 5: Offline restart and image upgrade reuse both caches
    r_off = mgr.provision_or_verify(
        m_eng_v1,
        license_accepted=True,
        offline=True,
        asset_fetcher=_no_network,
        image_tag="v2",
    )
    assert r_off.ready is True and r_off.cache_hit is True and r_off.downloads_attempted == 0

    # Scenario 6: Revision change for eng keeps fra untouched and preserves eng v1
    m_eng_v2, b_eng_v2 = _make_manifest(
        lang="eng", revision="rev-eng-2", weights=b"eng-weights-v2"
    )
    fp_eng_v2 = compute_manifest_fingerprint(m_eng_v2)
    r_eng_v2 = mgr.provision_or_verify(
        m_eng_v2, license_accepted=True, asset_fetcher=_fetcher(b_eng_v2, [])
    )
    assert r_eng_v2.ready is True and r_eng_v2.fingerprint == fp_eng_v2
    assert mgr.paths_for(m_eng_v1.modelId).revision_dir(fp_eng_v1).is_dir()
    assert mgr.paths_for(m_fra_v1.modelId).revision_dir(fp_fra_v1).is_dir()

    # Scenario 7: Offline revert eng back to v1
    r_rev = mgr.provision_or_verify(
        m_eng_v1, license_accepted=True, offline=True, asset_fetcher=_no_network
    )
    assert r_rev.ready is True and r_rev.fingerprint == fp_eng_v1


def test_scenarios_08_to_12_corruption_interruption_concurrency_request(tmp_path: Path) -> None:
    mgr = RuntimeModelCacheManager(tmp_path, "mms-tts")
    m1, b1 = _make_manifest(lang="eng", revision="rev-1")

    # Scenario 10: Concurrent startup
    calls: list[int] = []
    with concurrent.futures.ThreadPoolExecutor(max_workers=3) as pool:
        results = list(
            pool.map(
                lambda _: mgr.provision_or_verify(
                    m1, license_accepted=True, asset_fetcher=_fetcher(b1, calls)
                ),
                range(3),
            )
        )
    assert all(r.ready for r in results) and len(calls) == 2

    # Scenario 9: Interrupted download preserves v1
    m2, _ = _make_manifest(lang="eng", revision="rev-2", weights=b"weights-v2")

    def _fail(_a: RuntimeModelAsset, _p: Path) -> None:
        raise OSError("interrupted")

    r_fail = mgr.provision_or_verify(m2, license_accepted=True, asset_fetcher=_fail)
    assert r_fail.ready is False and r_fail.reason == "provision_failed"
    assert mgr.verify_for_request(m1, license_accepted=True).ready is True

    # Scenario 8: Tokenizer mismatch / corruption rejected
    assert results[0].fingerprint is not None
    tok_path = mgr.paths_for(m1.modelId).assets_dir(results[0].fingerprint) / "tokenizer.json"
    tok_path.write_bytes(b"corrupted-tokenizer")
    r_corrupt = mgr.provision_or_verify(
        m1, license_accepted=True, offline=True, asset_fetcher=_no_network
    )
    assert r_corrupt.ready is False and r_corrupt.reason == "tokenizer_mismatch"

    # Scenario 11 & 12: Offline missing & request handler never downloads
    m3, _ = _make_manifest(lang="deu", revision="rev-1")
    assert (
        mgr.provision_or_verify(
            m3, license_accepted=True, offline=True, asset_fetcher=_no_network
        ).ready
        is False
    )
    assert mgr.verify_for_request(m3, license_accepted=True).ready is False
