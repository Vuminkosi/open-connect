"""Read exact versioned MinIO objects using the document storage key contract."""

from __future__ import annotations

import hashlib
import re
from pathlib import Path
from typing import Protocol

import urllib3

_TENANT = re.compile(r"[0-7][0-9A-HJKMNP-TV-Z]{25}\Z")
_DIGEST = re.compile(r"[0-9a-f]{64}\Z")
_MAX_BYTES = 10 * 1024 * 1024


def storage_pool(*, tls: bool, ca_file: str | None = None) -> urllib3.PoolManager:
    """Create a bounded, verifying pool, optionally trusting a private CA bundle."""
    if not isinstance(tls, bool):
        raise ValueError("invalid_storage_configuration")
    options = {"timeout": urllib3.Timeout(connect=5, read=30), "retries": False,
               "cert_reqs": "CERT_REQUIRED"}
    if ca_file:
        path = Path(ca_file)
        if not tls or not path.is_absolute() or not path.is_file():
            raise ValueError("invalid_storage_configuration")
        options["ca_certs"] = str(path)
    return urllib3.PoolManager(**options)


class MinioClient(Protocol):
    def get_object(self, bucket_name: str, object_name: str, *, version_id: str): ...


class MinioResolver:
    """Read bounded immutable objects; the host supplies an authenticated client.

    The client must use a private bucket, protected transport and bounded network
    timeouts. This component never accepts URLs or caller-provided bucket keys.
    """

    def __init__(self, client: MinioClient, bucket: str):
        if client is None or not isinstance(bucket, str) or not 3 <= len(bucket) <= 63:
            raise ValueError("invalid_storage_configuration")
        self._client = client
        self._bucket = bucket

    def read(self, tenant_id: str, object_id: str, version: str, size: int, sha256: str) -> bytes:
        if (not isinstance(tenant_id, str) or not _TENANT.fullmatch(tenant_id)
                or not isinstance(object_id, str) or not _DIGEST.fullmatch(object_id)
                or not isinstance(sha256, str) or not _DIGEST.fullmatch(sha256)
                or not isinstance(version, str) or not 1 <= len(version) <= 1024 or version == "null"
                or any(ord(c) < 32 or ord(c) == 127 for c in version)
                or isinstance(size, bool) or not isinstance(size, int) or not 1 <= size <= _MAX_BYTES):
            raise ValueError("invalid_object_reference")
        try:
            version.encode("utf-8")
        except UnicodeEncodeError:
            raise ValueError("invalid_object_reference") from None
        key = f"v1/tenants/{hashlib.sha256(tenant_id.encode('ascii')).hexdigest()}/documents/{object_id}"
        data = bytearray()
        try:
            response = self._client.get_object(self._bucket, key, version_id=version)
            try:
                if response.headers.get("x-amz-version-id") != version:
                    raise RuntimeError("document_version_mismatch")
                while len(data) <= size:
                    chunk = response.read(min(65536, size + 1 - len(data)))
                    if not chunk:
                        break
                    data.extend(chunk)
            finally:
                try:
                    response.close()
                finally:
                    response.release_conn()
        except Exception:
            data[:] = bytes(len(data))
            raise RuntimeError("document_storage_unavailable") from None
        try:
            if len(data) != size or hashlib.sha256(data).hexdigest() != sha256:
                raise RuntimeError("document_integrity_failed")
            return bytes(data)
        finally:
            data[:] = bytes(len(data))
