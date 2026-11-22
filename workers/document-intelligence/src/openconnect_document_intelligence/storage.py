"""Tenant-scoped MinIO document storage for authorized host operations."""

from __future__ import annotations

import hashlib
import io
import re
import secrets
from dataclasses import dataclass

from minio import Minio

from openconnect_document_intelligence.malware import ClamAvScanner
from openconnect_document_intelligence.ocr import MAX_INPUT_BYTES

_TENANT = re.compile(r"^[0-7][0-9A-HJKMNP-TV-Z]{25}$")
_OBJECT = re.compile(r"^[0-9a-f]{64}$")


@dataclass(frozen=True, repr=False)
class DocumentReference:
    """Opaque ID and integrity metadata; authorization is enforced by the host."""

    object_id: str
    sha256: str
    size_bytes: int
    version_id: str | None = None

    def __post_init__(self) -> None:
        if (not isinstance(self.object_id, str) or not _OBJECT.fullmatch(self.object_id)
                or not isinstance(self.sha256, str) or not _OBJECT.fullmatch(self.sha256)
                or isinstance(self.size_bytes, bool) or not isinstance(self.size_bytes, int)
                or not 0 < self.size_bytes <= MAX_INPUT_BYTES
                or not isinstance(self.version_id, str) or not 0 < len(self.version_id) <= 1024
                or self.version_id == "null" or any(ord(char) < 32 or ord(char) == 127 for char in self.version_id)):
            raise ValueError("invalid_document_reference")
        try:
            self.version_id.encode("utf-8")
        except UnicodeEncodeError:
            raise ValueError("invalid_document_reference") from None


class MinioDocumentStore:
    """Store screened content under server-derived tenant and document keys.

    Tenant scope and object-level authorization must be established by the
    trusted host before invoking this component, never from unverified JSON.
    The configured client must use protected transport and a private bucket.
    """

    def __init__(self, client: Minio, bucket: str, scanner: ClamAvScanner) -> None:
        if not bucket or not scanner:
            raise ValueError("invalid_storage_configuration")
        self._client = client
        self._bucket = bucket
        self._scanner = scanner

    @staticmethod
    def _key(tenant_id: str, object_id: str) -> str:
        if (not isinstance(tenant_id, str) or not _TENANT.fullmatch(tenant_id)
                or not isinstance(object_id, str) or not _OBJECT.fullmatch(object_id)):
            raise ValueError("invalid_document_scope")
        tenant_hash = hashlib.sha256(tenant_id.encode("ascii")).hexdigest()
        return f"v1/tenants/{tenant_hash}/documents/{object_id}"

    def put(self, tenant_id: str, content: bytes) -> DocumentReference:
        """Scan and store a new document; no overwrite key is supplied by a caller."""
        if type(content) is not bytes or not content or len(content) > MAX_INPUT_BYTES:
            raise ValueError("invalid_document")
        object_id = secrets.token_hex(32)
        key = self._key(tenant_id, object_id)
        self._scanner.scan(content)
        digest = hashlib.sha256(content).hexdigest()
        try:
            if self._client.get_bucket_versioning(self._bucket).status != "Enabled":
                raise RuntimeError("document_storage_unavailable")
            result = self._client.put_object(self._bucket, key, io.BytesIO(content), len(content),
                                             content_type="application/octet-stream")
            reference = DocumentReference(object_id, digest, len(content), result.version_id)
        except Exception:
            raise RuntimeError("document_storage_unavailable") from None
        return reference

    def get(self, tenant_id: str, reference: DocumentReference) -> bytes:
        """Read one authorized reference and verify its exact size and digest."""
        reference = self._validated_reference(reference)
        key = self._key(tenant_id, reference.object_id)
        try:
            response = self._client.get_object(self._bucket, key, version_id=reference.version_id)
            try:
                if response.headers.get("x-amz-version-id") != reference.version_id:
                    raise RuntimeError("document_version_mismatch")
                data = bytearray()
                while chunk := response.read(min(65536, reference.size_bytes + 1 - len(data))):
                    data.extend(chunk)
                    if len(data) > reference.size_bytes:
                        break
            finally:
                try:
                    response.close()
                finally:
                    response.release_conn()
        except Exception:
            raise RuntimeError("document_storage_unavailable") from None
        if len(data) != reference.size_bytes or hashlib.sha256(data).hexdigest() != reference.sha256:
            raise RuntimeError("document_integrity_failed")
        return bytes(data)

    def delete(self, tenant_id: str, reference: DocumentReference) -> None:
        """Remove the referenced version; dependent stores require separate deletion."""
        reference = self._validated_reference(reference)
        key = self._key(tenant_id, reference.object_id)
        try:
            self._client.remove_object(self._bucket, key, version_id=reference.version_id)
        except Exception:
            raise RuntimeError("document_storage_unavailable") from None

    @staticmethod
    def _validated_reference(reference: DocumentReference) -> DocumentReference:
        if type(reference) is not DocumentReference:
            raise ValueError("invalid_document_reference")
        return DocumentReference(reference.object_id, reference.sha256, reference.size_bytes, reference.version_id)
