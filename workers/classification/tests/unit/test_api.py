import openconnect_classification.app as app_module
from openconnect_classification.app import app
from openconnect_classification.config import Settings
from openconnect_classification.label_sets import get_label_set, label_set_versions

from fastapi.testclient import TestClient

client = TestClient(app)


def _auth(monkeypatch) -> None:
    monkeypatch.setattr(
        app_module,
        "settings",
        Settings(
            worker_token="local-test-token",
            model_dir=app_module.settings.model_dir,
            fake_engine=True,
            label_set_version="v1",
            max_text_chars=20000,
            license_accepted=False,
        ),
    )


def test_live_and_ready_fake_engine(monkeypatch) -> None:
    _auth(monkeypatch)
    assert client.get("/health/live").json() == {"status": "live"}
    body = client.get("/health/ready").json()
    assert body["status"] == "ready" and body["ready"] is True
    assert body["engine"] == "fake"


def test_ready_unready_when_model_missing_and_real(monkeypatch, tmp_path) -> None:
    monkeypatch.setattr(
        app_module,
        "settings",
        Settings(
            worker_token="t",
            model_dir=tmp_path,
            fake_engine=False,
            label_set_version="v1",
            max_text_chars=20000,
            license_accepted=False,
        ),
    )
    body = client.get("/health/ready").json()
    assert body == {"status": "unready", "ready": False, "reason": "model_unavailable"}


def test_auth_required(monkeypatch) -> None:
    _auth(monkeypatch)
    assert client.get("/internal/v1/capabilities").status_code == 401
    payload = {
        "schemaVersion": "openconnect.v1",
        "taskId": "t-1",
        "input": {"text": "hello", "labelSetVersion": "v1"},
    }
    assert client.post("/internal/v1/execute", json=payload).status_code == 401
    assert client.post("/internal/v1/claim", json={"taskId": "t-1"}).status_code == 401


def test_capabilities_never_grant_tools(monkeypatch) -> None:
    _auth(monkeypatch)
    response = client.get(
        "/internal/v1/capabilities", headers={"Authorization": "Bearer local-test-token"}
    )
    assert response.status_code == 200
    body = response.json()
    assert body["workerKind"] == "classification"
    assert body["capabilities"][0]["grantsTools"] is False


def _execute(text, **kwargs):
    payload = {
        "schemaVersion": "openconnect.v1",
        "taskId": "t-1",
        "input": {"text": text, "labelSetVersion": "v1", **kwargs},
    }
    return client.post(
        "/internal/v1/execute", json=payload, headers={"Authorization": "Bearer local-test-token"}
    )


def test_execute_fake_engine_happy_path(monkeypatch) -> None:
    _auth(monkeypatch)
    response = _execute("hello world, a perfectly safe message")
    assert response.status_code == 200
    body = response.json()
    assert body["status"] == "completed"
    result = body["result"]
    assert result["modelRevision"] == "fake-0.1.0"
    assert result["engine"] == "fake"
    assert result["grantsTools"] is False
    assert result["engineWarning"] is not None


def test_execute_unknown_label_set(monkeypatch) -> None:
    _auth(monkeypatch)
    response = _execute("hello", labelSetVersion="v99")
    assert response.status_code == 422
    assert response.json()["error"]["code"] == "label_set_unknown"


def test_execute_input_too_long(monkeypatch) -> None:
    monkeypatch.setattr(
        app_module,
        "settings",
        Settings(
            worker_token="local-test-token",
            model_dir=app_module.settings.model_dir,
            fake_engine=True,
            label_set_version="v1",
            max_text_chars=8,
            license_accepted=False,
        ),
    )
    response = _execute("0123456789abcdef")
    assert response.status_code == 413
    assert response.json()["error"]["code"] == "input_too_long"


def test_execute_input_ref_unresolved(monkeypatch) -> None:
    _auth(monkeypatch)
    payload = {
        "schemaVersion": "openconnect.v1",
        "taskId": "t-9",
        "input": {"inputRef": "opaque://payload/abc", "labelSetVersion": "v1"},
    }
    response = client.post(
        "/internal/v1/execute", json=payload, headers={"Authorization": "Bearer local-test-token"}
    )
    assert response.status_code == 200
    assert response.json()["error"]["code"] == "input_ref_unresolved"


def test_execute_model_unavailable_real_mode(monkeypatch, tmp_path) -> None:
    monkeypatch.setattr(
        app_module,
        "settings",
        Settings(
            worker_token="local-test-token",
            model_dir=tmp_path,
            fake_engine=False,
            label_set_version="v1",
            max_text_chars=20000,
            license_accepted=False,
        ),
    )
    response = _execute("hello world")
    assert response.status_code == 503
    assert response.json()["error"]["code"] == "model_unavailable"


def test_claim_heartbeat_complete_flow(monkeypatch) -> None:
    _auth(monkeypatch)
    headers = {"Authorization": "Bearer local-test-token"}
    claim = client.post("/internal/v1/claim", json={"taskId": "flow-1"}, headers=headers).json()
    assert claim["taskId"] == "flow-1" and claim["leaseId"]
    hb = client.post(
        "/internal/v1/heartbeat",
        json={"taskId": "flow-1", "leaseId": claim["leaseId"]},
        headers=headers,
    )
    assert hb.json()["renewed"] is True
    done = client.post(
        "/internal/v1/complete",
        json={"taskId": "flow-1", "leaseId": claim["leaseId"]},
        headers=headers,
    )
    assert done.json()["committed"] is True


def test_expired_lease_cannot_commit(monkeypatch) -> None:
    _auth(monkeypatch)
    headers = {"Authorization": "Bearer local-test-token"}
    claim = client.post("/internal/v1/claim", json={"taskId": "stale-1"}, headers=headers).json()
    app_module._leases["stale-1"]["expiresAtEpoch"] = 0.0
    response = client.post(
        "/internal/v1/complete",
        json={"taskId": "stale-1", "leaseId": claim["leaseId"]},
        headers=headers,
    )
    assert response.status_code == 409


def test_validation_rejects_empty_and_dual_input(monkeypatch) -> None:
    _auth(monkeypatch)
    headers = {"Authorization": "Bearer local-test-token"}
    empty = {"schemaVersion": "openconnect.v1", "taskId": "t-1", "input": {"labelSetVersion": "v1"}}
    assert client.post("/internal/v1/execute", json=empty, headers=headers).status_code == 422
    dual = {
        "schemaVersion": "openconnect.v1",
        "taskId": "t-1",
        "input": {"text": "a", "inputRef": "opaque://x", "labelSetVersion": "v1"},
    }
    assert client.post("/internal/v1/execute", json=dual, headers=headers).status_code == 422


def test_label_sets_versioned() -> None:
    assert "v1" in label_set_versions()
    assert set(get_label_set("v1")) == {"safe", "spam", "toxic", "sensitive"}
    try:
        get_label_set("nope")
    except KeyError:
        pass
    else:  # pragma: no cover
        raise AssertionError("expected KeyError")
