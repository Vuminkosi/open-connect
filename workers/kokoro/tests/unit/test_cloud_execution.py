import base64
import json
from uuid import uuid4

from fastapi.testclient import TestClient

import openconnect_kokoro.app as app_module
from openconnect_kokoro.cloud_execution import execute_cloud
from openconnect_kokoro.config import Settings


def _envelope():
    request_id = str(uuid4())
    payload = json.dumps({"schemaVersion": "openconnect.v1", "taskId": request_id,
                          "kind": "speech.synthesize", "payload": {"schemaVersion": "openconnect.v1",
                          "text": "hello", "voiceId": "af_heart", "locale": "en-US", "format": "pcm"}},
                         separators=(",", ":")).encode()
    envelope = {"schemaVersion": "openconnect.cloud.worker.voice-synthesis/v1", "reservationId": str(uuid4()),
                "claimId": str(uuid4()), "payload": base64.b64encode(payload).decode(),
                "key": base64.b64encode(bytes(32)).decode()}
    return envelope, payload


class Consumer:
    def __init__(self, expected):
        self.expected = expected
        self.calls = 0

    def consume(self, reservation, claim, payload, key):
        self.calls += 1
        if payload != self.expected or len(key) != 32:
            return "unavailable"
        return "consumed" if self.calls == 1 else "already_consumed"


def test_cloud_execution_validates_before_single_use_consumption():
    envelope, payload = _envelope()
    consumer = Consumer(payload)
    calls = []
    def invoke(request):
        calls.append(request.taskId)
        return {"ok": True}
    body = json.dumps(envelope).encode()
    assert execute_cloud(body, consumer, invoke) == {"ok": True}
    assert execute_cloud(body, consumer, invoke) is None
    assert len(calls) == 1
    envelope["key"] = base64.b64encode(bytes(31)).decode()
    assert execute_cloud(json.dumps(envelope).encode(), consumer, invoke) is None
    assert consumer.calls == 2
    assert execute_cloud(b'{"schemaVersion":"x","schemaVersion":"y"}', consumer, invoke) is None
    assert execute_cloud(body, None, invoke) is None


def test_cloud_route_requires_auth_and_disables_legacy_bypass(monkeypatch):
    envelope, payload = _envelope()
    consumer = Consumer(payload)
    monkeypatch.setattr(app_module, "settings", Settings(worker_token="fixture-token", model_dir="/models",
                        model_device="cpu", voices=("af_heart",), default_voice="af_heart"))
    monkeypatch.setattr(app_module, "configured_client", lambda: consumer)
    monkeypatch.setattr(app_module, "run_synthesis", lambda request: {"taskId": request.taskId})
    client = TestClient(app_module.app)
    route = "/internal/cloud/v1/voice-synthesis/execute"
    assert client.post(route, json=envelope).status_code == 401
    assert consumer.calls == 0
    headers = {"Authorization": "Bearer fixture-token"}
    assert client.post("/internal/v1/execute", json=json.loads(payload), headers=headers).status_code == 503
    assert client.post(route, json=envelope, headers=headers).status_code == 200
    assert client.post(route, json=envelope, headers=headers).status_code == 503
    assert consumer.calls == 2
    assert client.post(route, content=b"x", headers={**headers, "content-type": "application/json", "content-encoding": "gzip"}).status_code == 415
