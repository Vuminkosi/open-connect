import asyncio
import base64
import json
import threading
import time
from datetime import datetime, timedelta, timezone
from pathlib import Path

import pytest
from fastapi.testclient import TestClient
from jsonschema import Draft202012Validator, FormatChecker

from openconnect_analytics_inference.app import app
from openconnect_analytics_inference import app as analytics_app
from openconnect_worker_common.security.signing import sign_bytes

KEY = bytes.fromhex("ab" * 32)


def test_shared_analytics_request_fixtures_match_request_model():
    root = Path(__file__).resolve().parents[3] / "contracts" / "fixtures"
    analytics_app.ExecuteRequest.model_validate_json((root / "valid" / "analytics-request.json").read_bytes())
    with pytest.raises(ValueError):
        analytics_app.ExecuteRequest.model_validate_json((root / "invalid" / "analytics-request.json").read_bytes())


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


def test_request_representations_do_not_expose_input_or_scope():
    request = command()
    request["taskId"] = "private-task"
    request["tenantId"] = "private-tenant"
    request["payload"].update(feature="private-feature", unit="private-unit", lineage="private-lineage",
                              values=[123456.789], forecast={"timestamps": ["2026-01-01T00:00:00Z"],
                              "intervalSeconds": 60, "seasonalPeriod": 1, "horizon": 1})
    parsed = analytics_app.ExecuteRequest.model_validate(request)
    for value in (parsed, parsed.payload, parsed.payload.forecast):
        representation = repr(value)
        for secret in ("private-task", "private-tenant", "private-feature", "private-unit", "private-lineage",
                       "123456.789", "2026, 1, 1"):
            assert secret not in representation
    assert parsed.model_dump()["payload"]["values"] == [123456.789]


def submit(client: TestClient, payload: dict, *, signature: str | None = None):
    body = json.dumps(payload).encode()
    return client.post("/internal/v1/execute", content=body, headers={
        "Authorization": "Bearer private-token",
        "X-OpenConnect-Signature": signature or sign_bytes(body, KEY),
    })


def test_cancelled_calculation_keeps_slot_until_thread_finishes(monkeypatch):
    started, release, finished = threading.Event(), threading.Event(), threading.Event()
    original = analytics_app.analyze

    def slow_analyze(values, *, threshold):
        started.set()
        try:
            assert release.wait(5)
            return original(values, threshold=threshold)
        finally:
            finished.set()

    monkeypatch.setattr(analytics_app, "analyze", slow_analyze)
    request = analytics_app.ExecuteRequest.model_validate(command())

    async def exercise():
        task = asyncio.create_task(asyncio.to_thread(analytics_app._calculate, request))
        try:
            assert await asyncio.to_thread(started.wait, 5)
            task.cancel()
            with pytest.raises(asyncio.CancelledError):
                await task
            with pytest.raises(analytics_app._Busy):
                analytics_app._calculate(request)
        finally:
            release.set()
            assert await asyncio.to_thread(finished.wait, 5)

    asyncio.run(exercise())
    assert analytics_app._slot.acquire(blocking=False)
    analytics_app._slot.release()


def test_concurrent_calculation_returns_rate_limited(client):
    assert analytics_app._slot.acquire(blocking=False)
    try:
        assert submit(client, command()).status_code == 429
    finally:
        analytics_app._slot.release()


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
    assert "trend" not in result.json()["result"]


def test_cloud_execution_requires_consumed_claim_and_disables_unclaimed_path(client, monkeypatch):
    monkeypatch.setenv("OPENCONNECT_CLOUD_CONSUMPTION_ENABLED", "true")
    payload = command()
    payload["taskId"] = "11111111-1111-1111-1111-111111111111"
    payload["tenantId"] = "01ARZ3NDEKTSV4RRFFQ69G5FAV"
    encoded = json.dumps(payload).encode()
    envelope = {"schemaVersion": "openconnect.cloud.worker.analytics/v1",
                "reservationId": payload["taskId"], "claimId": payload["taskId"],
                "payload": base64.b64encode(encoded).decode(), "key": base64.b64encode(bytes(32)).decode()}

    class Consumer:
        status = "consumed"
        calls = 0

        def consume(self, _reservation, _claim, raw, _key):
            self.calls += 1
            assert raw == encoded
            return self.status

        def ready_deployment(self):
            return payload["taskId"]

    consumer = Consumer()
    monkeypatch.setattr(analytics_app, "_cloud_consumer", lambda: consumer)
    endpoint = "/internal/cloud/v1/analytics/execute"
    assert client.get("/internal/cloud/v1/analytics/ready").status_code == 401
    readiness = client.get("/internal/cloud/v1/analytics/ready", headers={"Authorization": "Bearer private-token"})
    assert readiness.json() == {"ready": True, "deploymentId": payload["taskId"]}
    assert client.post(endpoint, json=envelope).status_code == 401
    assert consumer.calls == 0
    response = client.post(endpoint, json=envelope, headers={"Authorization": "Bearer private-token"})
    assert response.status_code == 200
    assert response.json()["taskId"] == payload["taskId"]
    assert 1 <= response.json()["usage"]["computeMilliseconds"] <= 120000
    consumer.status = "already_consumed"
    assert client.post(endpoint, json=envelope, headers={"Authorization": "Bearer private-token"}).status_code == 503
    assert submit(client, payload).status_code == 503


def test_cloud_readiness_fails_closed_for_missing_or_invalid_configuration(client, monkeypatch):
    analytics_app._cloud_consumer.cache_clear()
    monkeypatch.setenv("OPENCONNECT_CLOUD_CONSUMPTION_ENABLED", "true")
    monkeypatch.delenv("OPENCONNECT_CLOUD_AUTHORITY_ORIGIN", raising=False)
    assert client.get("/health/ready").status_code == 503
    assert client.get("/internal/v1/capabilities", headers={"Authorization": "Bearer private-token"}).status_code == 503
    monkeypatch.setenv("OPENCONNECT_CLOUD_CONSUMPTION_ENABLED", "invalid")
    assert client.get("/health/ready").status_code == 503
    analytics_app._cloud_consumer.cache_clear()


def test_signed_opt_in_trend_and_contract_parity(client: TestClient) -> None:
    payload = command()
    payload["payload"]["values"] = [2, None, 6, 8]
    payload["payload"]["trend"] = True
    response = submit(client, payload)
    assert response.status_code == 200
    result = response.json()["result"]["trend"]
    assert result["slope_per_sample"] == pytest.approx(2)
    assert "missing_values" in result["warnings"]
    root = Path(__file__).resolve().parents[3] / "contracts" / "jsonschema"
    for value, name in [(payload, "analytics-request.json"), (response.json(), "analytics-response.json")]:
        schema = json.loads((root / name).read_text(encoding="utf-8"))
        Draft202012Validator.check_schema(schema)
        Draft202012Validator(schema, format_checker=FormatChecker()).validate(value)

    payload["payload"]["trend"] = "true"
    assert submit(client, payload).status_code == 422


def test_missing_keys_fail_closed(client: TestClient, monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.delenv("OPENCONNECT_WORKER_SIGNING_KEY")
    assert client.get("/health/ready").status_code == 503
    assert submit(client, command()).status_code == 503


def test_request_schema_matches_nullable_forecast_and_numeric_limits(client: TestClient) -> None:
    root = Path(__file__).resolve().parents[3] / "contracts" / "jsonschema"
    schema = json.loads((root / "analytics-request.json").read_text(encoding="utf-8"))
    validator = Draft202012Validator(schema, format_checker=FormatChecker())
    payload = command()
    payload["payload"]["forecast"] = None
    payload["payload"]["values"] = [1e150, -1e150, None]
    validator.validate(payload)
    assert submit(client, payload).status_code == 200
    payload["payload"]["values"] = [1e151]
    assert not validator.is_valid(payload)
    assert submit(client, payload).status_code == 422


def test_rejects_bad_signature_and_tampering(client: TestClient) -> None:
    assert submit(client, command(), signature="0" * 64).status_code == 401
    assert client.post("/internal/v1/execute", content=b"{}", headers={
        "Authorization": "Bearer wrong", "X-OpenConnect-Signature": sign_bytes(b"{}", KEY),
    }).status_code == 401


@pytest.mark.parametrize("field", ["tenantId", "values"])
def test_signed_duplicate_keys_are_rejected(client: TestClient, field: str) -> None:
    payload = json.dumps(command())
    if field == "tenantId":
        payload = payload.replace('"tenantId":', '"tenantId": "other-tenant", "tenantId":', 1)
    else:
        payload = payload.replace('"values":', '"values": [999], "values":', 1)
    body = payload.encode()
    response = client.post("/internal/v1/execute", content=body, headers={
        "Authorization": "Bearer private-token", "X-OpenConnect-Signature": sign_bytes(body, KEY)})
    assert response.status_code == 422 and "other-tenant" not in response.text


def test_result_is_not_returned_after_request_expiry(client: TestClient, monkeypatch: pytest.MonkeyPatch) -> None:
    payload = command()
    payload["expiresAt"] = (datetime.now(timezone.utc) + timedelta(milliseconds=500)).isoformat()
    original = analytics_app.analyze
    started = []
    finished = threading.Event()
    def slow(*args, **kwargs):
        started.append(True)
        try:
            time.sleep(0.6)
            return original(*args, **kwargs)
        finally:
            finished.set()
    monkeypatch.setattr(analytics_app, "analyze", slow)
    try:
        assert submit(client, payload).status_code == 422
        assert started == [True]
    finally:
        assert finished.wait(5)


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

    payload["payload"]["trend"] = True
    combined = submit(client, payload)
    assert combined.status_code == 200
    assert "trend" in combined.json()["result"] and "forecast" in combined.json()["result"]
    for value, name in [(payload, "analytics-request.json"), (combined.json(), "analytics-response.json")]:
        schema = json.loads((root / name).read_text(encoding="utf-8"))
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
