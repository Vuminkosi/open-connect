from fastapi.testclient import TestClient

from openconnect_knowledge import app as module


def test_image_capability_recovers_after_database_startup(monkeypatch):
    monkeypatch.setenv("OPENCONNECT_WORKER_TOKEN", "test-token")
    monkeypatch.setenv("OPENCONNECT_WORKER_SIGNING_KEY", "ab" * 32)
    monkeypatch.setenv("OC_KNOWLEDGE_IMAGE_ENABLED", "true")
    monkeypatch.setenv("OC_KNOWLEDGE_SEARCH_DSN", "synthetic-dsn")
    monkeypatch.setattr(module, "_load_engine", lambda: None)
    monkeypatch.setattr(module, "_load_reranker", lambda: None)
    monkeypatch.setattr(module, "_load_index", lambda: None)
    monkeypatch.setattr(module, "_load_jobs", lambda: None)

    class Index:
        available = False

        def __init__(self, dsn):
            assert dsn == "synthetic-dsn"

        def _verify(self):
            if not self.available:
                raise RuntimeError("database unavailable")

    monkeypatch.setattr(module, "PostgresImageIndex", Index)
    with TestClient(module.app) as client:
        assert client.get("/health/ready").status_code == 503
        assert client.get("/internal/v1/capabilities", headers={"Authorization": "Bearer test-token"}).json() == {
            "capabilities": []}
        module.app.state.image_index.available = True
        assert client.get("/health/ready").status_code == 200
        caps = client.get("/internal/v1/capabilities", headers={"Authorization": "Bearer test-token"}).json()
        assert {item["id"] for item in caps["capabilities"]} == {"knowledge.image.upsert", "knowledge.image.search"}
        module.app.state.image_index.available = False
        assert client.get("/health/ready").status_code == 503


def test_delete_only_readiness_reflects_database_health(monkeypatch):
    monkeypatch.setenv("OPENCONNECT_WORKER_TOKEN", "test-token")
    monkeypatch.setenv("OPENCONNECT_WORKER_SIGNING_KEY", "ab" * 32)
    monkeypatch.setenv("OC_INDEX_DELETE_ENABLED", "true")
    monkeypatch.setenv("OC_KNOWLEDGE_SEARCH_DSN", "synthetic-dsn")
    monkeypatch.setattr(module, "_load_engine", lambda: None)
    monkeypatch.setattr(module, "_load_reranker", lambda: None)
    monkeypatch.setattr(module, "_load_jobs", lambda: None)
    monkeypatch.setattr(module, "_load_image_index", lambda: None)

    class Index:
        available = False

        def __init__(self, dsn):
            assert dsn == "synthetic-dsn"

        def _verify(self):
            if not self.available:
                raise RuntimeError("database unavailable")

    monkeypatch.setattr(module, "PostgresKnowledgeIndex", Index)
    with TestClient(module.app) as client:
        assert client.get("/health/ready").status_code == 503
        module.app.state.index.available = True
        assert client.get("/health/ready").status_code == 200
        caps = client.get("/internal/v1/capabilities", headers={"Authorization": "Bearer test-token"}).json()
        assert [item["id"] for item in caps["capabilities"]] == ["knowledge.index.delete"]
