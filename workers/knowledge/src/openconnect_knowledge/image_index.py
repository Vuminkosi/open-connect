"""Tenant-scoped derived image vectors inheriting their parent document ACL."""

from __future__ import annotations

import math
import re
from dataclasses import dataclass
from itertools import islice
from typing import Sequence

import psycopg

from openconnect_knowledge.chunking import DocumentScope, _identifier
from openconnect_knowledge.postgres_index import IndexConflictError, SearchScope, _acl, _keys

MAX_VALUES = 4_000_000


@dataclass(frozen=True, repr=False)
class ImageHit:
    document_id: str
    image_id: str
    image_revision: str
    score: float


def _vector(vector: Sequence[float]) -> list[float]:
    if isinstance(vector, (str, bytes)):
        raise ValueError("invalid_vector")
    snapshot = tuple(islice(vector, 8193))
    if not 1 <= len(snapshot) <= 8192:
        raise ValueError("invalid_vector")
    if any(isinstance(value, bool) or not isinstance(value, (int, float)) for value in snapshot):
        raise ValueError("invalid_vector")
    try:
        values = [float(value) for value in snapshot]
    except (OverflowError, ValueError, TypeError):
        raise ValueError("invalid_vector") from None
    if any(not math.isfinite(value) or abs(value) > 1 for value in values):
        raise ValueError("invalid_vector")
    if abs(math.sqrt(math.fsum(value * value for value in values)) - 1) > .001:
        raise ValueError("invalid_vector")
    return values


def _identity(space: str, model_id: str, fingerprint: str) -> None:
    _identifier(model_id)
    if (not isinstance(space, str) or not re.fullmatch(r"[0-9a-f]{64}", space)
            or not isinstance(fingerprint, str) or not re.fullmatch(r"[0-9a-f]{64}", fingerprint)):
        raise ValueError("invalid_model")


class PostgresImageIndex:
    """Trusted hosts authorize writes and supplied caller identity before using this library."""

    def __init__(self, dsn: str):
        if not isinstance(dsn, str) or not dsn.strip():
            raise ValueError("invalid_database_configuration")
        self._dsn = dsn

    def _verify(self) -> None:
        with psycopg.connect(self._dsn, connect_timeout=5) as connection:
            connection.execute("SET LOCAL statement_timeout = '5s'")
            connection.execute("SELECT tenant_id, embedding FROM openconnect_knowledge.image_vectors LIMIT 0")
            connection.execute("SELECT tenant_id, allowed_principals, allowed_roles "
                               "FROM openconnect_knowledge.documents LIMIT 0")

    def upsert(self, scope: DocumentScope, *, expected_generation: int, image_id: str, image_revision: str,
               vector: Sequence[float], embedding_space_id: str, model_id: str, fingerprint: str,
               expected_image_revision: str | None = None) -> None:
        keys = _keys(scope)
        _identifier(image_id)
        _identifier(image_revision)
        if expected_image_revision is not None:
            _identifier(expected_image_revision)
        _identity(embedding_space_id, model_id, fingerprint)
        values = _vector(vector)
        if isinstance(expected_generation, bool) or not isinstance(expected_generation, int) or expected_generation < 1:
            raise ValueError("invalid_generation")
        with psycopg.connect(self._dsn, connect_timeout=5) as connection:
            connection.execute("SET LOCAL statement_timeout = '5s'")
            parent = connection.execute("""SELECT generation, source_revision FROM openconnect_knowledge.documents
                WHERE tenant_id=%s AND application_id=%s AND collection_id=%s AND document_id=%s
                AND NOT deleted AND (expires_at IS NULL OR expires_at > statement_timestamp()) FOR UPDATE""",
                keys).fetchone()
            if parent != (expected_generation, scope.source_revision):
                raise IndexConflictError("generation_conflict")
            existing = connection.execute("""SELECT image_revision, model_id, model_fingerprint, embedding_space_id, embedding
                FROM openconnect_knowledge.image_vectors WHERE tenant_id=%s AND application_id=%s
                AND collection_id=%s AND document_id=%s AND image_id=%s""", (*keys, image_id)).fetchone()
            identity = (image_revision, model_id, fingerprint, embedding_space_id, values)
            if existing is not None and existing[0] == image_revision and existing != identity:
                raise IndexConflictError("revision_conflict")
            if existing == identity:
                return
            if (existing is None and expected_image_revision is not None
                    or existing is not None and existing[0] != expected_image_revision):
                raise IndexConflictError("image_revision_conflict")
            connection.execute("""INSERT INTO openconnect_knowledge.image_vectors
                (tenant_id, application_id, collection_id, document_id, image_id, image_revision,
                 document_generation, model_id, model_fingerprint, embedding_space_id, embedding)
                VALUES (%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s)
                ON CONFLICT (tenant_id, application_id, collection_id, document_id, image_id)
                DO UPDATE SET image_revision=excluded.image_revision, document_generation=excluded.document_generation,
                    model_id=excluded.model_id, model_fingerprint=excluded.model_fingerprint,
                    embedding_space_id=excluded.embedding_space_id, embedding=excluded.embedding""",
                (*keys, image_id, image_revision, expected_generation, model_id, fingerprint, embedding_space_id, values))

    def search(self, scope: SearchScope, vector: Sequence[float], *, embedding_space_id: str, model_id: str,
               fingerprint: str, limit: int = 10) -> tuple[ImageHit, ...]:
        for value in (scope.tenant_id, scope.application_id, scope.collection_id, scope.principal_id):
            _identifier(value)
        roles = _acl(scope.role_ids)
        _identity(embedding_space_id, model_id, fingerprint)
        values = _vector(vector)
        if isinstance(limit, bool) or not isinstance(limit, int) or not 1 <= limit <= 100:
            raise ValueError("invalid_search")
        params = (scope.tenant_id, scope.application_id, scope.collection_id, scope.principal_id, roles,
                  embedding_space_id, model_id, fingerprint)
        authorized = """FROM openconnect_knowledge.documents d JOIN openconnect_knowledge.image_vectors i
            ON i.tenant_id=d.tenant_id AND i.application_id=d.application_id AND i.collection_id=d.collection_id
            AND i.document_id=d.document_id AND i.document_generation=d.generation
            WHERE d.tenant_id=%s AND d.application_id=%s AND d.collection_id=%s AND NOT d.deleted
            AND (d.expires_at IS NULL OR d.expires_at > statement_timestamp())
            AND (%s = ANY(d.allowed_principals) OR d.allowed_roles && %s::text[])
            AND i.embedding_space_id=%s AND i.model_id=%s AND i.model_fingerprint=%s"""
        with psycopg.connect(self._dsn, connect_timeout=5) as connection:
            connection.execute("SET TRANSACTION ISOLATION LEVEL REPEATABLE READ READ ONLY")
            connection.execute("SET LOCAL statement_timeout = '5s'")
            count, mismatch = connection.execute("SELECT count(*), coalesce(bool_or(cardinality(embedding) <> %s), false) "
                "FROM (SELECT i.embedding " + authorized + " LIMIT 10001) c", (len(values), *params)).fetchone()
            if mismatch or count > 10000 or count * len(values) > MAX_VALUES:
                raise ValueError("search_capacity_or_dimension_exceeded")
            rows = connection.execute("""WITH candidates AS MATERIALIZED (SELECT i.document_id, i.image_id,
                i.image_revision, i.document_generation, i.embedding """ + authorized + """), scored AS (
                SELECT document_id, image_id, image_revision, document_generation, sum(pair.x * pair.y) AS score
                FROM candidates CROSS JOIN LATERAL unnest(embedding, %s::double precision[]) AS pair(x, y)
                GROUP BY document_id, image_id, image_revision, document_generation)
                SELECT document_id, image_id, image_revision, document_generation, score FROM scored
                ORDER BY score DESC, document_id, image_id LIMIT %s""", (*params, values, limit)).fetchall()
        if not rows:
            return ()
        # Recheck authorization and identity after ranking using a fresh snapshot.
        with psycopg.connect(self._dsn, connect_timeout=5) as connection:
            connection.execute("SET LOCAL statement_timeout = '5s'")
            current = connection.execute("SELECT i.document_id, i.image_id, i.image_revision, i.document_generation "
                + authorized + " AND i.document_id = ANY(%s::text[]) AND i.image_id = ANY(%s::text[])",
                (*params, list({row[0] for row in rows}), list({row[1] for row in rows}))).fetchall()
        valid = set(current)
        return tuple(ImageHit(document, image, revision, score)
                     for document, image, revision, generation, score in rows
                     if (document, image, revision, generation) in valid)
