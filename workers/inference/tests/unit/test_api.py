"""API tests for the inference worker (health, auth, execute, tasks)."""

import uuid

import pytest
from fastapi.testclient import TestClient

from openconnect_inference import app as app_module
from openconnect_inference.app import app
from openconnect_inference.config import Settings

client = TestClient(app)

REQUEST_ID = "123e4567-e89b-42d3-a456-426614174000"


@pytest.fixture(autouse=True)
def _configured(monkeypatch) -> None:
    monkeypatch.setattr(
        app_module,
        "settings",
        Settings(
            worker_token="local-test-token",
            backend="python-transformers",
            model_id=None,
            model_revision=None,
            model_dir="/models",
            endpoint_url=None,
            enable_tools=False,
            enable_structured_output=False,
            enable_embeddings=False,
            max_messages=64,
            max_input_chars=32_768,
            max_output_tokens=1_024,
            max_tasks=1_000,
            default_lease_ms=30_000,
        ),
    )
    app_module._tasks.clear()
    app_module._idempotency.clear()


def _auth() -> dict[str, str]:
    return {"Authorization": "Bearer local-test-token"}


def _execute(body_extra: dict | None = None) -> dict:
    body = {
        "schemaVersion": "openconnect.v1",
        "requestId": REQUEST_ID,
        "input": {"messages": [{"role": "user", "content": "hello"}]},
    }
    if body_extra:
        body["input"].update(body_extra)
    return body


def test_live_and_ready_are_distinct() -> None:
    assert client.get("/health/live").json() == {"status": "live"}
    body = client.get("/health/ready").json()
    assert body["status"] == "ready"
    assert body["capabilities"] == ["chat", "stream"]


def test_protected_routes_require_auth() -> None:
    assert client.get("/internal/v1/capabilities").status_code == 401
    assert client.post("/internal/v1/execute", json=_execute()).status_code == 401
    assert client.post("/internal/v1/tasks/claim", json={}).status_code == 401
    bad = {"Authorization": "Bearer wrong"}
    assert client.get("/internal/v1/capabilities", headers=bad).status_code == 401


def test_missing_token_fails_closed() -> None:
    app_module.settings = Settings(
        worker_token=None,
        backend="python-transformers",
        model_id=None,
        model_revision=None,
        model_dir="/models",
        endpoint_url=None,
        enable_tools=False,
        enable_structured_output=False,
        enable_embeddings=False,
        max_messages=64,
        max_input_chars=32_768,
        max_output_tokens=1_024,
        max_tasks=1_000,
        default_lease_ms=30_000,
    )
    response = client.get("/internal/v1/capabilities", headers=_auth())
    assert response.status_code == 503
    assert response.json()["detail"] == "worker unavailable"


def test_capabilities_truthful_by_default() -> None:
    response = client.get("/internal/v1/capabilities", headers=_auth())
    assert response.status_code == 200
    body = response.json()
    assert body["worker"] == "inference"
    assert body["engine"] == "fake-bounded"
    assert body["capabilities"] == ["chat", "stream"]
    assert body["modelLoaded"] is False


def test_execute_happy_path() -> None:
    response = client.post("/internal/v1/execute", json=_execute(), headers=_auth())
    assert response.status_code == 200
    body = response.json()
    assert body["requestId"] == REQUEST_ID
    assert "hello" in body["output"]
    assert body["usage"]["inputChars"] == 5
    assert body["capabilitiesUsed"] == ["chat"]
    assert body["chunks"] is None


def test_execute_stream_terminal_once() -> None:
    response = client.post("/internal/v1/execute", json=_execute({"stream": True}), headers=_auth())
    assert response.status_code == 200
    chunks = response.json()["chunks"]
    assert chunks is not None and len(chunks) >= 2
    assert [c["seq"] for c in chunks] == list(range(len(chunks)))
    assert sum(1 for c in chunks if c["done"]) == 1
    assert chunks[-1]["done"] is True
    assert "".join(c["delta"] for c in chunks) == response.json()["output"]


def test_execute_rejects_unadvertised_capability() -> None:
    body = _execute({"requestedCapabilities": ["tools"]})
    response = client.post("/internal/v1/execute", json=body, headers=_auth())
    assert response.status_code == 503
    assert response.json()["detail"] == "capability_unavailable"


def test_execute_rejects_oversized_input() -> None:
    body = _execute()
    body["input"]["messages"] = [{"role": "user", "content": "x" * 40_000}]
    response = client.post("/internal/v1/execute", json=body, headers=_auth())
    assert response.status_code in (400, 422)


def test_execute_idempotent() -> None:
    key = str(uuid.uuid4())
    first = client.post(
        "/internal/v1/execute",
        json={**_execute(), "idempotencyKey": key},
        headers=_auth(),
    )
    second = client.post(
        "/internal/v1/execute",
        json={**_execute(), "idempotencyKey": key},
        headers=_auth(),
    )
    assert first.status_code == 200
    assert first.json() == second.json()


def test_task_lifecycle_claim_heartbeat_complete() -> None:
    claim = client.post("/internal/v1/tasks/claim", json={}, headers=_auth())
    assert claim.status_code == 200
    task_id = claim.json()["taskId"]
    lease_id = claim.json()["leaseId"]

    heartbeat = client.post(
        f"/internal/v1/tasks/{task_id}/heartbeat",
        json={"leaseId": lease_id},
        headers=_auth(),
    )
    assert heartbeat.status_code == 200

    complete = client.post(
        f"/internal/v1/tasks/{task_id}/complete",
        json={"leaseId": lease_id, "resultSummary": "ok"},
        headers=_auth(),
    )
    assert complete.status_code == 200
    assert complete.json()["state"] == "completed"

    # Terminal-once: second completion conflicts.
    again = client.post(
        f"/internal/v1/tasks/{task_id}/complete",
        json={"leaseId": lease_id},
        headers=_auth(),
    )
    assert again.status_code == 409


def test_task_fail_and_lease_mismatch() -> None:
    claim = client.post("/internal/v1/tasks/claim", json={}, headers=_auth())
    task_id = claim.json()["taskId"]
    fail = client.post(
        f"/internal/v1/tasks/{task_id}/fail",
        json={"leaseId": claim.json()["leaseId"], "error": "boom"},
        headers=_auth(),
    )
    assert fail.status_code == 200
    assert fail.json()["state"] == "failed"

    other = client.post("/internal/v1/tasks/claim", json={}, headers=_auth()).json()
    wrong = client.post(
        f"/internal/v1/tasks/{other['taskId']}/heartbeat",
        json={"leaseId": str(uuid.uuid4())},
        headers=_auth(),
    )
    assert wrong.status_code == 409

    missing = client.post(
        f"/internal/v1/tasks/{uuid.uuid4()}/heartbeat",
        json={"leaseId": str(uuid.uuid4())},
        headers=_auth(),
    )
    assert missing.status_code == 404
    assert missing.json()["detail"] == "task_not_found"


def test_adapter_mode_without_endpoint_not_ready() -> None:
    app_module.settings = Settings(
        worker_token="local-test-token",
        backend="existing-local-endpoint",
        model_id=None,
        model_revision=None,
        model_dir="/models",
        endpoint_url=None,
        enable_tools=False,
        enable_structured_output=False,
        enable_embeddings=False,
        max_messages=64,
        max_input_chars=32_768,
        max_output_tokens=1_024,
        max_tasks=1_000,
        default_lease_ms=30_000,
    )
    assert client.get("/health/ready").status_code == 503
    response = client.post("/internal/v1/execute", json=_execute(), headers=_auth())
    assert response.status_code == 503
    assert response.json()["detail"] == "endpoint_unavailable"
