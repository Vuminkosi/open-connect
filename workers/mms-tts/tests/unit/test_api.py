import base64

from fastapi.testclient import TestClient

from openconnect_mms_tts import app as app_module
from openconnect_mms_tts.app import app
from openconnect_mms_tts.config import Settings
from openconnect_mms_tts.engine import CheckpointCache

client = TestClient(app)
AUTH = {"Authorization": "Bearer local-test-token"}


def _use_auth(monkeypatch, **overrides) -> None:
    base = {
        "worker_token": "local-test-token",
        "model_dir": "/models",
        "languages": ("eng",),
        "commercial_use": False,
        "cache_size": 2,
    }
    base.update(overrides)
    monkeypatch.setattr(app_module, "settings", Settings(**base))


def _synth(language: str = "eng", text: str = "hello world") -> object:
    return client.post(
        "/internal/v1/execute",
        json={
            "schemaVersion": "openconnect.v1",
            "taskId": "t-synth",
            "kind": "mms.synthesize",
            "payload": {
                "schemaVersion": "openconnect.v1",
                "text": text,
                "language": language,
            },
        },
        headers=AUTH,
    )


def test_live_and_ready_reports_opt_in_only(monkeypatch) -> None:
    _use_auth(monkeypatch)
    assert client.get("/health/live").json() == {"status": "live"}
    body = client.get("/health/ready").json()
    assert body["status"] == "ready"
    assert body["optInLanguages"] == ["eng"]


def test_too_many_opt_in_languages_fails_closed(monkeypatch) -> None:
    _use_auth(monkeypatch, languages=("eng", "fra", "deu"))
    assert client.get("/health/ready").status_code == 503
    assert _synth().status_code == 503


def test_capabilities_requires_auth_and_lists_licenses(monkeypatch) -> None:
    _use_auth(monkeypatch)
    assert client.get("/internal/v1/capabilities").status_code == 401
    body = client.get("/internal/v1/capabilities", headers=AUTH).json()
    assert body["optInLanguages"] == [{"language": "eng", "license": "CC-BY-NC-4.0"}]


def test_synthesize_opted_in_language(monkeypatch) -> None:
    _use_auth(monkeypatch)
    response = _synth()
    assert response.status_code == 200
    result = response.json()["result"]
    assert result["language"] == "eng"
    assert base64.b64decode(result["audioBase64"])


def test_non_opted_in_language_rejected_without_catalog_pull(monkeypatch) -> None:
    _use_auth(monkeypatch)
    response = _synth(language="yor")
    assert response.status_code == 422
    assert "not opted in" in response.json()["detail"]


def test_nc_license_blocks_commercial_use(monkeypatch) -> None:
    _use_auth(monkeypatch, commercial_use=True)
    response = _synth()
    assert response.status_code == 403
    assert "commercial" in response.json()["detail"]


def test_lru_cache_evicts_oldest_in_ram_only() -> None:
    cache = CheckpointCache(max_entries=1)
    cache.load("eng", "CC-BY-NC-4.0")
    assert cache.cached_languages() == ["eng"]
    cache.load("fra", "CC-BY-NC-4.0")
    assert cache.cached_languages() == ["fra"]
    cache.load("fra", "CC-BY-NC-4.0")
    cache.load("eng", "CC-BY-NC-4.0")
    assert cache.cached_languages() == ["eng"]


def test_task_lifecycle_claim_heartbeat_complete_fail(monkeypatch) -> None:
    _use_auth(monkeypatch)
    claim = client.post(
        "/internal/v1/tasks/claim",
        json={"taskId": "task-1", "kind": "mms.synthesize"},
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
