import json
import os
from dataclasses import replace
from datetime import datetime, timedelta, timezone
from pathlib import Path
from uuid import uuid4

import pytest
from fastapi.testclient import TestClient
from jsonschema import Draft202012Validator, FormatChecker
from psycopg.conninfo import conninfo_to_dict

from openconnect_knowledge.app import app
from openconnect_knowledge.chunking import DocumentScope
from openconnect_knowledge.postgres_index import PostgresKnowledgeIndex
from openconnect_worker_common.security.signing import sign_bytes
from test_postgres_index import projection

DSN = os.getenv("OC_TEST_KNOWLEDGE_PG_DSN")
pytestmark = pytest.mark.skipif(not DSN, reason="Set OC_TEST_KNOWLEDGE_PG_DSN for disposable PostgreSQL tests")
KEY = bytes.fromhex("ab" * 32)


def test_signed_search_enforces_prefiltered_acl_and_contract(engine, monkeypatch):
    settings = conninfo_to_dict(DSN)
    assert settings.get("dbname") == "openconnect_test" and settings.get("host") in {"127.0.0.1", "::1", "localhost"}
    tenant = str(uuid4())
    scope = DocumentScope(tenant, "app", "collection", "public", "v1", "a" * 64)
    store = PostgresKnowledgeIndex(DSN)
    store.upsert(projection(scope, "forest wood"), expected_generation=0, principals=["alice"])
    store.upsert(projection(replace(scope, document_id="private"), "forest tree"),
                 expected_generation=0, principals=["bob"])
    monkeypatch.setenv("OC_KNOWLEDGE_SEARCH_ENABLED", "true")
    monkeypatch.setenv("OC_KNOWLEDGE_SEARCH_DSN", DSN)
    monkeypatch.setenv("OPENCONNECT_WORKER_TOKEN", "test-token")
    monkeypatch.setenv("OPENCONNECT_WORKER_SIGNING_KEY", KEY.hex())
    monkeypatch.setattr("openconnect_knowledge.app._load_reranker", lambda: engine[0])
    now = datetime.now(timezone.utc)
    command = {"schemaVersion": "openconnect.v1", "taskId": "test", "kind": "knowledge.search", "tenantId": tenant,
               "issuedAt": (now - timedelta(seconds=1)).isoformat(), "expiresAt": (now + timedelta(minutes=1)).isoformat(),
               "payload": {"applicationId": "app", "collectionId": "collection", "principalId": "alice", "query": "forest"}}
    with TestClient(app) as client:
        assert "knowledge.search" in [item["id"] for item in client.get("/internal/v1/capabilities", headers={
            "Authorization": "Bearer test-token"}).json()["capabilities"]]
        body = json.dumps(command).encode()
        assert client.post("/internal/v1/execute", content=body, headers={"Authorization": "Bearer test-token",
            "X-OpenConnect-Signature": "0" * 64}).status_code == 401
        response = client.post("/internal/v1/execute", content=body, headers={"Authorization": "Bearer test-token",
            "X-OpenConnect-Signature": sign_bytes(body, KEY)})
        assert response.status_code == 200
        hits = response.json()["result"]["hits"]
        assert [item["hit"]["document_id"] for item in hits] == ["public"]
        schema_dir = Path(__file__).resolve().parents[3] / "contracts" / "jsonschema"
        for document, name in [(command, "knowledge-search-request.json"),
                               (response.json(), "knowledge-search-response.json")]:
            schema = json.loads((schema_dir / name).read_text(encoding="utf-8"))
            Draft202012Validator.check_schema(schema)
            Draft202012Validator(schema, format_checker=FormatChecker()).validate(document)
        command["payload"]["hybrid"] = {"vector": [1.0, 0.0], "embeddingSpaceId": "a" * 64,
                                        "modelId": "fixture-model", "fingerprint": "b" * 64}
        body = json.dumps(command).encode()
        hybrid = client.post("/internal/v1/execute", content=body, headers={"Authorization": "Bearer test-token",
            "X-OpenConnect-Signature": sign_bytes(body, KEY)})
        assert hybrid.status_code == 200
        assert [item["hit"]["document_id"] for item in hybrid.json()["result"]["hits"]] == ["public"]
        assert not list(Draft202012Validator(json.loads((schema_dir / "knowledge-search-request.json").read_text(
            encoding="utf-8"))).iter_errors(command))
        assert not list(Draft202012Validator(json.loads((schema_dir / "knowledge-search-response.json").read_text(
            encoding="utf-8"))).iter_errors(hybrid.json()))


def test_search_is_not_advertised_when_disabled(engine, monkeypatch):
    monkeypatch.delenv("OC_KNOWLEDGE_SEARCH_ENABLED", raising=False)
    monkeypatch.setenv("OPENCONNECT_WORKER_TOKEN", "test-token")
    monkeypatch.setenv("OPENCONNECT_WORKER_SIGNING_KEY", KEY.hex())
    monkeypatch.setattr("openconnect_knowledge.app._load_reranker", lambda: engine[0])
    with TestClient(app) as client:
        advertised = client.get("/internal/v1/capabilities", headers={"Authorization": "Bearer test-token"}).json()
        assert "knowledge.search" not in [item["id"] for item in advertised["capabilities"]]
