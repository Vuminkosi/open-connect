import json
from datetime import datetime, timedelta, timezone
from pathlib import Path

import pytest
from fastapi.testclient import TestClient
from jsonschema import Draft202012Validator, FormatChecker

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


def test_signed_forecast_and_contract_parity(client: TestClient) -> None:
    payload = command()
    payload["payload"]["values"] = [10, 20, 30] * 5
    start = datetime(2026, 1, 1, tzinfo=timezone.utc)
    payload["payload"]["forecast"] = {
        "timestamps": [(start + timedelta(days=index)).isoformat() for index in range(15)],
        "intervalSeconds": 86400, "seasonalPeriod": 3, "horizon": 3,
    }
    response = submit(client, payload)
    assert response.status_code == 200
    result = response.json()["result"]["forecast"]
    assert [point["value"] for point in result["points"]] == [10, 20, 30]
    assert result["backtest"]["mean_absolute_error"] == 0
    root = Path(__file__).resolve().parents[3] / "contracts" / "jsonschema"
    for value, name in [(payload, "analytics-forecast-request.json"),
                        (response.json(), "analytics-forecast-response.json")]:
        schema = json.loads((root / name).read_text(encoding="utf-8"))
        Draft202012Validator.check_schema(schema)
        Draft202012Validator(schema, format_checker=FormatChecker()).validate(value)


def test_forecast_missing_data_returns_warning_without_predictions(client: TestClient) -> None:
    payload = command()
    payload["payload"]["values"] = [1, 2, None, 4, 5, 6, 7]
    start = datetime(2026, 1, 1, tzinfo=timezone.utc)
    payload["payload"]["forecast"] = {
        "timestamps": [(start + timedelta(days=index)).isoformat() for index in range(7)],
        "intervalSeconds": 86400, "seasonalPeriod": 1, "horizon": 2,
    }
    response = submit(client, payload)
    assert response.status_code == 200
    result = response.json()["result"]["forecast"]
    assert not result["points"] and result["backtest"] is None
    assert "missing_values" in result["warnings"]
    payload["payload"]["forecast"]["timestamps"].pop()
    assert submit(client, payload).status_code == 422
