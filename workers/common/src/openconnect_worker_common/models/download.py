"""Manifest-bound HTTPS asset fetcher for explicit model initialization only."""

from __future__ import annotations

import hashlib
import http.client
import ipaddress
import math
import os
import shutil
import socket
import ssl
import threading
import time
from dataclasses import dataclass
from pathlib import Path
from urllib.parse import urlsplit

from openconnect_worker_common.models.runtime_cache import RuntimeModelAsset, RuntimeModelManifest


@dataclass(frozen=True)
class AssetSource:
    """Operator-approved location and media types for one manifest asset."""

    url: str
    media_types: frozenset[str]


class _PinnedHTTPSConnection(http.client.HTTPSConnection):
    """Connect to a prevalidated address while verifying TLS for the hostname."""

    def __init__(self, host: str, address: str, timeout: float) -> None:
        super().__init__(host, 443, timeout=timeout, context=ssl.create_default_context())
        self._address = address

    def connect(self) -> None:
        raw = socket.create_connection((self._address, 443), self.timeout)
        try:
            self.sock = self._context.wrap_socket(raw, server_hostname=self.host)
        except BaseException:
            raw.close()
            raise


class StartupAssetFetcher:
    """Fetch only exact assets in one operator-selected manifest.

    Redirects and proxies are deliberately unsupported. Call only during a
    model initialization stage; the cache manager handles activation.
    """

    def __init__(
        self,
        manifest: RuntimeModelManifest,
        sources: dict[str, AssetSource],
        allowed_hosts: frozenset[str],
        *,
        max_bytes: int,
        timeout_seconds: float,
        reserve_bytes: int = 0,
    ) -> None:
        assets = {asset.relativePath: asset.model_copy(deep=True) for asset in manifest.assets}
        if len(assets) != len(manifest.assets) or set(assets) != set(sources):
            raise ValueError("invalid_asset_sources")
        if (max_bytes <= 0 or not math.isfinite(timeout_seconds)
                or timeout_seconds <= 0 or reserve_bytes < 0):
            raise ValueError("invalid_download_limits")
        if sum(asset.sizeBytes for asset in assets.values()) > max_bytes:
            raise ValueError("download_budget_exceeded")
        self._assets = assets
        self._sources = dict(sources)
        self._allowed_hosts = frozenset(host.lower() for host in allowed_hosts)
        self._timeout = timeout_seconds
        self._reserve = reserve_bytes
        self._budget = max_bytes
        self._downloaded = 0
        self._dns_timed_out = False

    def _resolve(self, host: str, timeout: float) -> list[str]:
        if self._dns_timed_out:
            raise TimeoutError("source_resolution_timeout")
        finished = threading.Event()
        result: list[str] = []
        errors: list[Exception] = []

        def lookup() -> None:
            try:
                result.extend(entry[4][0] for entry in socket.getaddrinfo(
                    host, 443, type=socket.SOCK_STREAM
                ))
            except Exception as exc:
                errors.append(exc)
            finally:
                finished.set()

        threading.Thread(target=lookup, name="model-source-dns", daemon=True).start()
        if not finished.wait(timeout):
            # Do not create another resolver thread with this fetcher after a timeout.
            self._dns_timed_out = True
            raise TimeoutError("source_resolution_timeout")
        if errors:
            raise OSError("source_unavailable") from errors[0]
        return result

    def __call__(self, asset: RuntimeModelAsset, target: Path) -> None:
        expected = self._assets.get(asset.relativePath)
        if expected is None or asset != expected or asset.sizeBytes <= 0:
            raise ValueError("unapproved_asset")
        source = self._sources[asset.relativePath]
        parsed = urlsplit(source.url)
        host = parsed.hostname
        if (parsed.scheme != "https" or not host or host.lower() not in self._allowed_hosts
                or parsed.username is not None or parsed.password is not None
                or parsed.port not in (None, 443) or parsed.fragment or not source.media_types):
            raise ValueError("unapproved_source")
        if self._downloaded + asset.sizeBytes > self._budget:
            raise ValueError("download_budget_exceeded")
        if shutil.disk_usage(target.parent).free < asset.sizeBytes + self._reserve:
            raise OSError("insufficient_cache_space")

        deadline = time.monotonic() + self._timeout
        ips = self._resolve(host, self._timeout)
        if not ips:
            raise OSError("source_unavailable")
        if any(not ipaddress.ip_address(ip).is_global for ip in ips):
            raise ValueError("unapproved_address")
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise TimeoutError("download_timeout")
        connection = _PinnedHTTPSConnection(host, ips[0], remaining)
        try:
            path = parsed.path or "/"
            if parsed.query:
                path += "?" + parsed.query
            connection.request("GET", path, headers={"Accept-Encoding": "identity"})
            response = connection.getresponse()
            if response.status != 200:
                raise OSError("download_failed")
            media_type = response.getheader("Content-Type", "").split(";", 1)[0].strip().lower()
            if media_type not in source.media_types:
                raise ValueError("invalid_content_type")
            length = response.getheader("Content-Length")
            if length is None or not length.isdecimal() or int(length) != asset.sizeBytes:
                raise ValueError("invalid_content_length")
            if response.getheader("Content-Encoding", "identity").lower() != "identity":
                raise ValueError("invalid_content_encoding")
            digest = hashlib.sha256()
            count = 0
            flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_NOFOLLOW", 0)
            with os.fdopen(os.open(target, flags, 0o600), "wb") as output:
                while count < asset.sizeBytes:
                    remaining = deadline - time.monotonic()
                    if remaining <= 0:
                        raise TimeoutError("download_timeout")
                    if connection.sock is not None:
                        connection.sock.settimeout(remaining)
                    chunk = response.read1(min(64 * 1024, asset.sizeBytes - count))
                    if not chunk:
                        raise OSError("incomplete_download")
                    count += len(chunk)
                    digest.update(chunk)
                    output.write(chunk)
            self._downloaded += count
            if digest.hexdigest() != asset.sha256.lower():
                raise ValueError("checksum_mismatch")
        finally:
            connection.close()
