import io
from types import SimpleNamespace

import pytest

from openconnect_document_intelligence.malware import MalwareDetectedError
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
