import threading

import pytest

from openconnect_knowledge import expiration_worker


def test_expiry_is_disabled_by_default(monkeypatch):
    monkeypatch.delenv("OC_INDEX_EXPIRY_ENABLED", raising=False)
    with pytest.raises(ValueError, match="index_expiry_disabled"):
        expiration_worker.run(threading.Event())


def test_expiry_processes_tenant_without_model_or_storage(monkeypatch):
    monkeypatch.setenv("OC_INDEX_EXPIRY_ENABLED", "true")
    monkeypatch.setenv("OC_KNOWLEDGE_SEARCH_DSN", "synthetic-dsn")
    stop = threading.Event()
    events = []

    class Index:
        def __init__(self, dsn):
            assert dsn == "synthetic-dsn"

        def expiration_tenants(self, *, after):
            return ("tenant",)

        def expire_due(self, tenant):
            events.append(tenant)
            stop.set()
            return 1

    monkeypatch.setattr(expiration_worker, "PostgresKnowledgeIndex", Index)
    expiration_worker.run(stop)
    assert events == ["tenant"]


def test_locked_expiry_rows_wait_before_retry(monkeypatch):
    monkeypatch.setenv("OC_INDEX_EXPIRY_ENABLED", "true")
    monkeypatch.setenv("OC_KNOWLEDGE_SEARCH_DSN", "synthetic-dsn")

    class Stop:
        stopped = False

        def is_set(self):
            return self.stopped

        def wait(self, seconds):
            assert seconds == 2
            self.stopped = True

    class Index:
        def __init__(self, dsn):
            pass

        def expiration_tenants(self, *, after):
            return ("tenant",)

        def expire_due(self, tenant):
            return 0  # All eligible rows were locked by another worker.

    monkeypatch.setattr(expiration_worker, "PostgresKnowledgeIndex", Index)
    stop = Stop()
    expiration_worker.run(stop)
    assert stop.stopped


def test_expiry_retries_database_failure_without_logging_details(monkeypatch, capsys):
    monkeypatch.setenv("OC_INDEX_EXPIRY_ENABLED", "true")
    monkeypatch.setenv("OC_KNOWLEDGE_SEARCH_DSN", "synthetic-dsn")

    class Stop:
        stopped = False

        def is_set(self):
            return self.stopped

        def wait(self, seconds):
            assert seconds == 5
            self.stopped = True

    class Index:
        def __init__(self, dsn):
            pass

        def expiration_tenants(self, *, after):
            raise RuntimeError("sensitive-database-error")

    monkeypatch.setattr(expiration_worker, "PostgresKnowledgeIndex", Index)
    expiration_worker.run(Stop())
    assert "sensitive-database-error" not in capsys.readouterr().out


def test_tenant_failure_does_not_prevent_other_expirations(monkeypatch, capsys):
    monkeypatch.setenv("OC_INDEX_EXPIRY_ENABLED", "true")
    monkeypatch.setenv("OC_KNOWLEDGE_SEARCH_DSN", "synthetic-dsn")
    processed = []

    class Stop:
        stopped = False

        def is_set(self):
            return self.stopped

        def wait(self, seconds):
            assert seconds == 5
            self.stopped = True

    class Index:
        def __init__(self, dsn):
            pass

        def expiration_tenants(self, *, after):
            return ("unavailable", "healthy")

        def expire_due(self, tenant):
            if tenant == "unavailable":
                raise RuntimeError("sensitive-tenant-data")
            processed.append(tenant)
            return 1

    monkeypatch.setattr(expiration_worker, "PostgresKnowledgeIndex", Index)
    expiration_worker.run(Stop())
    assert processed == ["healthy"]
    output = capsys.readouterr()
    assert "sensitive-tenant-data" not in output.out + output.err


def test_expiry_advances_past_failed_batch_and_wraps(monkeypatch):
    monkeypatch.setenv("OC_INDEX_EXPIRY_ENABLED", "true")
    monkeypatch.setenv("OC_KNOWLEDGE_SEARCH_DSN", "synthetic-dsn")
    pages = []
    processed = []

    class Stop:
        stopped = False

        def is_set(self):
            return self.stopped

        def wait(self, seconds):
            assert seconds in (2, 5)

    stop = Stop()

    class Index:
        def __init__(self, dsn):
            pass

        def expiration_tenants(self, *, after):
            pages.append(after)
            if len(pages) == 4:
                stop.stopped = True
                return ()
            return {None: ("a",), "a": ("b",), "b": ()}[after]

        def expire_due(self, tenant):
            processed.append(tenant)
            if tenant == "a":
                raise RuntimeError("sensitive failure")
            return 1

    monkeypatch.setattr(expiration_worker, "PostgresKnowledgeIndex", Index)
    expiration_worker.run(stop)
    assert pages == [None, "a", "b", None]
    assert processed == ["a", "b"]
