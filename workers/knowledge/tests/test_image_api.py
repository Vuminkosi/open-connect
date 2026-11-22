import json
import os
import secrets
from datetime import datetime, timedelta, timezone
from pathlib import Path

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
pytestmark = pytest.mark.skipif(not DSN, reason="Requires disposable PostgreSQL")
KEY = bytes.fromhex("ab" * 32)


def test_signed_image_upsert_and_acl_search(monkeypatch):
    settings = conninfo_to_dict(DSN)
    assert settings.get("dbname") == "openconnect_test" and settings.get("host") in {"127.0.0.1", "::1", "localhost"}
    tenant = "0" + "".join(secrets.choice("0123456789ABCDEFGHJKMNPQRSTVWXYZ") for _ in range(25))
    scope = DocumentScope(tenant, "app", "collection", "doc", "v1", "a" * 64)
    PostgresKnowledgeIndex(DSN).upsert(projection(scope), expected_generation=0, principals=["alice"])
    monkeypatch.setenv("OC_KNOWLEDGE_IMAGE_ENABLED", "true")
    monkeypatch.setenv("OC_KNOWLEDGE_SEARCH_DSN", DSN)
    monkeypatch.setenv("OPENCONNECT_WORKER_TOKEN", "test-token")
    monkeypatch.setenv("OPENCONNECT_WORKER_SIGNING_KEY", KEY.hex())
    now = datetime.now(timezone.utc)
    common = {"schemaVersion": "openconnect.v1", "taskId": "test", "tenantId": tenant,
              "issuedAt": (now - timedelta(seconds=1)).isoformat(), "expiresAt": (now + timedelta(minutes=1)).isoformat()}
    model = {"vector": [1.0, 0.0], "embeddingSpaceId": "c" * 64, "modelId": "clip", "fingerprint": "d" * 64}
    upsert = {**common, "kind": "knowledge.image.upsert", "payload": {**model, "applicationId": "app",
              "collectionId": "collection", "documentId": "doc", "sourceRevision": "v1", "expectedGeneration": 1,
              "imageId": "image", "imageRevision": "image-v1"}}
    search = {**common, "kind": "knowledge.image.search", "payload": {**model, "applicationId": "app",
              "collectionId": "collection", "principalId": "alice"}}

    def send(client, command, bad=False):
        body = json.dumps(command).encode()
        return client.post("/internal/v1/execute", content=body, headers={"Authorization": "Bearer test-token",
            "X-OpenConnect-Signature": "0" * 64 if bad else sign_bytes(body, KEY)})

    with TestClient(app) as client:
        assert client.get("/health/ready").status_code == 200
        caps = client.get("/internal/v1/capabilities", headers={"Authorization": "Bearer test-token"}).json()
        assert {"knowledge.image.upsert", "knowledge.image.search"} <= {x["id"] for x in caps["capabilities"]}
        assert send(client, upsert, bad=True).status_code == 401
        stored = send(client, upsert)
        assert stored.json()["result"] == {"stored": True}
        assert send(client, upsert).status_code == 200
        found = send(client, search)
        assert found.json()["result"]["hits"][0]["image_id"] == "image"
        root = Path(__file__).resolve().parents[3] / "contracts" / "jsonschema"
        for value, name in [(upsert, "knowledge-image-request.json"), (search, "knowledge-image-request.json"),
                            (stored.json(), "knowledge-image-response.json"), (found.json(), "knowledge-image-response.json")]:
            schema = json.loads((root / name).read_text(encoding="utf-8"))
            Draft202012Validator.check_schema(schema)
            Draft202012Validator(schema, format_checker=FormatChecker()).validate(value)
        denied = json.loads(json.dumps(search))
        denied["payload"]["principalId"] = "bob"
        assert send(client, denied).json()["result"]["hits"] == []
        denied["tenantId"] = "01ARZ3NDEKTSV4RRFFQ69G5FAV"
        assert send(client, denied).json()["result"]["hits"] == []
        replacement = json.loads(json.dumps(upsert))
        replacement["payload"].update(imageRevision="image-v2", vector=[0.0, 1.0])
        assert send(client, replacement).status_code == 409
        replacement["payload"]["expectedImageRevision"] = "image-v1"
        assert send(client, replacement).status_code == 200
        assert send(client, upsert).status_code == 409
        invalid = json.loads(json.dumps(search))
        invalid["payload"]["vector"] = [0.0, 0.0]
        assert send(client, invalid).status_code == 422
