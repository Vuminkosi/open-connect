from fastapi.testclient import TestClient

import openconnect_kokoro.app as app_module
from openconnect_kokoro.app import app
from openconnect_kokoro.config import Settings

client = TestClient(app)
AUTH = {"Authorization": "Bearer local-test-token"}


def _use_auth(monkeypatch) -> None:
    monkeypatch.setattr(
        app_module,
        "settings",
        Settings(
            worker_token="local-test-token",
            model_dir="/models",
            model_device="cpu",
            voices=("af_heart",),
            default_voice="af_heart",
        ),
    )


def _synth(**payload):
    body = {"schemaVersion": "openconnect.v1", "text": "hello world", "format": "pcm"}
    body.update(payload)
    return client.post(
        "/internal/v1/execute",
        json={
            "schemaVersion": "openconnect.v1",
            "taskId": "t-synth",
            "kind": "speech.synthesize",
            "payload": body,
        },
        headers=AUTH,
    )


def test_live_and_capabilities(monkeypatch) -> None:
    _use_auth(monkeypatch)
    assert client.get("/health/live").json() == {"status": "live"}
    response = client.get("/internal/v1/capabilities", headers=AUTH)
    assert response.status_code == 200
    body = response.json()
    assert body["engine"] == "kokoro"
    assert body["voices"] == ["af_heart"]
    assert body["formats"] == ["wav", "pcm"]


def test_capabilities_requires_auth(monkeypatch) -> None:
    _use_auth(monkeypatch)
    assert client.get("/internal/v1/capabilities").status_code == 401


def test_mp3_is_rejected_without_fabricated_audio(monkeypatch) -> None:
    _use_auth(monkeypatch)
    response = _synth(format="mp3")
    assert response.status_code == 422


def test_task_lifecycle(monkeypatch) -> None:
    _use_auth(monkeypatch)
    claim = client.post(
        "/internal/v1/tasks/claim",
        json={"taskId": "task-1", "kind": "speech.synthesize"},
        headers=AUTH,
    )
    assert claim.status_code == 200
    lease = {"leaseId": claim.json()["leaseId"], "fencingToken": claim.json()["fencingToken"]}
    assert client.post("/internal/v1/tasks/task-1/heartbeat", json=lease, headers=AUTH).status_code == 200
    done = client.post(
        "/internal/v1/tasks/task-1/complete",
        json=dict(lease, result={"ok": True}),
        headers=AUTH,
    )
    assert done.json()["state"] == "completed"
