"""Runtime model cache and manifest verification lifecycle.

Implements the canonical versioned cache layout and 12-scenario verification
protocol required by ``openconnect-registration-spec.md`` §§7.1-7.2 and
``workers-samples-e2e-spec.md`` §§4.1-4.3:

Layout under ``<cache_root>/<worker-kind>/<model-id-safe>/``::

    revisions/<manifest-fingerprint>/
      manifest.lock.json
      assets/...
      verification.json
    .staging/<unique-id>/
    locks/<fingerprint>.lock
    active.json

Invariants:
- Docker image build NEVER downloads models.
- Disabled workers/detectors create zero directories and perform zero network calls.
- Cache key is a deterministic SHA-256 over canonical manifest metadata and
  artifact hashes/sizes (never keyed by container image tag alone).
- Promotion from ``.staging/<unique-id>`` to ``revisions/<fingerprint>`` and
  updates to ``active.json`` are atomic; previous verified revisions remain on
  disk for offline rollback.
- Request handlers (``verify_for_request``) never provision or open network
  connections.
"""

from __future__ import annotations

import contextlib
import dataclasses
import datetime
import hashlib
import json
import os
import re
import shutil
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
from collections.abc import Callable, Iterable, Iterator
from dataclasses import dataclass
from pathlib import Path

from pydantic import BaseModel, ConfigDict, Field

PRE_APPROVED_LICENSES: frozenset[str] = frozenset(
    {"apache-2.0", "mit", "bsd-3-clause", "bsd-2-clause", "cc0-1.0"}
)
NON_COMMERCIAL_TOKENS: tuple[str, ...] = ("nc", "noncommercial", "non-commercial", "research-only")

_SAFE_MODEL_ID_RE = re.compile(r"[^A-Za-z0-9._-]+")
_IN_PROCESS_LOCKS_GUARD = threading.Lock()
_IN_PROCESS_LOCKS: dict[str, threading.Lock] = {}

FORBIDDEN_DOCKERFILE_PATTERNS: tuple[re.Pattern[str], ...] = (
    re.compile(r"snapshot_download", re.IGNORECASE),
    re.compile(r"hf_hub_download", re.IGNORECASE),
    re.compile(r"from_pretrained\s*\(", re.IGNORECASE),
    re.compile(r"huggingface-cli\s+download", re.IGNORECASE),
    re.compile(r"ollama\s+pull", re.IGNORECASE),
    re.compile(
        r"\b(curl|wget)\b[^\n]*\.(bin|safetensors|onnx|pt|pth|gguf|ckpt)\b",
        re.IGNORECASE,
    ),
)


class RuntimeModelAsset(BaseModel):
    """One digest-pinned file artifact in a runtime model manifest."""

    model_config = ConfigDict(extra="forbid")

    relativePath: str = Field(min_length=1, max_length=1024)
    sha256: str = Field(pattern=r"^[0-9a-fA-F]{64}$")
    sizeBytes: int = Field(ge=0)
    kind: str = Field(default="weights", min_length=1, max_length=64)


class RuntimeModelManifest(BaseModel):
    """Canonical immutable runtime model manifest."""

    model_config = ConfigDict(extra="forbid")

    workerKind: str = Field(min_length=1, max_length=128)
    modelId: str = Field(min_length=1, max_length=256)
    immutableRevision: str = Field(min_length=1, max_length=128)
    license: str = Field(min_length=1, max_length=128)
    assets: list[RuntimeModelAsset] = Field(min_length=1)
    runtimeCompatibility: str = Field(default="py312-cpu-cuda", min_length=1, max_length=128)
    quantization: str = Field(default="none", min_length=1, max_length=64)
    voiceId: str | None = Field(default=None, max_length=128)
    languageCode: str | None = Field(default=None, max_length=64)


@dataclass(frozen=True)
class CacheProvisionResult:
    """Outcome of a startup provision/verify or request-time cache check."""

    ready: bool
    status: str
    reason: str | None
    fingerprint: str | None
    model_id: str | None
    revision: str | None
    revision_dir: Path | None
    downloads_attempted: int = 0
    cache_hit: bool = False
    license_approved: bool = False


AssetFetcher = Callable[[RuntimeModelAsset, Path], None]

DEFAULT_ALLOWED_MODEL_HOSTS: frozenset[str] = frozenset(
    {
        "huggingface.co",
        "cdn-lfs.huggingface.co",
        "cdn-lfs-us-1.huggingface.co",
        "cdn-lfs-eu-1.huggingface.co",
        "github.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com",
    }
)
FORBIDDEN_FLOATING_REVISIONS: frozenset[str] = frozenset(
    {"main", "master", "latest", "head", "trunk", "dev", "nightly"}
)
DEFAULT_MAX_ASSET_BYTES = 2 * 1024 * 1024 * 1024  # 2 GiB hard ceiling


class FetchPolicyError(ValueError):
    """Raised when an asset download violates host, scheme, revision, or size policy."""


def validate_pinned_revision(revision: str) -> str:
    """Reject floating branch/tag names; require a pinned immutable revision."""
    cleaned = revision.strip()
    if not cleaned or cleaned.lower() in FORBIDDEN_FLOATING_REVISIONS:
        raise FetchPolicyError(f"floating revision not allowed: {revision!r}")
    if any(ch in cleaned for ch in ("/", "\\", "..", "\r", "\n", " ")):
        raise FetchPolicyError(f"invalid revision format: {revision!r}")
    return cleaned


def validate_allowlisted_https_url(
    url: str,
    *,
    allowed_hosts: Iterable[str] = DEFAULT_ALLOWED_MODEL_HOSTS,
) -> urllib.parse.SplitResult:
    """Validate that ``url`` is HTTPS, has no userinfo, and targets an allowlisted host."""
    parsed = urllib.parse.urlsplit(url)
    if parsed.scheme.lower() != "https":
        raise FetchPolicyError(f"non-https scheme rejected: {parsed.scheme!r}")
    if parsed.username is not None or parsed.password is not None:
        raise FetchPolicyError("url userinfo is not allowed")
    host = (parsed.hostname or "").strip().lower()
    normalized_allowed = {h.strip().lower() for h in allowed_hosts}
    if not host or host not in normalized_allowed:
        raise FetchPolicyError(f"host not in allowlist: {host!r}")
    return parsed


class _AllowlistedRedirectHandler(urllib.request.HTTPRedirectHandler):
    """Re-validate every redirect hop against the HTTPS host allowlist."""

    def __init__(self, allowed_hosts: frozenset[str], max_redirects: int = 5) -> None:
        super().__init__()
        self._allowed_hosts = allowed_hosts
        self._max_redirects = max_redirects
        self._count = 0

    def redirect_request(self, req, fp, code, msg, headers, newurl):  # type: ignore[override]
        self._count += 1
        if self._count > self._max_redirects:
            raise FetchPolicyError("too_many_redirects")
        resolved = urllib.parse.urljoin(req.full_url, newurl)
        validate_allowlisted_https_url(resolved, allowed_hosts=self._allowed_hosts)
        return super().redirect_request(req, fp, code, msg, headers, resolved)


class AllowlistedHttpsAssetFetcher:
    """Bounded, allowlisted HTTPS fetcher with streaming SHA-256 and size verification."""

    def __init__(
        self,
        manifest: RuntimeModelManifest,
        *,
        allowed_hosts: Iterable[str] = DEFAULT_ALLOWED_MODEL_HOSTS,
        base_url: str = "https://huggingface.co",
        url_builder: Callable[[RuntimeModelManifest, RuntimeModelAsset], str] | None = None,
        timeout_seconds: float = 60.0,
        max_asset_bytes: int = DEFAULT_MAX_ASSET_BYTES,
        chunk_size: int = 256 * 1024,
        opener_factory: Callable[[urllib.request.BaseHandler], urllib.request.OpenerDirector]
        | None = None,
    ) -> None:
        validate_pinned_revision(manifest.immutableRevision)
        self._manifest = manifest
        self._allowed_hosts = frozenset(h.strip().lower() for h in allowed_hosts)
        self._base_url = base_url.rstrip("/")
        validate_allowlisted_https_url(self._base_url, allowed_hosts=self._allowed_hosts)
        self._url_builder = url_builder
        self._timeout = timeout_seconds
        self._max_asset_bytes = max_asset_bytes
        self._chunk_size = chunk_size
        self._opener_factory = opener_factory

    def build_url(self, asset: RuntimeModelAsset) -> str:
        if self._url_builder is not None:
            url = self._url_builder(self._manifest, asset)
        else:
            rel = asset.relativePath.lstrip("/").replace("\\", "/")
            encoded_model = urllib.parse.quote(self._manifest.modelId.strip("/"), safe="/._-")
            encoded_rev = urllib.parse.quote(self._manifest.immutableRevision.strip(), safe="._-")
            encoded_rel = urllib.parse.quote(rel, safe="/._-")
            url = f"{self._base_url}/{encoded_model}/resolve/{encoded_rev}/{encoded_rel}"
        validate_allowlisted_https_url(url, allowed_hosts=self._allowed_hosts)
        return url

    def __call__(self, asset: RuntimeModelAsset, target_path: Path) -> None:
        if asset.sizeBytes > self._max_asset_bytes:
            raise FetchPolicyError("asset_exceeds_max_size")
        if target_path.is_symlink():
            raise FetchPolicyError("symlink_target_rejected")
        url = self.build_url(asset)
        redirect_handler = _AllowlistedRedirectHandler(self._allowed_hosts)
        opener = (
            self._opener_factory(redirect_handler)
            if self._opener_factory is not None
            else urllib.request.build_opener(redirect_handler)
        )
        request = urllib.request.Request(
            url,
            headers={"User-Agent": "openconnect-security-worker/1.0"},
            method="GET",
        )
        hasher = hashlib.sha256()
        written = 0
        tmp_path = target_path.parent / f".{target_path.name}.{uuid.uuid4().hex}.part"
        try:
            with opener.open(request, timeout=self._timeout) as response:
                final_url = response.geturl()
                if final_url:
                    validate_allowlisted_https_url(final_url, allowed_hosts=self._allowed_hosts)
                with tmp_path.open("wb") as handle:
                    while True:
                        chunk = response.read(self._chunk_size)
                        if not chunk:
                            break
                        written += len(chunk)
                        if written > asset.sizeBytes or written > self._max_asset_bytes:
                            raise FetchPolicyError("download_size_exceeded")
                        hasher.update(chunk)
                        handle.write(chunk)
            if written != asset.sizeBytes:
                raise FetchPolicyError("download_size_mismatch")
            if hasher.hexdigest() != asset.sha256.lower():
                raise FetchPolicyError("download_hash_mismatch")
            os.replace(tmp_path, target_path)
        finally:
            with contextlib.suppress(OSError):
                if tmp_path.exists():
                    tmp_path.unlink()


def sanitize_model_id(model_id: str) -> str:
    """Return a filesystem-safe directory segment for ``model_id``."""
    cleaned = model_id.strip().strip("/")
    if not cleaned or cleaned in {".", ".."} or ".." in cleaned.split("/"):
        raise ValueError(f"invalid model_id: {model_id!r}")
    return _SAFE_MODEL_ID_RE.sub("__", cleaned)


def _is_safe_relative_path(base_dir: Path, relative_path: str) -> bool:
    if not relative_path or os.path.isabs(relative_path):
        return False
    parts = Path(relative_path).parts
    if any(p in {"", ".", ".."} for p in parts):
        return False
    try:
        resolved_base = base_dir.resolve()
        candidate = (resolved_base / relative_path).resolve()
        candidate.relative_to(resolved_base)
    except ValueError:
        return False
    return True


def sha256_file(path: Path, chunk_size: int = 1024 * 1024) -> str:
    """Compute lowercase hex SHA-256 of a local file without network I/O."""
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        while chunk := handle.read(chunk_size):
            digest.update(chunk)
    return digest.hexdigest()


def canonical_manifest_dict(manifest: RuntimeModelManifest) -> dict[str, object]:
    """Return the canonical dictionary used for deterministic fingerprinting."""
    sorted_assets = sorted(
        (
            {
                "relativePath": asset.relativePath.replace("\\", "/"),
                "sha256": asset.sha256.lower(),
                "sizeBytes": asset.sizeBytes,
                "kind": asset.kind,
            }
            for asset in manifest.assets
        ),
        key=lambda item: str(item["relativePath"]),
    )
    return {
        "workerKind": manifest.workerKind.strip().lower(),
        "modelId": manifest.modelId.strip(),
        "immutableRevision": manifest.immutableRevision.strip(),
        "license": manifest.license.strip().lower(),
        "assets": sorted_assets,
        "runtimeCompatibility": manifest.runtimeCompatibility.strip(),
        "quantization": manifest.quantization.strip().lower(),
        "voiceId": manifest.voiceId,
        "languageCode": manifest.languageCode,
    }


def compute_manifest_fingerprint(manifest: RuntimeModelManifest) -> str:
    """Compute the immutable SHA-256 cache key for ``manifest``.

    Container image tags are intentionally excluded so image upgrades with an
    unchanged manifest reuse the existing verified revision directory.
    """
    payload = json.dumps(
        canonical_manifest_dict(manifest),
        sort_keys=True,
        separators=(",", ":"),
    ).encode("utf-8")
    return hashlib.sha256(payload).hexdigest()


def is_license_approved(
    license_id: str,
    *,
    license_accepted: bool = False,
    commercial_use: bool = False,
) -> bool:
    """Evaluate whether a checkpoint license is permitted under current policy."""
    normalized = license_id.strip().lower()
    if not normalized or normalized in {"none", "unlicensed", "unknown", "proprietary-unapproved"}:
        return False
    is_non_commercial = any(
        token == normalized or f"-{token}" in normalized or f"_{token}" in normalized
        for token in NON_COMMERCIAL_TOKENS
    )
    if commercial_use and is_non_commercial:
        return False
    if normalized in PRE_APPROVED_LICENSES:
        return True
    return bool(license_accepted)


def verify_dockerfile_no_model_downloads(dockerfile_text: str) -> list[str]:
    """Scan Dockerfile content for prohibited build-time model acquisition."""
    violations: list[str] = []
    for line_no, raw_line in enumerate(dockerfile_text.splitlines(), start=1):
        stripped = raw_line.strip()
        if not stripped or stripped.startswith("#"):
            continue
        for pattern in FORBIDDEN_DOCKERFILE_PATTERNS:
            if pattern.search(stripped):
                violations.append(f"line {line_no}: {stripped}")
    return violations


class ModelCachePaths:
    """Physical directory layout for a worker's model cache."""

    def __init__(self, cache_root: Path | str, worker_kind: str, model_id: str) -> None:
        self.cache_root = Path(cache_root)
        self.worker_kind = worker_kind.strip().lower()
        self.model_id = model_id
        self.model_id_safe = sanitize_model_id(model_id)
        self.model_root = self.cache_root / self.worker_kind / self.model_id_safe
        self.revisions_dir = self.model_root / "revisions"
        self.staging_root = self.model_root / ".staging"
        self.locks_dir = self.model_root / "locks"
        self.active_path = self.model_root / "active.json"

    def revision_dir(self, fingerprint: str) -> Path:
        return self.revisions_dir / fingerprint

    def assets_dir(self, fingerprint: str) -> Path:
        return self.revision_dir(fingerprint) / "assets"

    def manifest_lock_path(self, fingerprint: str) -> Path:
        return self.revision_dir(fingerprint) / "manifest.lock.json"

    def verification_path(self, fingerprint: str) -> Path:
        return self.revision_dir(fingerprint) / "verification.json"

    def staging_dir(self, unique_id: str) -> Path:
        return self.staging_root / unique_id

    def lock_path(self, fingerprint: str) -> Path:
        return self.locks_dir / f"{fingerprint}.lock"


def _atomic_write_json(target: Path, data: dict[str, object]) -> None:
    target.parent.mkdir(parents=True, exist_ok=True)
    tmp = target.parent / f".{target.name}.{uuid.uuid4().hex}.tmp"
    payload = json.dumps(data, indent=2, sort_keys=True) + "\n"
    tmp.write_text(payload, encoding="utf-8")
    os.replace(tmp, target)


@contextlib.contextmanager
def _fingerprint_lock(lock_file: Path, timeout_seconds: float = 30.0) -> Iterator[None]:
    """Serialize concurrent provisioners targeting the same fingerprint."""
    key = str(lock_file.resolve()) if lock_file.is_absolute() else str(lock_file)
    with _IN_PROCESS_LOCKS_GUARD:
        thread_lock = _IN_PROCESS_LOCKS.setdefault(key, threading.Lock())
    with thread_lock:
        lock_file.parent.mkdir(parents=True, exist_ok=True)
        deadline = time.monotonic() + timeout_seconds
        fd: int | None = None
        while True:
            try:
                fd = os.open(str(lock_file), os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
                os.write(fd, f"{os.getpid()}:{uuid.uuid4().hex}\n".encode("ascii"))
                os.close(fd)
                fd = None
                break
            except FileExistsError:
                if time.monotonic() >= deadline:
                    raise TimeoutError(f"timed out waiting for model lock {lock_file}") from None
                time.sleep(0.02)
        try:
            yield
        finally:
            with contextlib.suppress(OSError):
                lock_file.unlink(missing_ok=True)


class RuntimeModelCacheManager:
    """Manages verification, atomic staging/promotion, and rollback for one worker."""

    def __init__(self, cache_root: Path | str, worker_kind: str) -> None:
        self.cache_root = Path(cache_root)
        self.worker_kind = worker_kind.strip().lower()

    def paths_for(self, model_id: str) -> ModelCachePaths:
        return ModelCachePaths(self.cache_root, self.worker_kind, model_id)

    def read_active(self, model_id: str) -> dict[str, object] | None:
        paths = self.paths_for(model_id)
        if not paths.active_path.is_file():
            return None
        try:
            data = json.loads(paths.active_path.read_text(encoding="utf-8"))
            return data if isinstance(data, dict) else None
        except (OSError, ValueError):
            return None

    def verify_revision(
        self,
        manifest: RuntimeModelManifest,
        *,
        license_accepted: bool = False,
        commercial_use: bool = False,
    ) -> CacheProvisionResult:
        """Verify an existing cached revision on disk without network or writes."""
        fingerprint = compute_manifest_fingerprint(manifest)
        approved = is_license_approved(
            manifest.license,
            license_accepted=license_accepted,
            commercial_use=commercial_use,
        )
        if not approved:
            return CacheProvisionResult(
                ready=False,
                status="unavailable",
                reason="license_denied",
                fingerprint=fingerprint,
                model_id=manifest.modelId,
                revision=manifest.immutableRevision,
                revision_dir=None,
                downloads_attempted=0,
                cache_hit=False,
                license_approved=False,
            )

        paths = self.paths_for(manifest.modelId)
        rev_dir = paths.revision_dir(fingerprint)
        if not rev_dir.is_dir():
            return CacheProvisionResult(
                ready=False,
                status="unavailable",
                reason="model_unavailable",
                fingerprint=fingerprint,
                model_id=manifest.modelId,
                revision=manifest.immutableRevision,
                revision_dir=None,
                downloads_attempted=0,
                cache_hit=False,
                license_approved=True,
            )

        lock_path = paths.manifest_lock_path(fingerprint)
        ver_path = paths.verification_path(fingerprint)
        if not lock_path.is_file() or not ver_path.is_file():
            return CacheProvisionResult(
                ready=False,
                status="unavailable",
                reason="cache_corrupt",
                fingerprint=fingerprint,
                model_id=manifest.modelId,
                revision=manifest.immutableRevision,
                revision_dir=None,
                downloads_attempted=0,
                cache_hit=False,
                license_approved=True,
            )

        try:
            lock_data = json.loads(lock_path.read_text(encoding="utf-8"))
            ver_data = json.loads(ver_path.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            return CacheProvisionResult(
                ready=False,
                status="unavailable",
                reason="cache_corrupt",
                fingerprint=fingerprint,
                model_id=manifest.modelId,
                revision=manifest.immutableRevision,
                revision_dir=None,
                downloads_attempted=0,
                cache_hit=False,
                license_approved=True,
            )

        if (
            lock_data.get("fingerprint") != fingerprint
            or ver_data.get("fingerprint") != fingerprint
            or ver_data.get("verified") is not True
        ):
            return CacheProvisionResult(
                ready=False,
                status="unavailable",
                reason="cache_corrupt",
                fingerprint=fingerprint,
                model_id=manifest.modelId,
                revision=manifest.immutableRevision,
                revision_dir=None,
                downloads_attempted=0,
                cache_hit=False,
                license_approved=True,
            )

        assets_dir = paths.assets_dir(fingerprint)
        for asset in manifest.assets:
            if not _is_safe_relative_path(assets_dir, asset.relativePath):
                return CacheProvisionResult(
                    ready=False,
                    status="unavailable",
                    reason="cache_corrupt",
                    fingerprint=fingerprint,
                    model_id=manifest.modelId,
                    revision=manifest.immutableRevision,
                    revision_dir=None,
                    downloads_attempted=0,
                    cache_hit=False,
                    license_approved=True,
                )
            asset_file = assets_dir / asset.relativePath
            if not asset_file.is_file():
                return CacheProvisionResult(
                    ready=False,
                    status="unavailable",
                    reason="cache_corrupt",
                    fingerprint=fingerprint,
                    model_id=manifest.modelId,
                    revision=manifest.immutableRevision,
                    revision_dir=None,
                    downloads_attempted=0,
                    cache_hit=False,
                    license_approved=True,
                )
            try:
                stat = asset_file.stat()
                actual_hash = sha256_file(asset_file)
            except OSError:
                return CacheProvisionResult(
                    ready=False,
                    status="unavailable",
                    reason="cache_corrupt",
                    fingerprint=fingerprint,
                    model_id=manifest.modelId,
                    revision=manifest.immutableRevision,
                    revision_dir=None,
                    downloads_attempted=0,
                    cache_hit=False,
                    license_approved=True,
                )
            if stat.st_size != asset.sizeBytes or actual_hash != asset.sha256.lower():
                reason = "tokenizer_mismatch" if asset.kind == "tokenizer" else "cache_corrupt"
                return CacheProvisionResult(
                    ready=False,
                    status="unavailable",
                    reason=reason,
                    fingerprint=fingerprint,
                    model_id=manifest.modelId,
                    revision=manifest.immutableRevision,
                    revision_dir=None,
                    downloads_attempted=0,
                    cache_hit=False,
                    license_approved=True,
                )

        return CacheProvisionResult(
            ready=True,
            status="ready",
            reason=None,
            fingerprint=fingerprint,
            model_id=manifest.modelId,
            revision=manifest.immutableRevision,
            revision_dir=rev_dir,
            downloads_attempted=0,
            cache_hit=True,
            license_approved=True,
        )

    def _activate(self, paths: ModelCachePaths, manifest: RuntimeModelManifest, fingerprint: str) -> None:
        with contextlib.suppress(OSError):
            _atomic_write_json(
                paths.active_path,
                {
                    "workerKind": self.worker_kind,
                    "modelId": manifest.modelId,
                    "activeFingerprint": fingerprint,
                    "immutableRevision": manifest.immutableRevision,
                    "activatedAtUtc": datetime.datetime.now(datetime.UTC).isoformat(),
                },
            )

    def provision_or_verify(
        self,
        manifest: RuntimeModelManifest | None,
        *,
        enabled: bool = True,
        provisioning: str = "first-start-if-missing",
        offline: bool = False,
        license_accepted: bool = False,
        commercial_use: bool = False,
        asset_fetcher: AssetFetcher | None = None,
        image_tag: str | None = None,
    ) -> CacheProvisionResult:
        """Run the startup model provision/verify state machine.

        ``image_tag`` is accepted so callers/tests can pass the container tag
        across image upgrades while proving it never alters the cache key.
        """
        del image_tag  # Never part of cache identity.

        if not enabled or provisioning == "disabled":
            return CacheProvisionResult(
                ready=False,
                status="disabled",
                reason="worker_disabled" if not enabled else "model_unavailable",
                fingerprint=None,
                model_id=manifest.modelId if manifest else None,
                revision=manifest.immutableRevision if manifest else None,
                revision_dir=None,
                downloads_attempted=0,
                cache_hit=False,
                license_approved=False,
            )

        if manifest is None:
            return CacheProvisionResult(
                ready=False,
                status="unavailable",
                reason="model_unavailable",
                fingerprint=None,
                model_id=None,
                revision=None,
                revision_dir=None,
                downloads_attempted=0,
                cache_hit=False,
                license_approved=False,
            )

        fingerprint = compute_manifest_fingerprint(manifest)
        approved = is_license_approved(
            manifest.license,
            license_accepted=license_accepted,
            commercial_use=commercial_use,
        )
        if not approved:
            return CacheProvisionResult(
                ready=False,
                status="unavailable",
                reason="license_denied",
                fingerprint=fingerprint,
                model_id=manifest.modelId,
                revision=manifest.immutableRevision,
                revision_dir=None,
                downloads_attempted=0,
                cache_hit=False,
                license_approved=False,
            )

        paths = self.paths_for(manifest.modelId)
        existing = self.verify_revision(
            manifest,
            license_accepted=license_accepted,
            commercial_use=commercial_use,
        )
        if existing.ready:
            self._activate(paths, manifest, fingerprint)
            return existing

        # If the revision directory exists on disk, it failed verification (corrupt).
        revision_exists_corrupt = paths.revision_dir(fingerprint).exists()
        if offline or provisioning == "verify-existing-only" or asset_fetcher is None:
            return CacheProvisionResult(
                ready=False,
                status="unavailable",
                reason=existing.reason if revision_exists_corrupt else "model_unavailable",
                fingerprint=fingerprint,
                model_id=manifest.modelId,
                revision=manifest.immutableRevision,
                revision_dir=None,
                downloads_attempted=0,
                cache_hit=False,
                license_approved=True,
            )

        with _fingerprint_lock(paths.lock_path(fingerprint)):
            # Re-verify after acquiring the lock: another process may have won.
            after_lock = self.verify_revision(
                manifest,
                license_accepted=license_accepted,
                commercial_use=commercial_use,
            )
            if after_lock.ready:
                self._activate(paths, manifest, fingerprint)
                return after_lock

            staging_id = f"{int(time.time() * 1000)}-{uuid.uuid4().hex[:12]}"
            staging_dir = paths.staging_dir(staging_id)
            staging_assets = staging_dir / "assets"
            staging_assets.mkdir(parents=True, exist_ok=True)
            downloads = 0

            try:
                for asset in manifest.assets:
                    if not _is_safe_relative_path(staging_assets, asset.relativePath):
                        raise ValueError(f"unsafe relativePath: {asset.relativePath!r}")
                    target_file = staging_assets / asset.relativePath
                    target_file.parent.mkdir(parents=True, exist_ok=True)
                    downloads += 1
                    asset_fetcher(asset, target_file)
                    if not target_file.is_file():
                        raise OSError(f"fetcher did not create {asset.relativePath}")
                    stat = target_file.stat()
                    actual_hash = sha256_file(target_file)
                    if stat.st_size != asset.sizeBytes or actual_hash != asset.sha256.lower():
                        reason = (
                            "tokenizer_mismatch" if asset.kind == "tokenizer" else "cache_corrupt"
                        )
                        _atomic_write_json(
                            staging_dir / "incomplete.json",
                            {
                                "fingerprint": fingerprint,
                                "reason": reason,
                                "failedAsset": asset.relativePath,
                                "timestampUtc": datetime.datetime.now(
                                    datetime.UTC
                                ).isoformat(),
                            },
                        )
                        return CacheProvisionResult(
                            ready=False,
                            status="unavailable",
                            reason=reason,
                            fingerprint=fingerprint,
                            model_id=manifest.modelId,
                            revision=manifest.immutableRevision,
                            revision_dir=None,
                            downloads_attempted=downloads,
                            cache_hit=False,
                            license_approved=True,
                        )

                now_iso = datetime.datetime.now(datetime.UTC).isoformat()
                _atomic_write_json(
                    staging_dir / "manifest.lock.json",
                    {
                        "fingerprint": fingerprint,
                        "manifest": canonical_manifest_dict(manifest),
                        "lockedAtUtc": now_iso,
                    },
                )
                _atomic_write_json(
                    staging_dir / "verification.json",
                    {
                        "fingerprint": fingerprint,
                        "workerKind": self.worker_kind,
                        "modelId": manifest.modelId,
                        "immutableRevision": manifest.immutableRevision,
                        "license": manifest.license,
                        "licenseApproved": True,
                        "verified": True,
                        "verifiedAtUtc": now_iso,
                    },
                )

                dest_dir = paths.revision_dir(fingerprint)
                dest_dir.parent.mkdir(parents=True, exist_ok=True)
                if dest_dir.exists():
                    shutil.rmtree(dest_dir)
                os.replace(staging_dir, dest_dir)
                self._activate(paths, manifest, fingerprint)
            except Exception as exc:  # noqa: BLE001 - Provisioning failures must preserve the active cache.
                with contextlib.suppress(OSError):
                    _atomic_write_json(
                        staging_dir / "incomplete.json",
                        {
                            "fingerprint": fingerprint,
                            "reason": "provision_failed",
                            "error": type(exc).__name__,
                            "timestampUtc": datetime.datetime.now(
                                datetime.UTC
                            ).isoformat(),
                        },
                    )
                return CacheProvisionResult(
                    ready=False,
                    status="unavailable",
                    reason="provision_failed",
                    fingerprint=fingerprint,
                    model_id=manifest.modelId,
                    revision=manifest.immutableRevision,
                    revision_dir=None,
                    downloads_attempted=downloads,
                    cache_hit=False,
                    license_approved=True,
                )

            verified = self.verify_revision(
                manifest,
                license_accepted=license_accepted,
                commercial_use=commercial_use,
            )
            return dataclasses.replace(
                verified,
                downloads_attempted=downloads,
                cache_hit=False,
            )

    def verify_for_request(
        self,
        manifest: RuntimeModelManifest | None,
        *,
        license_accepted: bool = False,
        commercial_use: bool = False,
    ) -> CacheProvisionResult:
        """Request-time readiness check: NEVER downloads or provisions models."""
        if manifest is None:
            return CacheProvisionResult(
                ready=False,
                status="unavailable",
                reason="model_unavailable",
                fingerprint=None,
                model_id=None,
                revision=None,
                revision_dir=None,
                downloads_attempted=0,
                cache_hit=False,
                license_approved=False,
            )
        return self.verify_revision(
            manifest,
            license_accepted=license_accepted,
            commercial_use=commercial_use,
        )
