import hashlib
import io
import json
from pathlib import Path
from uuid import uuid4

import pytest
from jsonschema import Draft202012Validator
from pydantic import ValidationError

from openconnect_knowledge.app import IndexDeletePayload, IndexPayload, IndexStatusPayload
from openconnect_knowledge.chunking import DocumentScope
from openconnect_knowledge.index_jobs import IndexJob, _payload
from openconnect_knowledge.minio_resolver import MinioResolver, storage_pool

TENANT = "01ARZ3NDEKTSV4RRFFQ69G5FAV"


def test_storage_pool_uses_explicit_private_ca_and_requires_certificate_verification(tmp_path):
    ca = tmp_path / "private-ca.pem"
    ca.write_text("fixture", encoding="utf-8")
    pool = storage_pool(tls=True, ca_file=str(ca))
    assert pool.connection_pool_kw["ca_certs"] == str(ca)
    assert pool.connection_pool_kw["cert_reqs"] == "CERT_REQUIRED"
    assert pool.connection_pool_kw["retries"].total is False
    with pytest.raises(ValueError, match="invalid_storage_configuration"):
        storage_pool(tls=False, ca_file=str(ca))


@pytest.mark.parametrize("tls,ca_file", [(False, "relative.pem"), (True, "relative.pem"),
                                         (True, "/nonexistent/ca.pem")])
def test_storage_pool_rejects_invalid_or_insecure_private_ca(tls, ca_file):
    with pytest.raises(ValueError, match="invalid_storage_configuration"):
        storage_pool(tls=tls, ca_file=ca_file)


@pytest.mark.parametrize(("name", "model"), [
    ("knowledge-index-request", IndexPayload),
    ("knowledge-index-status-request", IndexStatusPayload),
    ("knowledge-index-delete-request", IndexDeletePayload),
])
def test_shared_request_fixtures_agree_with_worker_models(name, model):
    root = Path(__file__).resolve().parents[3] / "contracts" / "fixtures"
    valid = json.loads((root / "valid" / f"{name}.json").read_text(encoding="utf-8"))
    invalid = json.loads((root / "invalid" / f"{name}.json").read_text(encoding="utf-8"))
    model.model_validate(valid["payload"])
    with pytest.raises(ValidationError):
        model.model_validate(invalid["payload"])


@pytest.mark.parametrize("changes", [
    {"state": "completed", "result_generation": None},
    {"state": "pending", "result_generation": 1},
    {"state": "leased", "result_generation": None, "retry_at": "2026-01-01T00:00:00Z"},
    {"attempts": 9},
])
def test_index_status_schema_rejects_inconsistent_job_states(changes):
    root = Path(__file__).resolve().parents[3] / "contracts"
    schema = json.loads((root / "jsonschema" / "knowledge-index-status-response.json").read_text(encoding="utf-8"))
    fixture = json.loads((root / "fixtures" / "valid" / "knowledge-index-status-response.json").read_text(encoding="utf-8"))
    fixture["result"].update(changes)
    assert not Draft202012Validator(schema).is_valid(fixture)


def test_index_status_schema_accepts_pending_retry():
    root = Path(__file__).resolve().parents[3] / "contracts"
    schema = json.loads((root / "jsonschema" / "knowledge-index-status-response.json").read_text(encoding="utf-8"))
    fixture = json.loads((root / "fixtures" / "valid" / "knowledge-index-status-response.json").read_text(encoding="utf-8"))
    fixture["result"].update(state="pending", result_generation=None, retry_at="2026-01-01T00:00:00Z")
    assert Draft202012Validator(schema).is_valid(fixture)


class Response(io.BytesIO):
    def __init__(self, data):
        super().__init__(data)
        self.released = False
        self.headers = {}

    def release_conn(self):
        self.released = True


class Client:
    def __init__(self, data):
        self.response = Response(data)
        self.calls = []

    def get_object(self, bucket, key, *, version_id):
        self.calls.append((bucket, key, version_id))
        self.response.headers["x-amz-version-id"] = version_id
        return self.response


def test_resolves_tenant_scoped_version_and_verifies_content():
    content = b"document"
    client = Client(content)
    result = MinioResolver(client, "private-bucket").read(TENANT, "a" * 64, "version-1", len(content),
                                                          hashlib.sha256(content).hexdigest())
    assert result == content and client.response.closed and client.response.released
    assert client.calls == [("private-bucket", f"v1/tenants/{hashlib.sha256(TENANT.encode()).hexdigest()}/documents/" +
                             "a" * 64, "version-1")]


@pytest.mark.parametrize("content,size,digest", [(b"bad", 3, "a" * 64), (b"too long", 3, "a" * 64),
                                                  (b"too short", 20, "a" * 64)])
def test_corrupt_or_wrong_size_never_returns_data(content, size, digest):
    client = Client(content)
    with pytest.raises(RuntimeError, match="document_integrity_failed"):
        MinioResolver(client, "private-bucket").read(TENANT, "a" * 64, "version", size, digest)
    assert client.response.closed and client.response.released


def test_rejects_arbitrary_keys_and_unversioned_objects_before_storage_access():
    client = Client(b"x")
    with pytest.raises(ValueError, match="invalid_object_reference"):
        MinioResolver(client, "private-bucket").read(TENANT, "../secret", "", 1, "a" * 64)
    assert not client.calls


def test_rejects_mismatched_version_and_releases_response():
    class WrongVersion(Client):
        def get_object(self, bucket, key, *, version_id):
            response = super().get_object(bucket, key, version_id=version_id)
            response.headers["x-amz-version-id"] = "different"
            return response

    client = WrongVersion(b"document")
    with pytest.raises(RuntimeError, match="document_storage_unavailable"):
        MinioResolver(client, "private-bucket").read(TENANT, "a" * 64, "version-1", 8,
                                                       hashlib.sha256(b"document").hexdigest())
    assert client.response.closed and client.response.released


def test_rejects_invalid_utf8_version_before_storage_access():
    client = Client(b"x")
    with pytest.raises(ValueError, match="invalid_object_reference"):
        MinioResolver(client, "private-bucket").read(TENANT, "a" * 64, "\ud800", 1, "b" * 64)
    assert not client.calls


def test_null_version_is_rejected_by_resolver_job_api_and_contract():
    client = Client(b"x")
    with pytest.raises(ValueError, match="invalid_object_reference"):
        MinioResolver(client, "private-bucket").read(TENANT, "a" * 64, "null", 1, "b" * 64)
    assert not client.calls

    scope = DocumentScope(TENANT, "app", "collection", "doc", "v1", "c" * 64)
    job = IndexJob(scope, "a" * 64, "b" * 64, 1, "null", 0, ("alice",))
    with pytest.raises(ValueError, match="invalid_job"):
        _payload(job)

    payload = {"jobId": str(uuid4()), "applicationId": "app", "collectionId": "collection",
               "documentId": "doc", "sourceRevision": "v1", "embeddingSpaceId": "c" * 64,
               "objectId": "a" * 64, "objectSha256": "b" * 64, "objectSize": 1,
               "objectVersion": "null", "expectedGeneration": 0, "principals": ["alice"]}
    with pytest.raises(ValidationError):
        IndexPayload.model_validate(payload)
    root = Path(__file__).resolve().parents[3] / "contracts" / "jsonschema" / "knowledge-index-request.json"
    schema = json.loads(root.read_text(encoding="utf-8"))
    assert not Draft202012Validator(schema["properties"]["payload"]).is_valid(payload)
    payload["objectVersion"] = "version-1"
    assert Draft202012Validator(schema["properties"]["payload"]).is_valid(payload)


@pytest.mark.parametrize("version", ["version\x00id", "version\nid", "version\n", "version\x7fid"])
def test_control_characters_in_version_are_rejected_across_boundaries(version):
    client = Client(b"x")
    with pytest.raises(ValueError, match="invalid_object_reference"):
        MinioResolver(client, "private-bucket").read(TENANT, "a" * 64, version, 1, "b" * 64)
    assert not client.calls

    scope = DocumentScope(TENANT, "app", "collection", "doc", "v1", "c" * 64)
    with pytest.raises(ValueError, match="invalid_job"):
        _payload(IndexJob(scope, "a" * 64, "b" * 64, 1, version, 0, ("alice",)))

    payload = {"jobId": str(uuid4()), "applicationId": "app", "collectionId": "collection",
               "documentId": "doc", "sourceRevision": "v1", "embeddingSpaceId": "c" * 64,
               "objectId": "a" * 64, "objectSha256": "b" * 64, "objectSize": 1,
               "objectVersion": version, "expectedGeneration": 0, "principals": ["alice"]}
    with pytest.raises(ValidationError):
        IndexPayload.model_validate(payload)
    root = Path(__file__).resolve().parents[3] / "contracts" / "jsonschema" / "knowledge-index-request.json"
    schema = json.loads(root.read_text(encoding="utf-8"))
    assert not Draft202012Validator(schema["properties"]["payload"]).is_valid(payload)
