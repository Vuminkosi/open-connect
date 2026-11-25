"""Negative-first tests for AllowlistedHttpsAssetFetcher and packaging invariants."""

from __future__ import annotations

import hashlib
import io
from pathlib import Path
from typing import Self

import pytest

from openconnect_security.runtime_cache import (
    AllowlistedHttpsAssetFetcher,
    FetchPolicyError,
    RuntimeModelAsset,
    RuntimeModelCacheManager,
    RuntimeModelManifest,
    validate_allowlisted_https_url,
    validate_pinned_revision,
    verify_dockerfile_no_model_downloads,
)

WORKER_DIR = Path(__file__).resolve().parents[2]


class _FakeHttpResponse:
    def __init__(self, payload: bytes, final_url: str) -> None:
        self._stream = io.BytesIO(payload)
        self._final_url = final_url

    def read(self, n: int = -1) -> bytes:
        return self._stream.read(n)

    def geturl(self) -> str:
        return self._final_url

    def __enter__(self) -> Self:
        return self

    def __exit__(self, *_args: object) -> None:
        self._stream.close()


class _FakeOpener:
    def __init__(self, payload: bytes, final_url: str) -> None:
        self.payload = payload
        self.final_url = final_url
        self.urls_requested: list[str] = []

    def open(self, request, timeout: float = 60.0):  # type: ignore[no-untyped-def]
        del timeout
        self.urls_requested.append(request.full_url)
        return _FakeHttpResponse(self.payload, self.final_url)


def _manifest_for(payload: bytes, *, revision: str = "a1b2c3d4e5f6") -> RuntimeModelManifest:
    return RuntimeModelManifest(
        workerKind="security",
        modelId="urchade/gliner_small-v2.1",
        immutableRevision=revision,
        license="apache-2.0",
        assets=[
            RuntimeModelAsset(
                relativePath="model.safetensors",
                sha256=hashlib.sha256(payload).hexdigest(),
                sizeBytes=len(payload),
                kind="weights",
            )
        ],
    )


@pytest.mark.parametrize("bad_rev", ["main", "master", "LATEST", "HEAD", "rev/bad", "../escape", ""])
def test_rejects_floating_and_invalid_revisions(bad_rev: str) -> None:
    with pytest.raises(FetchPolicyError):
        validate_pinned_revision(bad_rev)


@pytest.mark.parametrize(
    "bad_url",
    [
        "http://huggingface.co/model/file",
        "file:///etc/passwd",
        "https://user:pass@huggingface.co/model/file",
        "https://evil.example.com/model/file",
        "https://huggingface.co.evil.com/model/file",
    ],
)
def test_rejects_non_allowlisted_or_unsafe_urls(bad_url: str) -> None:
    with pytest.raises(FetchPolicyError):
        validate_allowlisted_https_url(bad_url)


def test_fetcher_rejects_size_and_hash_mismatches_and_cleans_temp(tmp_path: Path) -> None:
    expected = b"verified-model-bytes"
    manifest = _manifest_for(expected)
    asset = manifest.assets[0]
    target = tmp_path / "model.safetensors"

    # Oversized payload rejected
    oversized_opener = _FakeOpener(
        expected + b"extra",
        "https://huggingface.co/urchade/gliner_small-v2.1/resolve/a1b2c3d4e5f6/model.safetensors",
    )
    fetcher_over = AllowlistedHttpsAssetFetcher(
        manifest, opener_factory=lambda _h: oversized_opener  # type: ignore[arg-type]
    )
    with pytest.raises(FetchPolicyError, match="download_size_exceeded"):
        fetcher_over(asset, target)
    assert not target.exists()
    assert list(tmp_path.iterdir()) == []

    # Hash mismatch rejected
    corrupt_opener = _FakeOpener(
        b"X" * len(expected),
        "https://huggingface.co/urchade/gliner_small-v2.1/resolve/a1b2c3d4e5f6/model.safetensors",
    )
    fetcher_bad = AllowlistedHttpsAssetFetcher(
        manifest, opener_factory=lambda _h: corrupt_opener  # type: ignore[arg-type]
    )
    with pytest.raises(FetchPolicyError, match="download_hash_mismatch"):
        fetcher_bad(asset, target)
    assert not target.exists()

    # Final URL redirect to non-allowlisted host rejected
    evil_redirect = _FakeOpener(expected, "https://evil.example.com/model.safetensors")
    fetcher_redirect = AllowlistedHttpsAssetFetcher(
        manifest, opener_factory=lambda _h: evil_redirect  # type: ignore[arg-type]
    )
    with pytest.raises(FetchPolicyError, match="host not in allowlist"):
        fetcher_redirect(asset, target)
    assert not target.exists()


def test_fetcher_end_to_end_with_cache_manager_and_offline_reuse(tmp_path: Path) -> None:
    payload = b"real-verified-weights-v1"
    manifest = _manifest_for(payload)
    opener = _FakeOpener(
        payload,
        "https://cdn-lfs.huggingface.co/urchade/gliner_small-v2.1/model.safetensors",
    )
    fetcher = AllowlistedHttpsAssetFetcher(
        manifest, opener_factory=lambda _h: opener  # type: ignore[arg-type]
    )
    mgr = RuntimeModelCacheManager(tmp_path, "security")

    first = mgr.provision_or_verify(manifest, asset_fetcher=fetcher)
    assert first.ready is True
    assert first.downloads_attempted == 1
    assert len(opener.urls_requested) == 1

    # Offline warm restart reuses verified revision with zero downloads
    second = mgr.provision_or_verify(manifest, offline=True)
    assert second.ready is True
    assert second.cache_hit is True
    assert second.downloads_attempted == 0


def test_dockerfile_and_compose_allow_authorized_provisioning_and_optional_clamav() -> None:
    dockerfile = (WORKER_DIR / "Dockerfile").read_text(encoding="utf-8")
    assert verify_dockerfile_no_model_downloads(dockerfile) == []
    assert "HF_HUB_OFFLINE=1" not in dockerfile
    assert "TRANSFORMERS_OFFLINE=1" not in dockerfile

    compose = (WORKER_DIR / "compose.yaml").read_text(encoding="utf-8")
    assert "HF_HUB_OFFLINE" not in compose
    assert "TRANSFORMERS_OFFLINE" not in compose
    assert "oc_security_models:/models:ro" not in compose
    assert "oc_security_models:/models" in compose
    assert "clamav:" in compose
