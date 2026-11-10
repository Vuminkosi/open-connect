import openconnect_nllb.app as app_module
from openconnect_nllb.app import app
from openconnect_nllb.config import Settings
from openconnect_nllb.languages import SUPPORTED_LANGUAGES, is_supported_pair

from fastapi.testclient import TestClient

client = TestClient(app)


def _auth(monkeypatch, **overrides) -> None:
    kwargs = dict(
        worker_token="local-test-token",
        model_dir=app_module.settings.model_dir,
        fake_engine=True,
        max_text_chars=5000,
        license_accepted=False,
    )
    kwargs.update(overrides)
    monkeypatch.setattr(app_module, "settings", Settings(**kwargs))  # type: ignore[arg-type]


def test_live_and_ready_fake_engine(monkeypatch) -> None:
    _auth(monkeypatch)
    assert client.get("/health/live").json() == {"status": "live"}
    body = client.get("/health/ready").json()
    assert body["status"] == "ready" and body["ready"] is True
    assert body["engine"] == "fake"
    assert "eng_Latn" in body["supportedLanguages"]


def test_ready_unready_when_model_missing_and_real(monkeypatch, tmp_path) -> None:
    _auth(monkeypatch, model_dir=tmp_path, fake_engine=False)
    body = client.get("/health/ready").json()
    assert body["status"] == "unready" and body["ready"] is False
    assert body["reason"] in ("model_unavailable", "tokenizer_mismatch", "license_denied")


def test_auth_required(monkeypatch) -> None:
    _auth(monkeypatch)
    assert client.get("/internal/v1/capabilities").status_code == 401
    payload = {
        "schemaVersion": "openconnect.v1",
        "taskId": "t-1",
        "input": {"text": "hello", "sourceLang": "eng_Latn", "targetLang": "fra_Latn"},
    }
    response = client.post("/internal/v1/execute", json=payload)
    assert response.status_code == 401
    bad = client.post(
        "/internal/v1/execute", json=payload, headers={"Authorization": "Bearer wrong"}
    )
    assert bad.status_code == 401


def test_capabilities_lists_pairs_and_no_tools(monkeypatch) -> None:
    _auth(monkeypatch)
    response = client.get(
        "/internal/v1/capabilities", headers={"Authorization": "Bearer local-test-token"}
    )
    assert response.status_code == 200
    body = response.json()
    assert body["workerKind"] == "nllb"
    assert "eng_Latn->fra_Latn" in body["capabilities"][0]["languagePairs"]
    assert body["capabilities"][0]["grantsTools"] is False


def _execute(text, source="eng_Latn", target="fra_Latn"):
    payload = {
        "schemaVersion": "openconnect.v1",
        "taskId": "t-1",
        "input": {"text": text, "sourceLang": source, "targetLang": target},
    }
    return client.post(
        "/internal/v1/execute", json=payload, headers={"Authorization": "Bearer local-test-token"}
    )


def test_execute_fake_engine_happy_path(monkeypatch) -> None:
    _auth(monkeypatch)
    response = _execute("hello world")
    assert response.status_code == 200
    body = response.json()
    assert body["status"] == "completed"
    result = body["result"]
    assert result["sourceLang"] == "eng_Latn" and result["targetLang"] == "fra_Latn"
    assert result["engine"] == "fake"
    assert result["engineWarning"] is not None
    assert result["translatedText"].startswith("[fra_Latn fake]")


def test_execute_language_unsupported(monkeypatch) -> None:
    _auth(monkeypatch)
    response = _execute("hello", source="eng_Latn", target="xx_Yyyy")
    assert response.status_code == 422
    assert response.json()["error"]["code"] == "language_unsupported"


def test_execute_identical_languages_rejected(monkeypatch) -> None:
    _auth(monkeypatch)
    response = _execute("hello", source="eng_Latn", target="eng_Latn")
    assert response.status_code == 422
    assert response.json()["error"]["code"] == "language_unsupported"


def test_execute_input_too_long(monkeypatch) -> None:
    _auth(monkeypatch, max_text_chars=4)
    response = _execute("0123456789")
    assert response.status_code == 413
    assert response.json()["error"]["code"] == "input_too_long"


def test_execute_model_unavailable_real_mode(monkeypatch, tmp_path) -> None:
    _auth(monkeypatch, model_dir=tmp_path, fake_engine=False)
    response = _execute("hello")
    assert response.status_code == 503
    assert response.json()["error"]["code"] == "model_unavailable"


def test_execute_input_ref_unresolved(monkeypatch) -> None:
    _auth(monkeypatch)
    payload = {
        "schemaVersion": "openconnect.v1",
        "taskId": "t-9",
        "input": {
            "inputRef": "opaque://payload/abc",
            "sourceLang": "eng_Latn",
            "targetLang": "fra_Latn",
        },
    }
    response = client.post(
        "/internal/v1/execute", json=payload, headers={"Authorization": "Bearer local-test-token"}
    )
    assert response.json()["error"]["code"] == "input_ref_unresolved"


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


def test_supported_pair_table() -> None:
    assert "eng_Latn" in SUPPORTED_LANGUAGES
    assert is_supported_pair("eng_Latn", "fra_Latn")
    assert not is_supported_pair("eng_Latn", "xx_Yyyy")
    assert not is_supported_pair("eng_Latn", "eng_Latn")
