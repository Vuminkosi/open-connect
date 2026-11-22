from contextlib import nullcontext

import pytest

from openconnect_knowledge.chunking import DocumentScope, SourcePage
from openconnect_knowledge.embeddings import Embedding, EmbeddingBatch
from openconnect_knowledge.index_preparation import prepare_document
from openconnect_knowledge.postgres_index import IndexConflictError, PostgresKnowledgeIndex, _validate


class Engine:
    def embed(self, texts, *, input_kind):
        return EmbeddingBatch(tuple(Embedding((1.0, 0.0), False, 1.0) for _ in texts),
                              "fixture", "b" * 64, 2, "a" * 64)


def test_identical_content_does_not_bypass_stale_generation(monkeypatch):
    scope = DocumentScope("tenant", "app", "collection", "doc", "revision", "a" * 64)
    document = prepare_document(scope, [SourcePage("page", "private text")], Engine())
    row = (3, False, scope.source_revision, scope.embedding_space_id, _validate(document),
           document.model_id, document.fingerprint, document.dimension, ["alice"], [], None)

    class Cursor:
        def execute(self, sql, parameters=None):
            return self

        def fetchone(self):
            return row

    class Connection:
        def cursor(self):
            return nullcontext(Cursor())

    store = PostgresKnowledgeIndex("synthetic")
    monkeypatch.setattr(store, "_connect", lambda: nullcontext(Connection()))
    with pytest.raises(IndexConflictError, match="generation_conflict"):
        store.upsert(document, expected_generation=0, principals=["alice"])
    assert store.upsert(document, expected_generation=2, principals=["alice"]) == 3
    assert store.upsert(document, expected_generation=3, principals=["alice"]) == 3
