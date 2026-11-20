import base64
import json
from dataclasses import replace

from fastapi.testclient import TestClient
import pytest

import openconnect_classification.app as app_module
import openconnect_classification.cloud_execution as cloud_execution

client = TestClient(app_module.app)
headers = {"Authorization": "Bearer local-test-token"}
route = "/internal/cloud/v1/classification/execute"
reservation = "11111111-1111-1111-1111-111111111111"
claim = "22222222-2222-2222-2222-222222222222"


def envelope(payload=None):
    raw = payload or json.dumps({"schemaVersion": "openconnect.v1", "taskId": reservation, "input": {"text": "private fixture"}}).encode()
    return {"schemaVersion": "openconnect.cloud.worker.classification/v1", "reservationId": reservation,
            "claimId": claim, "payload": base64.b64encode(raw).decode(), "key": base64.b64encode(b"k" * 32).decode()}


def setup(monkeypatch, result="consumed"):
    monkeypatch.setattr(app_module, "settings", replace(app_module.settings, worker_token="local-test-token", fake_engine=True, is_production=False))

    class Admission:
        def __init__(self):
            self.calls = []

        def consume(self, reservation_id, claim_id, payload, key):
            self.calls.append((reservation_id, claim_id, payload, key))
            return result

    admission = Admission()
    monkeypatch.setattr(app_module, "configured_client", lambda: admission)
    return admission


def test_cloud_execution_requires_auth_and_executes_the_admitted_payload(monkeypatch):
    admission = setup(monkeypatch)
    data = envelope()
    assert client.post(route, json=data).status_code == 401
    assert not admission.calls
    response = client.post(route, json=data, headers=headers)
    assert response.status_code == 200
    assert response.json()["status"] == "completed"
    assert admission.calls == [(reservation, claim, base64.b64decode(data["payload"]), b"k" * 32)]


def test_cloud_execution_fails_closed_without_consumption(monkeypatch):
    admission = setup(monkeypatch, result="already_consumed")
    calls = []
    monkeypatch.setattr(app_module, "run_classification", lambda request: calls.append(request))
    assert client.post(route, json=envelope(), headers=headers).status_code == 503
    assert len(admission.calls) == 1
    assert not calls


def test_cloud_configured_worker_does_not_expose_unfenced_local_execution(monkeypatch):
    admission = setup(monkeypatch)
    payload = {"taskId": reservation, "input": {"text": "private fixture"}}
    assert client.post("/internal/v1/execute", json=payload, headers=headers).status_code == 503
    assert not admission.calls


def test_cloud_execution_rejects_invalid_inputs_before_consumption(monkeypatch):
    admission = setup(monkeypatch)
    for data in (
        {**envelope(), "key": "!" * 44},
        {**envelope(), "schemaVersion": "other"},
        envelope(b'{"taskId":"one","taskId":"two","input":{"text":"x"}}'),
        envelope(b'{"taskId":"one","input":{"inputRef":"opaque"}}'),
        envelope(json.dumps({"taskId": reservation, "input": {"text": "x", "labelSetVersion": "unknown"}}).encode()),
    ):
        assert client.post(route, json=data, headers=headers).status_code == 503
    assert client.post(route, content=b"{}", headers={**headers, "content-type": "text/plain"}).status_code == 415
    assert client.post(route, content=b"x" * 180001, headers={**headers, "content-type": "application/json"}).status_code == 413
    assert not admission.calls


def test_cloud_execution_is_disabled_without_client(monkeypatch):
    setup(monkeypatch)
    monkeypatch.setattr(app_module, "configured_client", lambda: None)
    assert client.post(route, json=envelope(), headers=headers).status_code == 503


def test_cloud_client_is_opt_in_and_rejects_incomplete_configuration(monkeypatch):
    cloud_execution.configured_client.cache_clear()
    monkeypatch.delenv("OPENCONNECT_CLOUD_CONSUMPTION_ENABLED", raising=False)
    assert cloud_execution.configured_client() is None
    cloud_execution.configured_client.cache_clear()
    monkeypatch.setenv("OPENCONNECT_CLOUD_CONSUMPTION_ENABLED", "true")
    for name in ("OPENCONNECT_CLOUD_AUTHORITY_ORIGIN", "OPENCONNECT_CLOUD_AUTHORITY_CA_FILE",
                 "OPENCONNECT_CLOUD_WORKER_CERT_FILE", "OPENCONNECT_CLOUD_WORKER_KEY_FILE"):
        monkeypatch.delenv(name, raising=False)
    with pytest.raises(ValueError):
        cloud_execution.configured_client()
    cloud_execution.configured_client.cache_clear()
