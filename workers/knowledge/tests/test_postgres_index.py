import os
from concurrent.futures import ThreadPoolExecutor
from dataclasses import replace
from datetime import datetime, timedelta, timezone
from uuid import uuid4

import psycopg
import pytest
from psycopg.conninfo import conninfo_to_dict

from openconnect_knowledge.chunking import DocumentScope, SourcePage
from openconnect_knowledge.embeddings import Embedding, EmbeddingBatch
from openconnect_knowledge.index_preparation import prepare_document
from openconnect_knowledge.postgres_index import IndexConflictError, PostgresKnowledgeIndex, SearchScope
from openconnect_knowledge.reranker import RankedCandidate, Ranking
from openconnect_knowledge.search import search_and_rerank

DSN = os.getenv("OC_TEST_KNOWLEDGE_PG_DSN")
pytestmark = pytest.mark.skipif(not DSN, reason="Set OC_TEST_KNOWLEDGE_PG_DSN for disposable PostgreSQL tests")


@pytest.fixture
def store():
    settings = conninfo_to_dict(DSN)
    assert settings.get("dbname") == "openconnect_test" and settings.get("host") in {"127.0.0.1", "::1", "localhost"}
    return PostgresKnowledgeIndex(DSN)


@pytest.fixture
def scope():
    return DocumentScope(str(uuid4()), "app", "collection", "doc", "revision-1", "a" * 64)


class Engine:
    def __init__(self, vector=(1.0, 0.0), space="a" * 64, fingerprint="b" * 64):
        self.vector, self.space, self.fingerprint = vector, space, fingerprint

    def embed(self, texts, *, input_kind):
        assert input_kind == "passage"
        return EmbeddingBatch(tuple(Embedding(self.vector, False, 1.0) for _ in texts),
                              "fixture-model", self.fingerprint, 2, self.space)


def projection(scope, text="green forest timber"):
    return prepare_document(scope, [SourcePage("page-1", text)], Engine())


def test_lifecycle_acl_replay_conflicts_and_tombstone(store, scope):
    document = projection(scope)
    assert store.upsert(document, expected_generation=0, principals=["alice"]) == 1
    assert store.upsert(document, expected_generation=0, principals=["alice"]) == 1
    assert len(store.search(SearchScope(scope.tenant_id, "app", "collection", "alice"), "forest")) == 1
    assert not store.search(SearchScope(scope.tenant_id, "app", "collection", "bob"), "forest")
    assert not store.search(SearchScope("other", "app", "collection", "alice"), "forest")
    assert not store.search(SearchScope(scope.tenant_id, "other", "collection", "alice"), "forest")
    assert not store.search(SearchScope(scope.tenant_id, "app", "other", "alice"), "forest")
    with pytest.raises(IndexConflictError):
        store.upsert(projection(scope, "different forest"), expected_generation=1, principals=["alice"])
    changed = projection(replace(scope, source_revision="revision-2"), "blue forest")
    assert store.upsert(changed, expected_generation=1, principals=[], roles=["readers"]) == 2
    assert not store.search(SearchScope(scope.tenant_id, "app", "collection", "alice"), "forest")
    assert store.search(SearchScope(scope.tenant_id, "app", "collection", "bob", ("readers",)), "forest")
    with pytest.raises(IndexConflictError):
        store.upsert(document, expected_generation=1, principals=["alice"])
    assert store.delete(scope, expected_generation=2) == 3
    assert store.delete(scope, expected_generation=2) == 3
    assert not store.search(SearchScope(scope.tenant_id, "app", "collection", "bob", ("readers",)), "forest")
    with pytest.raises(IndexConflictError):
        store.upsert(document, expected_generation=1, principals=["alice"])
    with psycopg.connect(DSN) as connection:
        rows = connection.execute("""SELECT generation, operation FROM openconnect_knowledge.outbox
            WHERE tenant_id=%s ORDER BY generation""", (scope.tenant_id,)).fetchall()
        tombstone = connection.execute("""SELECT source_revision, model_id, allowed_principals, allowed_roles
            FROM openconnect_knowledge.documents WHERE tenant_id=%s""", (scope.tenant_id,)).fetchone()
    assert rows == [(1, "upsert"), (2, "upsert"), (3, "delete")]
    assert tombstone == (None, None, [], [])


def test_expired_document_and_empty_acl_fail_closed(store, scope):
    with pytest.raises(ValueError, match="empty_acl"):
        store.upsert(projection(scope), expected_generation=0, principals=[])
    with pytest.raises(IndexConflictError, match="retention_expired"):
        store.upsert(projection(scope), expected_generation=0, principals=["alice"],
                     expires_at=datetime.now(timezone.utc) - timedelta(seconds=1))
    assert not store.search(SearchScope(scope.tenant_id, "app", "collection", "alice"), "forest")


def test_expiration_tenant_pages_advance_in_database_order(store, scope):
    prefix = f"pagination-{uuid4()}-"
    first, second = prefix + "a", prefix + "b"
    for tenant in (first, second):
        tenant_scope = replace(scope, tenant_id=tenant)
        store.upsert(projection(tenant_scope), expected_generation=0, principals=["alice"],
                     expires_at=datetime.now(timezone.utc) + timedelta(hours=1))
    with psycopg.connect(DSN) as connection:
        connection.execute("""UPDATE openconnect_knowledge.documents
            SET expires_at = clock_timestamp() - interval '1 second'
            WHERE tenant_id IN (%s, %s)""", (first, second))

    assert store.expiration_tenants(limit=1, after=prefix) == (first,)
    assert store.expiration_tenants(limit=1, after=first) == (second,)
    assert first not in store.expiration_tenants(limit=256, after=second)
    assert second not in store.expiration_tenants(limit=256, after=second)


def test_expiration_tombstones_once_and_preserves_other_documents(store, scope):
    expired = projection(scope)
    current_scope = replace(scope, document_id="current")
    store.upsert(expired, expected_generation=0, principals=["alice"],
                 expires_at=datetime.now(timezone.utc) + timedelta(hours=1))
    store.upsert(projection(current_scope), expected_generation=0, principals=["alice"],
                 expires_at=datetime.now(timezone.utc) + timedelta(hours=1))
    with psycopg.connect(DSN) as connection:
        updated = connection.execute("""UPDATE openconnect_knowledge.documents
            SET expires_at = clock_timestamp() - interval '1 second'
            WHERE tenant_id=%s AND application_id=%s AND collection_id=%s AND document_id=%s""",
            (scope.tenant_id, scope.application_id, scope.collection_id, scope.document_id))
        assert updated.rowcount == 1
    assert scope.tenant_id in store.expiration_tenants(limit=256)
    assert store.expire_due("different-tenant") == 0
    with ThreadPoolExecutor(max_workers=2) as pool:
        counts = list(pool.map(lambda _: store.expire_due(scope.tenant_id), range(2)))
    assert sum(counts) == 1
    assert scope.tenant_id not in store.expiration_tenants(limit=256)
    with psycopg.connect(DSN) as connection:
        state = connection.execute("""SELECT generation, deleted, source_revision, allowed_principals
            FROM openconnect_knowledge.documents WHERE tenant_id=%s AND document_id=%s""",
            (scope.tenant_id, scope.document_id)).fetchone()
        chunks = connection.execute("SELECT count(*) FROM openconnect_knowledge.chunks "
                                    "WHERE tenant_id=%s AND document_id=%s",
                                    (scope.tenant_id, scope.document_id)).fetchone()[0]
        events = connection.execute("SELECT generation, operation FROM openconnect_knowledge.outbox "
                                    "WHERE tenant_id=%s AND document_id=%s ORDER BY generation",
                                    (scope.tenant_id, scope.document_id)).fetchall()
    assert state == (2, True, None, []) and chunks == 0
    assert events == [(1, "upsert"), (2, "delete")]
    assert store.search(SearchScope(scope.tenant_id, "app", "collection", "alice"), "forest")
    with pytest.raises(IndexConflictError, match="generation_conflict"):
        store.upsert(expired, expected_generation=1, principals=["alice"])


def test_acl_revocation_uses_generation_and_does_not_replay_old_access(store, scope):
    document = projection(scope)
    assert store.upsert(document, expected_generation=0, principals=["alice"]) == 1
    assert store.upsert(document, expected_generation=1, principals=["bob"]) == 2
    assert not store.search(SearchScope(scope.tenant_id, "app", "collection", "alice"), "forest")
    assert store.search(SearchScope(scope.tenant_id, "app", "collection", "bob"), "forest")
    with pytest.raises(IndexConflictError):
        store.upsert(document, expected_generation=0, principals=["alice"])


def test_stale_replay_cannot_succeed_after_delete_and_recreation(store, scope):
    document = projection(scope)
    assert store.upsert(document, expected_generation=0, principals=["alice"]) == 1
    assert store.delete(scope, expected_generation=1) == 2
    assert store.upsert(document, expected_generation=2, principals=["alice"]) == 3
    with pytest.raises(IndexConflictError, match="generation_conflict"):
        store.upsert(document, expected_generation=0, principals=["alice"])
    assert store.upsert(document, expected_generation=2, principals=["alice"]) == 3


def test_failed_replacement_rolls_back_document_chunks_and_outbox(store, scope):
    assert store.upsert(projection(scope), expected_generation=0, principals=["alice"]) == 1
    invalid = projection(replace(scope, source_revision="revision-2"), "broken\x00forest")
    with pytest.raises(psycopg.Error):
        store.upsert(invalid, expected_generation=1, principals=["alice"])
    hits = store.search(SearchScope(scope.tenant_id, "app", "collection", "alice"), "forest")
    assert len(hits) == 1 and hits[0].source_revision == "revision-1"
    with psycopg.connect(DSN) as connection:
        assert connection.execute("SELECT count(*) FROM openconnect_knowledge.outbox WHERE tenant_id=%s",
                                  (scope.tenant_id,)).fetchone()[0] == 1


def test_concurrent_replacements_have_one_generation_winner(store, scope):
    store.upsert(projection(scope), expected_generation=0, principals=["alice"])

    def replace_document(revision):
        try:
            return store.upsert(projection(replace(scope, source_revision=revision), revision),
                                expected_generation=1, principals=["alice"])
        except IndexConflictError:
            return "conflict"

    with ThreadPoolExecutor(max_workers=2) as pool:
        results = list(pool.map(replace_document, ["revision-2", "revision-3"]))
    assert sorted(results, key=str) == [2, "conflict"]


def test_ten_thousand_chunk_write_is_complete(store, scope):
    document = prepare_document(scope, [SourcePage("page-1", "x" * 640000)], Engine(),
                                max_chars=64, overlap_chars=0)
    assert len(document.chunks) == 10_000
    assert store.upsert(document, expected_generation=0, principals=["alice"]) == 1
    with psycopg.connect(DSN) as connection:
        count = connection.execute("SELECT count(*) FROM openconnect_knowledge.chunks WHERE tenant_id=%s",
                                   (scope.tenant_id,)).fetchone()[0]
    assert count == 10_000
    hits = store.hybrid_search(SearchScope(scope.tenant_id, "app", "collection", "alice"),
                               "unmatched", (1.0, 0.0), embedding_space_id="a" * 64,
                               model_id="fixture-model", fingerprint="b" * 64, limit=5)
    assert len(hits) == 5 and all(hit.document_id == "doc" for hit in hits)


def test_hybrid_search_filters_acl_and_model_before_ranking(store, scope):
    store.upsert(projection(scope, "timber forest"), expected_generation=0, principals=["alice"])
    second_scope = replace(scope, document_id="second")
    second = prepare_document(second_scope, [SourcePage("page", "green woodland")], Engine((0.0, 1.0)))
    store.upsert(second, expected_generation=0, principals=["alice"])
    hidden_scope = replace(scope, document_id="hidden")
    store.upsert(projection(hidden_scope, "timber secret"), expected_generation=0, principals=["bob"])
    other_scope = replace(scope, document_id="other-space", embedding_space_id="c" * 64)
    other = prepare_document(other_scope, [SourcePage("page", "timber incompatible")], Engine(space="c" * 64))
    store.upsert(other, expected_generation=0, principals=["alice"])
    access = SearchScope(scope.tenant_id, "app", "collection", "alice")
    hits = store.hybrid_search(access, "timber", (1.0, 0.0), embedding_space_id="a" * 64,
                               model_id="fixture-model", fingerprint="b" * 64)
    assert [hit.document_id for hit in hits] == ["doc", "second"]
    assert hits[0].score > hits[1].score and all(isinstance(hit.score, float) for hit in hits)
    assert not store.hybrid_search(replace(access, tenant_id="other"), "timber", (1.0, 0.0),
                                   embedding_space_id="a" * 64, model_id="fixture-model", fingerprint="b" * 64)
    with pytest.raises(ValueError, match="embedding_space_mismatch"):
        store.hybrid_search(access, "timber", (1.0, 0.0), embedding_space_id="a" * 64,
                            model_id="fixture-model", fingerprint="d" * 64)


def test_hybrid_rejects_corrupt_dimensions_and_invalid_vectors(store, scope):
    store.upsert(projection(scope), expected_generation=0, principals=["alice"])
    access = SearchScope(scope.tenant_id, "app", "collection", "alice")
    options = {"embedding_space_id": "a" * 64, "model_id": "fixture-model", "fingerprint": "b" * 64}
    for vector in [(0.0, 0.0), (float("nan"), 0.0), (True, 0.0)]:
        with pytest.raises(ValueError, match="invalid_search"):
            store.hybrid_search(access, "forest", vector, **options)
    with psycopg.connect(DSN) as connection:
        connection.execute("UPDATE openconnect_knowledge.chunks SET embedding=ARRAY[1.0] WHERE tenant_id=%s",
                           (scope.tenant_id,))
    with pytest.raises(ValueError, match="search_capacity_or_dimension_exceeded"):
        store.hybrid_search(access, "forest", (1.0, 0.0), **options)


def test_reranking_receives_only_prefiltered_content_and_preserves_citations(store, scope):
    store.upsert(projection(scope, "forest public"), expected_generation=0, principals=["alice"])
    hidden = replace(scope, document_id="secret")
    store.upsert(projection(hidden, "forest private"), expected_generation=0, principals=["bob"])

    class Reranker:
        def rank(self, query, candidates):
            assert query == "forest" and [item.text for item in candidates] == ["forest public"]
            return Ranking((RankedCandidate(candidates[0].id, 1.0),), "fixture", "a" * 64)

    result = search_and_rerank(store, Reranker(), SearchScope(scope.tenant_id, "app", "collection", "alice"),
                               "forest")
    assert len(result.hits) == 1 and result.hits[0].hit.document_id == "doc"
    assert result.hits[0].hit.text == "forest public" and result.hits[0].rerank_score == 1.0
    assert result.warnings == ()


def test_access_revoked_during_reranking_is_not_returned(store, scope):
    document = projection(scope, "forest public")
    store.upsert(document, expected_generation=0, principals=["alice"])

    class RevokingReranker:
        def rank(self, query, candidates):
            store.upsert(document, expected_generation=1, principals=["bob"])
            return Ranking((RankedCandidate(candidates[0].id, 1.0),), "fixture", "a" * 64)

    result = search_and_rerank(store, RevokingReranker(),
                               SearchScope(scope.tenant_id, "app", "collection", "alice"), "forest")
    assert not result.hits and result.warnings == ("access_or_source_changed",)


def test_model_space_changed_during_reranking_is_not_returned(store, scope):
    document = projection(scope, "forest public")
    store.upsert(document, expected_generation=0, principals=["alice"])

    class MigratingReranker:
        def rank(self, query, candidates):
            migrated_scope = replace(scope, embedding_space_id="c" * 64)
            migrated = prepare_document(migrated_scope, [SourcePage("page-1", "forest public")], Engine(space="c" * 64))
            store.upsert(migrated, expected_generation=1, principals=["alice"])
            return Ranking((RankedCandidate(candidates[0].id, 1.0),), "fixture", "a" * 64)

    result = search_and_rerank(store, MigratingReranker(), SearchScope(scope.tenant_id, "app", "collection", "alice"),
                               "forest", vector=(1.0, 0.0), embedding_space_id="a" * 64,
                               model_id="fixture-model", fingerprint="b" * 64)
    assert not result.hits and result.warnings == ("access_or_source_changed",)


def test_reranker_cannot_inject_candidates_into_hybrid_results(store, scope):
    store.upsert(projection(scope, "forest public"), expected_generation=0, principals=["alice"])

    class MalformedReranker:
        def rank(self, query, candidates):
            return Ranking((RankedCandidate("unexpected", 1.0),), "fixture", "a" * 64)

    with pytest.raises(RuntimeError, match="rerank_result_invalid"):
        search_and_rerank(store, MalformedReranker(),
            SearchScope(scope.tenant_id, "app", "collection", "alice"), "forest", vector=(1.0, 0.0),
            embedding_space_id="a" * 64, model_id="fixture-model", fingerprint="b" * 64)
