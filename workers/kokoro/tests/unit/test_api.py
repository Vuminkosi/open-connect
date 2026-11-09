import base64

from fastapi.testclient import TestClient

from openconnect_kokoro import app as app_module
from openconnect_kokoro.app import app
from openconnect_kokoro.config import Settings

client = TestClient(app)
AUTH = {"Authorization": "Bearer local-test-token"}


def _use_auth(monkeypatch, **overrides) -> None:
    base = {
        "worker_token": "local-test-token",
        "model_dir": "/models",
        "require_model": False,
        "voices": ("af_heart",),
        "default_voice": "af_heart",
    }
    base.update(overrides)
    monkeypatch.setattr(app_module, "settings", Settings(**base))


def _synth(**payload) -> object:
    body = {"schemaVersion": "openconnect.v1", "text": "hello world"}
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


def test_live_and_ready_distinct(monkeypatch) -> None:
    _use_auth(monkeypatch)
    assert client.get("/health/live").json() == {"status": "live"}
    body = client.get("/health/ready").json()
    assert body["status"] == "ready"
    assert body["defaultVoice"] == "af_heart"


def test_capabilities_requires_auth_and_advertises_verified_voices(monkeypatch) -> None:
    _use_auth(monkeypatch)
    assert client.get("/internal/v1/capabilities").status_code == 401
    response = client.get("/internal/v1/capabilities", headers=AUTH)
    assert response.status_code == 200
    body = response.json()
    assert body["voices"] == ["af_heart"]
    assert body["nativeSampleRateHz"] == 24000
    assert body["nativeChannels"] == 1


def test_synthesize_defaults_to_verified_af_heart_with_native_metadata(
    monkeypatch,
) -> None:
    _use_auth(monkeypatch)
    response = _synth()
    assert response.status_code == 200
    result = response.json()["result"]
    assert result["voiceId"] == "af_heart"
    assert result["sampleRateHz"] == 24000
    assert result["channels"] == 1
    assert result["format"] == "wav"
    assert base64.b64decode(result["audioBase64"])


def test_unknown_voice_rejected_and_idempotency_replay(monkeypatch) -> None:
    _use_auth(monkeypatch)
    assert _synth(voiceId="xx_unknown!").status_code == 422

    first = client.post(
        "/internal/v1/execute",
        json={
            "schemaVersion": "openconnect.v1",
            "taskId": "t-1",
            "kind": "speech.synthesize",
            "payload": {
                "schemaVersion": "openconnect.v1",
                "text": "repeat me",
                "idempotencyKey": "key-1",
            },
        },
        headers=AUTH,
    )
    assert first.status_code == 200
    assert first.json()["idempotentReplay"] is False

    second = client.post(
        "/internal/v1/execute",
        json={
            "schemaVersion": "openconnect.v1",
            "taskId": "t-2",
            "kind": "speech.synthesize",
            "payload": {
                "schemaVersion": "openconnect.v1",
                "text": "repeat me",
                "idempotencyKey": "key-1",
            },
        },
        headers=AUTH,
    )
    assert second.json()["idempotentReplay"] is True
    assert second.json()["result"] == first.json()["result"]

    conflict = client.post(
        "/internal/v1/execute",
        json={
            "schemaVersion": "openconnect.v1",
            "taskId": "t-3",
            "kind": "speech.synthesize",
            "payload": {
                "schemaVersion": "openconnect.v1",
                "text": "different",
                "idempotencyKey": "key-1",
            },
        },
        headers=AUTH,
    )
    assert conflict.status_code == 409


def test_require_model_fails_closed_without_download(monkeypatch, tmp_path) -> None:
    _use_auth(monkeypatch, require_model=True, model_dir=str(tmp_path))
    response = _synth()
    assert response.status_code == 503
    # Staging the marker (init container) restores synthesis; no download involved.
    (tmp_path / "kokoro-init.json").write_text('{"worker": "kokoro"}\n')
    assert _synth().status_code == 200


def test_task_lifecycle_claim_heartbeat_complete_fail(monkeypatch) -> None:
    _use_auth(monkeypatch)
    claim = client.post(
        "/internal/v1/tasks/claim",
        json={"taskId": "task-1", "kind": "speech.synthesize"},
        headers=AUTH,
    )
    assert claim.status_code == 200
    lease = {
        "leaseId": claim.json()["leaseId"],
        "fencingToken": claim.json()["fencingToken"],
    }
    assert (
        client.post("/internal/v1/tasks/task-1/heartbeat", json=lease, headers=AUTH).status_code
        == 200
    )
    done = client.post(
        "/internal/v1/tasks/task-1/complete",
        json=dict(lease, result={"ok": True}),
        headers=AUTH,
    )
    assert done.json()["state"] == "completed"
    assert client.get("/internal/v1/tasks/missing", headers=AUTH).status_code == 404
