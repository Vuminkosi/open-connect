import json
from datetime import datetime, timedelta, timezone

import pytest
from fastapi.testclient import TestClient

from openconnect_analytics_inference.app import app
from openconnect_worker_common.security.signing import sign_bytes

KEY = bytes.fromhex("ab" * 32)


@pytest.fixture
def client(monkeypatch: pytest.MonkeyPatch) -> TestClient:
    monkeypatch.setenv("OPENCONNECT_WORKER_TOKEN", "private-token")
    monkeypatch.setenv("OPENCONNECT_WORKER_SIGNING_KEY", KEY.hex())
    return TestClient(app)


def command() -> dict:
    now = datetime.now(timezone.utc)
    return {
        "schemaVersion": "openconnect.v1", "taskId": "task-1", "kind": "analytics.analyze",
        "tenantId": "tenant-1", "issuedAt": (now - timedelta(seconds=1)).isoformat(),
        "expiresAt": (now + timedelta(minutes=1)).isoformat(),
        "payload": {"feature": "weight", "unit": "kg", "lineage": "dataset-1",
                    "values": [1, 2, 3, 4, 5, 100]},
    }


def submit(client: TestClient, payload: dict, *, signature: str | None = None):
    body = json.dumps(payload).encode()
    return client.post("/internal/v1/execute", content=body, headers={
        "Authorization": "Bearer private-token",
        "X-OpenConnect-Signature": signature or sign_bytes(body, KEY),
    })


def test_signed_execution_and_private_capabilities(client: TestClient) -> None:
    assert client.get("/health/live").status_code == 200
    assert client.get("/health/ready").status_code == 200
    assert client.get("/internal/v1/capabilities").status_code == 401
    response = client.get("/internal/v1/capabilities", headers={"Authorization": "Bearer private-token"})
    assert response.json()["capabilities"][0]["id"] == "analytics.analyze"
    result = submit(client, command())
    assert result.status_code == 200
    assert result.json()["result"]["anomalies"][0]["index"] == 5
    assert result.json()["result"]["lineage"] == "dataset-1"


def test_missing_keys_fail_closed(client: TestClient, monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.delenv("OPENCONNECT_WORKER_SIGNING_KEY")
    assert client.get("/health/ready").status_code == 503
    assert submit(client, command()).status_code == 503


def test_rejects_bad_signature_and_tampering(client: TestClient) -> None:
    assert submit(client, command(), signature="0" * 64).status_code == 401
    assert client.post("/internal/v1/execute", content=b"{}", headers={
        "Authorization": "Bearer wrong", "X-OpenConnect-Signature": sign_bytes(b"{}", KEY),
    }).status_code == 401


@pytest.mark.parametrize("change", ["expired", "unknown", "nonfinite", "future"])
def test_invalid_requests_fail_without_exposing_payload(client: TestClient, change: str) -> None:
    payload = command()
    if change == "expired":
        payload["expiresAt"] = (datetime.now(timezone.utc) - timedelta(seconds=1)).isoformat()
    elif change == "future":
        payload["issuedAt"] = (datetime.now(timezone.utc) + timedelta(seconds=30)).isoformat()
    elif change == "unknown":
        payload["payload"]["unexpected"] = "secret-sentinel"
    else:
        payload["payload"]["values"] = [float("nan")]
    response = submit(client, payload)
    assert response.status_code == 422
    assert "secret-sentinel" not in response.text


def test_oversized_body_is_rejected(client: TestClient) -> None:
    response = client.post("/internal/v1/execute", content=b"x" * (256 * 1024 + 1),
                           headers={"Authorization": "Bearer private-token"})
    assert response.status_code == 413
