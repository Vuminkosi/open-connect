import pytest

from openconnect_knowledge import outbox


@pytest.mark.parametrize("event_id", [True, 0, -1, "1", None])
def test_retry_dead_rejects_invalid_event_before_database(monkeypatch, event_id):
    monkeypatch.setattr(outbox.psycopg, "connect", lambda *args, **kwargs: pytest.fail("unexpected database access"))
    with pytest.raises(ValueError, match="invalid_event"):
        outbox.PostgresOutbox("synthetic").retry_dead("tenant", event_id)


def test_retry_dead_uses_bound_tenant_and_event_and_reports_absence(monkeypatch):
    calls = []

    class Connection:
        def __enter__(self):
            return self

        def __exit__(self, *args):
            pass

        def execute(self, statement, parameters=None):
            calls.append((statement, parameters))
            return self

        def fetchone(self):
            return None

    monkeypatch.setattr(outbox.psycopg, "connect", lambda *args, **kwargs: Connection())
    assert not outbox.PostgresOutbox("synthetic").retry_dead("tenant", 123)
    query, parameters = calls[-1]
    assert "tenant_id=%s" in query and "delivery_state='dead'" in query
    assert parameters == ("tenant", 123)
