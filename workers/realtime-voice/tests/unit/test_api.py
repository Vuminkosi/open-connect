from fastapi.testclient import TestClient
from jsonschema import Draft202012Validator

from openconnect_realtime_voice import app as app_module
from openconnect_realtime_voice.app import app
from openconnect_realtime_voice.config import Settings

import json
from pathlib import Path

client = TestClient(app)
ROOT = Path(__file__).resolve().parents[4]

SID = "123e4567-e89b-42d3-a456-426614174000"
AUTH = {"Authorization": "Bearer local-test-token"}


def _use_auth(monkeypatch) -> None:
    monkeypatch.setattr(app_module, "settings", Settings("local-test-token", True, "/models"))


def _open(session_id: str = SID) -> dict:
    return client.post(
        "/internal/v1/execute",
        json={
            "schemaVersion": "openconnect.v1",
            "taskId": "t-open",
            "kind": "voice.session-open",
            "payload": {"schemaVersion": "openconnect.v1", "sessionId": session_id},
        },
        headers=AUTH,
    ).json()


def _event(session_id: str, event: str, **extra) -> object:
    payload = {
        "schemaVersion": "openconnect.v1",
        "sessionId": session_id,
        "type": event,
    }
    payload.update(extra)
    return client.post(
        "/internal/v1/execute",
        json={
            "schemaVersion": "openconnect.v1",
            "taskId": f"t-{event}",
            "kind": "voice.event",
            "payload": payload,
        },
        headers=AUTH,
    )


def test_live_and_ready_orchestration_only(monkeypatch) -> None:
    _use_auth(monkeypatch)
    assert client.get("/health/live").json() == {"status": "live"}
    body = client.get("/health/ready").json()
    assert body["status"] == "ready"
    assert body["mode"] == "orchestration-only"
    assert "voice.session" in body["capabilities"]


def test_capabilities_requires_auth(monkeypatch) -> None:
    _use_auth(monkeypatch)
    assert client.get("/internal/v1/capabilities").status_code == 401
    response = client.get("/internal/v1/capabilities", headers=AUTH)
    assert response.status_code == 200
    assert response.json()["worker"] == "realtime-voice"


def test_full_session_flow_with_barge_in(monkeypatch) -> None:
    _use_auth(monkeypatch)
    opened = _open()
    assert opened["state"] == "idle"

    assert _event(SID, "begin-listening").status_code == 200
    assert _event(SID, "transcript-partial", text="hello").status_code == 200
    assert _event(SID, "transcript-final", text="hello world").status_code == 200
    assert _event(SID, "begin-reasoning").status_code == 200

    speaking = _event(
        SID, "begin-speaking", speechEpoch=0, text="hi there", voice="v", language="en"
    )
    assert speaking.status_code == 200
    chunks = speaking.json()["result"]["chunks"]
    assert chunks and chunks[-1]["isFinal"] is True
    assert [c["sequence"] for c in chunks] == list(range(len(chunks)))
    assert speaking.json()["result"]["speechEpoch"] == 0

    barged = _event(SID, "barge-in")
    assert barged.status_code == 200
    assert barged.json()["result"]["speechEpoch"] == 1
    assert barged.json()["result"]["state"] == "interrupted"

    # Stale epoch work is discarded, never applied.
    stale = _event(SID, "utterance-complete", speechEpoch=0)
    assert stale.status_code == 409

    assert _event(SID, "resume-listening").status_code == 200
    state = client.get(f"/internal/v1/voice/sessions/{SID}", headers=AUTH).json()
    assert state["state"] == "listening"
    assert state["speechEpoch"] == 1


def test_invalid_transition_rejected_and_terminal(monkeypatch) -> None:
    _use_auth(monkeypatch)
    other = "223e4567-e89b-42d3-a456-426614174000"
    _open(other)
    assert _event(other, "begin-speaking", speechEpoch=0).status_code == 409
    assert _event(other, "cancel").status_code == 200
    assert _event(other, "begin-listening").status_code == 409


def test_chunks_match_speech_chunk_contract(monkeypatch) -> None:
    _use_auth(monkeypatch)
    schema = json.loads((ROOT / "contracts/workers/v1/speech-chunk.json").read_text())
    validator = Draft202012Validator(schema)
    sid = "323e4567-e89b-42d3-a456-426614174000"
    _open(sid)
    _event(sid, "begin-listening")
    _event(sid, "transcript-final", text="go")
    _event(sid, "begin-reasoning")
    speaking = _event(
        sid,
        "begin-speaking",
        speechEpoch=0,
        text="hello there friend",
        voice="v",
        language="en",
    )
    assert speaking.status_code == 200
    chunks = speaking.json()["result"]["chunks"]
    assert chunks
    for chunk in chunks:
        validator.validate(chunk)


def test_task_claim_heartbeat_complete_fail(monkeypatch) -> None:
    _use_auth(monkeypatch)
    claim = client.post(
        "/internal/v1/tasks/claim",
        json={"taskId": "task-1", "kind": "voice.session-open"},
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

    stale = dict(lease, fencingToken=lease["fencingToken"] + 99)
    assert (
        client.post("/internal/v1/tasks/task-1/heartbeat", json=stale, headers=AUTH).status_code
        == 409
    )

    done = client.post(
        "/internal/v1/tasks/task-1/complete",
        json=dict(lease, result={"ok": True}),
        headers=AUTH,
    )
    assert done.status_code == 200
    assert client.get("/internal/v1/tasks/task-1", headers=AUTH).json()["state"] == "completed"

    claim2 = client.post(
        "/internal/v1/tasks/claim",
        json={"taskId": "task-2", "kind": "voice.event"},
        headers=AUTH,
    )
    lease2 = {
        "leaseId": claim2.json()["leaseId"],
        "fencingToken": claim2.json()["fencingToken"],
    }
    failed = client.post(
        "/internal/v1/tasks/task-2/fail",
        json=dict(lease2, error={"code": "engine_error", "message": "boom"}),
        headers=AUTH,
    )
    assert failed.status_code == 200
    assert failed.json()["state"] == "failed"

    assert client.get("/internal/v1/tasks/missing", headers=AUTH).status_code == 404
    assert (
        client.post(
            "/internal/v1/tasks/claim",
            json={"taskId": "task-3", "kind": "nope.unknown"},
            headers=AUTH,
        ).status_code
        == 409
    )
