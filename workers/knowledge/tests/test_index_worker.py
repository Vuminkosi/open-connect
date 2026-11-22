import threading

import pytest

from openconnect_knowledge import index_worker


def test_worker_is_disabled_by_default(monkeypatch):
    monkeypatch.delenv("OC_INDEX_WORKER_ENABLED", raising=False)
    with pytest.raises(ValueError, match="index_worker_disabled"):
        index_worker.run(threading.Event())


def test_initial_readiness_retries_without_advertising_until_dependencies_recover():
    events = []

    class Stop:
        def is_set(self):
            return False

        def wait(self, seconds):
            events.append(("wait", seconds))

    def heartbeat():
        events.append("probe")
        if events.count("probe") == 1:
            raise RuntimeError("private dependency error")
        events.append("ready")

    assert index_worker._wait_for_initial_readiness(Stop(), heartbeat)
    assert events == ["probe", ("wait", 5), "probe", "ready"]


def test_initial_readiness_can_be_cancelled_during_retry():
    stop = threading.Event()

    def wait(seconds):
        assert seconds == 5
        stop.set()

    stop.wait = wait

    def heartbeat():
        raise RuntimeError("unavailable")

    assert not index_worker._wait_for_initial_readiness(stop, heartbeat)


def test_failed_refresh_stops_claims_and_revokes_lease_then_recovers():
    ready = threading.Event()
    ready.set()
    events = []

    def failed_probe():
        raise RuntimeError("private dependency error")

    def retire():
        assert not ready.is_set()
        events.append("retired")

    index_worker._refresh_readiness(ready, failed_probe, retire)
    assert not ready.is_set() and events == ["retired"]
    index_worker._refresh_readiness(ready, lambda: events.append("refreshed"), retire)
    assert ready.is_set() and events == ["retired", "refreshed"]


def test_failed_lease_revocation_keeps_claims_suspended():
    ready = threading.Event()
    ready.set()

    def fail():
        raise RuntimeError("unavailable")

    index_worker._refresh_readiness(ready, fail, fail)
    assert not ready.is_set()


@pytest.mark.parametrize("mode", ["completed", "idle", "tenant_failure", "rotation"])
def test_worker_processes_queued_tenant_with_required_dependencies(monkeypatch, tmp_path, mode):
    values = {"OC_INDEX_WORKER_ENABLED": "true", "OC_INDEX_MINIO_TLS": "false",
              "OC_INDEX_MINIO_ENDPOINT": "minio:9000", "OC_INDEX_MINIO_ACCESS_KEY": "test-access",
              "OC_INDEX_MINIO_SECRET_KEY": "test-secret", "OC_INDEX_MINIO_BUCKET": "test-bucket",
              "OC_KNOWLEDGE_SEARCH_DSN": "synthetic-dsn", "OC_INDEX_CLAM_SOCKET": str(tmp_path / "clam.sock"),
              "OC_INDEX_DOCUMENT_SOCKET": str(tmp_path / "worker.sock"), "OPENCONNECT_WORKER_TOKEN": "test-token",
              "OPENCONNECT_WORKER_SIGNING_KEY": "ab" * 32}
    for name, value in values.items():
        monkeypatch.setenv(name, value)
    events = []

    class Stop(threading.Event):
        def wait(self, timeout=None):
            if timeout in (2, 5):
                events.append(f"wait {timeout}")
                if mode != "rotation":
                    self.set()
                    return True
                return False
            return super().wait(timeout)

    stop = Stop()

    class Index:
        def __init__(self, dsn):
            assert dsn == "synthetic-dsn"

        def _verify(self):
            events.append("index verified")

    class Jobs:
        def __init__(self, dsn):
            assert dsn == "synthetic-dsn"

        def ready_tenants(self, *, limit=64, embedding_space_id=None, after=None):
            assert embedding_space_id == (None if limit == 1 else "a" * 64)
            if mode == "rotation" and limit != 1:
                events.append(f"page {after}")
                return ("failed",) if after is None else ("tenant",)
            return () if limit == 1 else (("failed", "tenant") if mode == "tenant_failure" else ("tenant",))

        def record_worker(self, worker_id, space):
            assert space == "a" * 64
            events.append("worker ready")

        def retire_worker(self, worker_id):
            events.append("worker retired")

    class Scanner:
        def __init__(self, path):
            assert path == tmp_path / "clam.sock"

        def scan(self, content, *, timeout_seconds):
            assert content == b"OpenConnect readiness probe" and timeout_seconds == 2
            events.append("scanner verified")

    class Pool:
        def __init__(self, **kwargs):
            pass

        def clear(self):
            events.append("pool closed")

    class Parser:
        def ready(self):
            return True

    class Engine:
        def embed(self, texts, *, input_kind):
            return type("Result", (), {"space_id": "a" * 64})()

        def _verify(self):
            pass

    class Client:
        def bucket_exists(self, bucket):
            return True

        def get_bucket_versioning(self, bucket):
            return type("Versioning", (), {"status": "Enabled"})()

    monkeypatch.setattr(index_worker, "PostgresKnowledgeIndex", Index)
    monkeypatch.setattr(index_worker, "PostgresIndexJobs", Jobs)
    monkeypatch.setattr(index_worker, "ClamAvScanner", Scanner)
    parser, engine, client = Parser(), Engine(), Client()
    monkeypatch.setattr(index_worker, "DocumentOcrParser", lambda *args: parser)
    monkeypatch.setattr(index_worker, "_load_engine", lambda: engine)
    monkeypatch.setattr(index_worker, "storage_pool", lambda **kwargs: Pool())
    monkeypatch.setattr(index_worker, "Minio", lambda *args, **kwargs: client)
    monkeypatch.setattr(index_worker, "MinioResolver", lambda *args: "resolver")

    def process(jobs, index, tenant, **kwargs):
        if tenant == "failed":
            events.append("tenant failure")
            raise RuntimeError("private error")
        assert tenant == "tenant" and kwargs["lease_seconds"] == 300
        assert kwargs["embedding_space_id"] == "a" * 64
        assert kwargs["parser"] is parser and kwargs["embedder"] is engine
        events.append("processed")
        if mode == "idle":
            return index_worker.IndexStatus.IDLE
        if mode in ("completed", "rotation"):
            stop.set()
        return index_worker.IndexStatus.COMPLETED

    monkeypatch.setattr(index_worker, "process_one", process)
    index_worker.run(stop)
    if mode == "rotation":
        assert events == ["index verified", "scanner verified", "worker ready",
                          "page None", "tenant failure", "wait 5", "page failed", "processed",
                          "worker retired", "pool closed"]
        return
    assert events == (["index verified", "scanner verified", "worker ready"]
                      + (["tenant failure"] if mode == "tenant_failure" else []) + ["processed"]
                      + (["wait 2"] if mode == "idle" else ["wait 5"] if mode == "tenant_failure" else [])
                      + ["worker retired", "pool closed"])


@pytest.mark.parametrize("endpoint", ["https://host", "user@host", "host/path"])
def test_worker_rejects_untrusted_endpoint_format(monkeypatch, endpoint):
    monkeypatch.setenv("OC_INDEX_WORKER_ENABLED", "true")
    monkeypatch.setenv("OC_INDEX_MINIO_ENDPOINT", endpoint)
    with pytest.raises(ValueError, match="invalid_index_configuration"):
        index_worker.run(threading.Event())
