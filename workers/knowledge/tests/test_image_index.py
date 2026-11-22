import os
from concurrent.futures import ThreadPoolExecutor
from dataclasses import replace
from datetime import datetime, timedelta, timezone
from uuid import uuid4

import psycopg
import pytest
from psycopg.conninfo import conninfo_to_dict

from openconnect_knowledge.chunking import DocumentScope
from openconnect_knowledge.image_index import PostgresImageIndex
from openconnect_knowledge.postgres_index import IndexConflictError, PostgresKnowledgeIndex, SearchScope
from test_postgres_index import projection

DSN = os.getenv("OC_TEST_KNOWLEDGE_PG_DSN")
pytestmark = pytest.mark.skipif(not DSN, reason="Set OC_TEST_KNOWLEDGE_PG_DSN for disposable PostgreSQL tests")


@pytest.fixture
def stores():
    settings = conninfo_to_dict(DSN)
    assert settings.get("dbname") == "openconnect_test" and settings.get("host") in {"127.0.0.1", "::1", "localhost"}
    return PostgresKnowledgeIndex(DSN), PostgresImageIndex(DSN)


def test_image_retrieval_inherits_acl_space_and_document_lifecycle(stores):
    documents, images = stores
    scope = DocumentScope(str(uuid4()), "app", "collection", "doc", "rev1", "a" * 64)
    other = replace(scope, tenant_id=str(uuid4()))
    for item in (scope, other):
        documents.upsert(projection(item), expected_generation=0, principals=["alice"])
        images.upsert(item, expected_generation=1, image_id="image", image_revision="v1", vector=[1.0, 0.0],
                      embedding_space_id="c" * 64, model_id="clip", fingerprint="d" * 64)
    query = SearchScope(scope.tenant_id, "app", "collection", "alice")
    args = dict(embedding_space_id="c" * 64, model_id="clip", fingerprint="d" * 64)
    assert [(hit.document_id, hit.image_id, hit.score) for hit in images.search(query, [1.0, 0.0], **args)] == [
        ("doc", "image", 1.0)]
    assert not images.search(replace(query, principal_id="bob"), [1.0, 0.0], **args)
    assert not images.search(replace(query, collection_id="other"), [1.0, 0.0], **args)
    assert not images.search(query, [1.0, 0.0], **(args | {"embedding_space_id": "e" * 64}))
    with pytest.raises(IndexConflictError, match="revision_conflict"):
        images.upsert(scope, expected_generation=1, image_id="image", image_revision="v1", vector=[0.0, 1.0], **args)
    images.upsert(scope, expected_generation=1, image_id="image", image_revision="v2", vector=[0.0, 1.0],
                  expected_image_revision="v1", **args)
    with pytest.raises(IndexConflictError, match="image_revision_conflict"):
        images.upsert(scope, expected_generation=1, image_id="image", image_revision="v1", vector=[1.0, 0.0], **args)
    documents.upsert(projection(scope), expected_generation=1, principals=["bob"])
    assert not images.search(query, [1.0, 0.0], **args)
    assert not images.search(replace(query, principal_id="bob"), [1.0, 0.0], **args)
    with pytest.raises(IndexConflictError, match="generation_conflict"):
        images.upsert(scope, expected_generation=1, image_id="image", image_revision="v2", vector=[1.0, 0.0], **args)
    images.upsert(scope, expected_generation=2, image_id="image", image_revision="v2", vector=[1.0, 0.0], **args)
    documents.delete(scope, expected_generation=2)
    with psycopg.connect(DSN) as connection:
        assert connection.execute("SELECT count(*) FROM openconnect_knowledge.image_vectors WHERE tenant_id=%s",
                                  (scope.tenant_id,)).fetchone()[0] == 0
        assert connection.execute("SELECT count(*) FROM openconnect_knowledge.image_vectors WHERE tenant_id=%s",
                                  (other.tenant_id,)).fetchone()[0] == 1


def test_expiry_removes_derived_images_and_prevents_late_publication(stores):
    documents, images = stores
    scope = DocumentScope(str(uuid4()), "app", "collection", "doc", "rev1", "a" * 64)
    documents.upsert(projection(scope), expected_generation=0, principals=["alice"],
                     expires_at=datetime.now(timezone.utc) + timedelta(hours=1))
    args = dict(expected_generation=1, image_id="image", image_revision="v1", vector=[1.0, 0.0],
                embedding_space_id="c" * 64, model_id="clip", fingerprint="d" * 64)
    with ThreadPoolExecutor(max_workers=2) as pool:
        list(pool.map(lambda _: images.upsert(scope, **args), range(2)))
    with psycopg.connect(DSN) as connection:
        connection.execute("UPDATE openconnect_knowledge.documents SET expires_at=clock_timestamp()-interval '1 second' "
                           "WHERE tenant_id=%s AND document_id=%s", (scope.tenant_id, scope.document_id))
    assert documents.expire_due(scope.tenant_id) == 1
    with psycopg.connect(DSN) as connection:
        assert connection.execute("SELECT count(*) FROM openconnect_knowledge.image_vectors WHERE tenant_id=%s",
                                  (scope.tenant_id,)).fetchone()[0] == 0
    with pytest.raises(IndexConflictError, match="generation_conflict"):
        images.upsert(scope, **args)
