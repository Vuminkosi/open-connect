import json
import os
import secrets
from datetime import datetime, timedelta, timezone
from pathlib import Path
from uuid import uuid4

import pytest
from fastapi.testclient import TestClient
from jsonschema import Draft202012Validator, FormatChecker

from openconnect_knowledge.app import app
from openconnect_knowledge.chunking import DocumentScope
from openconnect_knowledge.index_jobs import IndexJob, PostgresIndexJobs
from openconnect_knowledge.postgres_index import PostgresKnowledgeIndex
from openconnect_knowledge.postgres_index import SearchScope
from openconnect_worker_common.security.signing import sign_bytes
from test_postgres_index import projection

pytestmark = pytest.mark.skipif(not os.getenv("OC_TEST_KNOWLEDGE_PG_DSN"), reason="Requires disposable PostgreSQL")
KEY = bytes.fromhex("ab" * 32)


@pytest.fixture
def setup_job():
    tenant = "0" + "".join(secrets.choice("0123456789ABCDEFGHJKMNPQRSTVWXYZ") for _ in range(25))
    scope = DocumentScope(tenant, "app", "collection", "doc", "v1", "a" * 64)
    job = IndexJob(scope, "c" * 64, "d" * 64, 123, "version-id", 0, ("alice",))
    dsn = os.environ["OC_TEST_KNOWLEDGE_PG_DSN"]
    from psycopg.conninfo import conninfo_to_dict
    settings = conninfo_to_dict(dsn)
    assert settings.get("dbname") == "openconnect_test" and settings.get("host") in {"127.0.0.1", "::1", "localhost"}
    return PostgresIndexJobs(dsn), PostgresKnowledgeIndex(dsn), job


def test_signed_index_submission_requires_live_worker_and_is_idempotent(setup_job, monkeypatch):
    jobs, _, job = setup_job
    worker = uuid4()
    jobs.record_worker(worker, job.scope.embedding_space_id)
    monkeypatch.setenv("OC_INDEX_SUBMISSION_ENABLED", "true")
    monkeypatch.setenv("OC_KNOWLEDGE_SEARCH_DSN", os.environ["OC_TEST_KNOWLEDGE_PG_DSN"])
    monkeypatch.setenv("OPENCONNECT_WORKER_TOKEN", "test-token")
    monkeypatch.setenv("OPENCONNECT_WORKER_SIGNING_KEY", KEY.hex())
    now = datetime.now(timezone.utc)
    command = {"schemaVersion": "openconnect.v1", "taskId": "task", "kind": "knowledge.index",
               "tenantId": job.scope.tenant_id, "issuedAt": (now - timedelta(seconds=1)).isoformat(),
               "expiresAt": (now + timedelta(minutes=1)).isoformat(), "payload": {
                   "jobId": str(uuid4()), "applicationId": job.scope.application_id,
                   "collectionId": job.scope.collection_id, "documentId": job.scope.document_id,
                   "sourceRevision": job.scope.source_revision, "embeddingSpaceId": job.scope.embedding_space_id,
                   "objectId": job.object_id, "objectSha256": job.object_sha256, "objectSize": job.object_size,
                   "objectVersion": job.object_version, "expectedGeneration": 0, "principals": ["alice"]}}

    def send(client, payload, bad=False):
        body = json.dumps(payload).encode()
        return client.post("/internal/v1/execute", content=body, headers={"Authorization": "Bearer test-token",
            "X-OpenConnect-Signature": "0" * 64 if bad else sign_bytes(body, KEY)})

    try:
        with TestClient(app) as client:
            descriptors = client.get("/internal/v1/capabilities", headers={"Authorization": "Bearer test-token"}).json()
            assert "knowledge.index" in [item["id"] for item in descriptors["capabilities"]]
            assert send(client, command, bad=True).status_code == 401
            result = send(client, command)
            assert result.status_code == 202 and result.json()["result"]["created"] is True
            root = Path(__file__).resolve().parents[3] / "contracts" / "jsonschema"
            for value, name in [(command, "knowledge-index-request.json"), (result.json(), "knowledge-index-response.json")]:
                schema = json.loads((root / name).read_text(encoding="utf-8"))
                Draft202012Validator.check_schema(schema)
                Draft202012Validator(schema, format_checker=FormatChecker()).validate(value)
            jobs.retire_worker(worker)
            status_command = {**command, "kind": "knowledge.index.status", "payload": {
                "jobId": command["payload"]["jobId"], "applicationId": "app", "collectionId": "collection",
                "documentId": "doc", "principalId": "alice"}}
            status = send(client, status_command)
            assert status.status_code == 200 and status.json()["result"]["state"] == "pending"
            for value, name in [(status_command, "knowledge-index-status-request.json"),
                                (status.json(), "knowledge-index-status-response.json")]:
                schema = json.loads((root / name).read_text(encoding="utf-8"))
                Draft202012Validator.check_schema(schema)
                Draft202012Validator(schema, format_checker=FormatChecker()).validate(value)
            status_command["payload"]["principalId"] = "bob"
            assert send(client, status_command).status_code == 404
            assert send(client, status_command, bad=True).status_code == 401
            assert send(client, command).status_code == 200
            command["payload"]["objectId"] = "e" * 64
            assert send(client, command).status_code == 409
            command["payload"]["jobId"] = str(uuid4())
            assert send(client, command).status_code == 503
    finally:
        jobs.retire_worker(worker)


def test_signed_delete_tombstones_only_matching_generation_and_reports_reconciliation(setup_job, monkeypatch):
    _, index, job = setup_job
    index.upsert(projection(job.scope), expected_generation=0, principals=["alice"])
    monkeypatch.setenv("OC_INDEX_DELETE_ENABLED", "true")
    monkeypatch.setenv("OC_KNOWLEDGE_SEARCH_DSN", os.environ["OC_TEST_KNOWLEDGE_PG_DSN"])
    monkeypatch.setenv("OPENCONNECT_WORKER_TOKEN", "test-token")
    monkeypatch.setenv("OPENCONNECT_WORKER_SIGNING_KEY", KEY.hex())
    now = datetime.now(timezone.utc)
    command = {"schemaVersion": "openconnect.v1", "taskId": "delete", "kind": "knowledge.index.delete",
               "tenantId": job.scope.tenant_id, "issuedAt": (now - timedelta(seconds=1)).isoformat(),
               "expiresAt": (now + timedelta(minutes=1)).isoformat(), "payload": {
                   "applicationId": "app", "collectionId": "collection", "documentId": "doc", "expectedGeneration": 1}}

    def send(client, body, bad=False):
        data = json.dumps(body).encode()
        return client.post("/internal/v1/execute", content=data, headers={"Authorization": "Bearer test-token",
            "X-OpenConnect-Signature": "0" * 64 if bad else sign_bytes(data, KEY)})

    with TestClient(app) as client:
        assert send(client, command, bad=True).status_code == 401
        stale = json.loads(json.dumps(command))
        stale["payload"]["expectedGeneration"] = 0
        assert send(client, stale).status_code == 409
        response = send(client, command)
        assert response.status_code == 200 and response.json()["result"] == {"generation": 2, "reconciliation_state": "pending"}
        assert not index.search(SearchScope(job.scope.tenant_id, "app", "collection", "alice"), "forest")
        assert send(client, command).json()["result"]["generation"] == 2
        assert send(client, stale).status_code == 409
        root = Path(__file__).resolve().parents[3] / "contracts" / "jsonschema"
        for value, name in [(command, "knowledge-index-delete-request.json"),
                            (response.json(), "knowledge-index-delete-response.json")]:
            schema = json.loads((root / name).read_text(encoding="utf-8"))
            Draft202012Validator.check_schema(schema)
            Draft202012Validator(schema, format_checker=FormatChecker()).validate(value)
