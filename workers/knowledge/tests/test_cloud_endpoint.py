import base64
import json
from datetime import datetime, timedelta, timezone

import pytest
import test_app

from openconnect_knowledge import app as app_module

pytest_plugins = ["test_embeddings"]

service_fixture = pytest.fixture(name="service")(test_app.service.__wrapped__)

ID = "11111111-1111-1111-1111-111111111111"
HEADERS = {"Authorization": "Bearer test-token"}
PATH = "/internal/cloud/v1/knowledge-embed/execute"
READY = "/internal/cloud/v1/knowledge-embed/ready"


class Consumer:
    def __init__(self, status="consumed"):
        self.status = status
        self.calls = []

    def consume(self, reservation_id, claim_id, payload, key):
        self.calls.append((reservation_id, claim_id, payload, key))
        return self.status

    def ready_deployment(self):
        return ID


def envelope(expired=False):
    now = datetime.now(timezone.utc)
    command = {"schemaVersion": "openconnect.v1", "taskId": ID, "kind": "knowledge.embed",
               "tenantId": "00000000000000000000000001", "issuedAt": (now - timedelta(seconds=10)).isoformat(),
               "expiresAt": (now + timedelta(seconds=-1 if expired else 60)).isoformat(),
               "payload": {"inputKind": "query", "texts": ["private text"]}}
    payload = json.dumps(command).encode()
    key = b"k" * 32
    body = {"schemaVersion": "openconnect.cloud.worker.knowledge-embed/v1", "reservationId": ID,
            "claimId": ID, "payload": base64.b64encode(payload).decode(), "key": base64.b64encode(key).decode()}
    return json.dumps(body).encode(), payload, key


def test_claimed_execution_and_readiness(service, monkeypatch):
    client, _ = service
    consumer = Consumer()
    monkeypatch.setenv("OC_KNOWLEDGE_CLOUD_CONSUMPTION_ENABLED", "true")
    monkeypatch.setattr(app_module, "_cloud_consumer", lambda: consumer)
    body, payload, key = envelope()
    assert client.get(READY).status_code == 401
    ready = client.get(READY, headers=HEADERS)
    assert ready.json() == {"ready": True, "deploymentId": ID}
    assert ready.headers["cache-control"] == "no-store"
    assert client.post(PATH, content=body).status_code == 401
    response = client.post(PATH, content=body, headers=HEADERS)
    assert response.status_code == 200
    assert response.json()["kind"] == "knowledge.embed"
    assert len(response.json()["result"]["items"]) == 1
    assert response.headers["cache-control"] == "no-store"
    assert consumer.calls == [(ID, ID, payload, key)]


@pytest.mark.parametrize("status", ["already_consumed", "denied", "unavailable"])
def test_failed_claim_does_not_run_model(service, monkeypatch, status):
    client, _ = service
    consumer = Consumer(status)
    monkeypatch.setenv("OC_KNOWLEDGE_CLOUD_CONSUMPTION_ENABLED", "true")
    monkeypatch.setattr(app_module, "_cloud_consumer", lambda: consumer)
    monkeypatch.setattr(app_module, "_execute_model", lambda *_: pytest.fail("must not execute"))
    response = client.post(PATH, content=envelope()[0], headers=HEADERS)
    assert response.status_code == 503
    assert "private text" not in response.text
    assert len(consumer.calls) == 1


def test_disabled_expired_and_oversize_requests_fail_closed(service, monkeypatch):
    client, _ = service
    consumer = Consumer()
    monkeypatch.setattr(app_module, "_cloud_consumer", lambda: consumer)
    monkeypatch.setenv("OC_KNOWLEDGE_CLOUD_CONSUMPTION_ENABLED", "false")
    assert client.post(PATH, content=envelope()[0], headers=HEADERS).status_code == 503
    assert client.get(READY, headers=HEADERS).status_code == 503
    monkeypatch.setenv("OC_KNOWLEDGE_CLOUD_CONSUMPTION_ENABLED", "true")
    assert client.post(PATH, content=envelope(expired=True)[0], headers=HEADERS).status_code == 503
    assert client.post(PATH, content=b"x" * 176001, headers=HEADERS).status_code == 413
    assert consumer.calls == []


def test_claim_authority_failure_does_not_expose_details(service, monkeypatch):
    client, _ = service
    monkeypatch.setenv("OC_KNOWLEDGE_CLOUD_CONSUMPTION_ENABLED", "true")

    def unavailable():
        raise RuntimeError("private configuration")

    monkeypatch.setattr(app_module, "_cloud_consumer", unavailable)
    for response in (client.get(READY, headers=HEADERS), client.post(PATH, content=envelope()[0], headers=HEADERS)):
        assert response.status_code == 503
        assert "private configuration" not in response.text
