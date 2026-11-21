import io
from types import SimpleNamespace

import pytest

from openconnect_document_intelligence.malware import MalwareDetectedError, MalwareScanUnavailableError
from openconnect_document_intelligence.storage import DocumentReference, MinioDocumentStore

TENANT_A = "01ARZ3NDEKTSV4RRFFQ69G5FAV"
TENANT_B = "01ARZ3NDEKTSV4RRFFQ69G5FAW"


class Scanner:
    def __init__(self, blocked=False):
        self.blocked = blocked
        self.scans = []

    def scan(self, content):
        self.scans.append(content)
        if self.blocked:
            raise MalwareDetectedError("malware_detected")


class Response(io.BytesIO):
    def __init__(self, data):
        super().__init__(data)
        self.released = False
        self.headers = {"x-amz-version-id": "version-1"}

    def release_conn(self):
        self.released = True


class Store:
    def __init__(self):
        self.objects = {}
        self.last_response = None
        self.last_deleted_version = None

    def put_object(self, bucket, key, data, size, **kwargs):
        self.objects[(bucket, key)] = data.read(size)
        return SimpleNamespace(version_id="version-1")

    def get_bucket_versioning(self, bucket):
        return SimpleNamespace(status="Enabled")

    def get_object(self, bucket, key, version_id=None):
        self.last_response = Response(self.objects[(bucket, key)])
        return self.last_response

    def remove_object(self, bucket, key, version_id=None):
        self.last_deleted_version = version_id
        self.objects.pop((bucket, key), None)


def test_screened_storage_roundtrip_is_tenant_scoped_and_deletes_version():
    scanner = Scanner()
    backend = Store()
    store = MinioDocumentStore(backend, "documents", scanner)
    reference = store.put(TENANT_A, b"sensitive document")
    assert scanner.scans == [b"sensitive document"]
    assert store.get(TENANT_A, reference) == b"sensitive document"
    assert backend.last_response.closed and backend.last_response.released
    with pytest.raises(RuntimeError, match="document_storage_unavailable"):
        store.get(TENANT_B, reference)
    store.delete(TENANT_A, reference)
    assert backend.last_deleted_version == "version-1" and not backend.objects


def test_malware_never_reaches_storage():
    backend = Store()
    with pytest.raises(MalwareDetectedError):
        MinioDocumentStore(backend, "documents", Scanner(blocked=True)).put(TENANT_A, b"test")
    assert backend.objects == {}


def test_unavailable_scanner_never_reaches_storage():
    class UnavailableScanner:
        def scan(self, content):
            raise MalwareScanUnavailableError("malware_scan_unavailable")

    backend = Store()
    with pytest.raises(MalwareScanUnavailableError):
        MinioDocumentStore(backend, "documents", UnavailableScanner()).put(TENANT_A, b"document")
    assert backend.objects == {}


def test_mutable_buffer_is_rejected_before_scan_or_storage():
    scanner = Scanner()
    backend = Store()
    with pytest.raises(ValueError, match="invalid_document"):
        MinioDocumentStore(backend, "documents", scanner).put(TENANT_A, bytearray(b"document"))
    assert scanner.scans == [] and backend.objects == {}


@pytest.mark.parametrize("replacement", [b"changed", b"sensitive document plus extra"])
def test_detects_content_tampering_and_releases_connection(replacement):
    backend = Store()
    store = MinioDocumentStore(backend, "documents", Scanner())
    reference = store.put(TENANT_A, b"sensitive document")
    key = next(iter(backend.objects))
    backend.objects[key] = replacement
    with pytest.raises(RuntimeError, match="document_integrity_failed"):
        store.get(TENANT_A, reference)
    assert backend.last_response.closed and backend.last_response.released


def test_rejects_unsafe_scope_and_reference_without_storage_access():
    store = MinioDocumentStore(Store(), "documents", Scanner())
    with pytest.raises(ValueError, match="invalid_document_scope"):
        store.put("../tenant", b"input")
    with pytest.raises(ValueError, match="invalid_document_reference"):
        DocumentReference("../escape", "0" * 64, 1)


@pytest.mark.parametrize("version", [None, "", "null", "bad\nversion", "\ud800"])
def test_rejects_nonimmutable_or_invalid_reference_versions(version):
    with pytest.raises(ValueError, match="invalid_document_reference"):
        DocumentReference("a" * 64, "b" * 64, 1, version)


@pytest.mark.parametrize("operation", ["get", "delete"])
def test_forged_or_changed_reference_never_reaches_storage(operation):
    backend = Store()
    store = MinioDocumentStore(backend, "documents", Scanner())
    reference = store.put(TENANT_A, b"document")
    forged = SimpleNamespace(object_id=reference.object_id, sha256=reference.sha256,
                             size_bytes=reference.size_bytes, version_id=None)
    with pytest.raises(ValueError, match="invalid_document_reference"):
        getattr(store, operation)(TENANT_A, forged)
    object.__setattr__(reference, "version_id", "null")
    with pytest.raises(ValueError, match="invalid_document_reference"):
        getattr(store, operation)(TENANT_A, reference)
    assert backend.last_response is None and backend.last_deleted_version is None
    assert len(backend.objects) == 1


@pytest.mark.parametrize("object_id,digest", [(None, "a" * 64), ("a" * 64, None)])
def test_reference_rejects_non_string_identifiers(object_id, digest):
    with pytest.raises(ValueError, match="invalid_document_reference"):
        DocumentReference(object_id, digest, 1, "version-1")


def test_disabled_bucket_versioning_never_writes():
    class Unversioned(Store):
        def get_bucket_versioning(self, bucket):
            return SimpleNamespace(status="Suspended")

    backend = Unversioned()
    with pytest.raises(RuntimeError, match="document_storage_unavailable"):
        MinioDocumentStore(backend, "documents", Scanner()).put(TENANT_A, b"document")
    assert not backend.objects


def test_missing_upload_version_does_not_issue_a_mutable_reference():
    class RacedVersioning(Store):
        def put_object(self, bucket, key, data, size, **kwargs):
            super().put_object(bucket, key, data, size, **kwargs)
            return SimpleNamespace(version_id="null")

    backend = RacedVersioning()
    with pytest.raises(RuntimeError, match="document_storage_unavailable"):
        MinioDocumentStore(backend, "documents", Scanner()).put(TENANT_A, b"document")
    # The upload may have succeeded before versioning changed; it is not deleted blindly.
    assert len(backend.objects) == 1


def test_mismatched_response_version_is_not_returned_and_connection_is_released():
    class WrongVersion(Store):
        def get_object(self, bucket, key, version_id=None):
            response = super().get_object(bucket, key, version_id)
            response.headers["x-amz-version-id"] = "different-version"
            return response

    backend = WrongVersion()
    store = MinioDocumentStore(backend, "documents", Scanner())
    reference = store.put(TENANT_A, b"document")
    with pytest.raises(RuntimeError, match="document_storage_unavailable"):
        store.get(TENANT_A, reference)
    assert backend.last_response.closed and backend.last_response.released
