import pytest

from openconnect_knowledge.postgres_index import PostgresKnowledgeIndex


def test_readiness_fails_when_deletion_outbox_schema_is_missing(monkeypatch):
    queries = []

    class Connection:
        def __enter__(self):
            return self

        def __exit__(self, *_):
            return False

        def execute(self, query):
            queries.append(query)
            if "openconnect_knowledge.outbox" in query:
                raise RuntimeError("missing outbox")

    index = PostgresKnowledgeIndex("synthetic-dsn")
    monkeypatch.setattr(index, "_connect", lambda: Connection())
    with pytest.raises(RuntimeError, match="missing outbox"):
        index._verify()
    assert any("delivery_state" in query for query in queries)
