"""Tenant-scoped PostgreSQL index; only trusted, authorized hosts may call it."""

from __future__ import annotations

import hashlib
import json
import math
import re
from dataclasses import dataclass
from datetime import datetime
from itertools import islice
from typing import Sequence
from uuid import UUID

import psycopg

from openconnect_knowledge.chunking import MAX_CHUNKS, DocumentScope, _identifier
from openconnect_knowledge.index_preparation import MAX_VECTOR_VALUES, PreparedDocument


class IndexConflictError(ValueError):
    """The document generation or immutable source revision did not match."""


class IndexRetentionExpiredError(IndexConflictError):
    """The database clock reached the document's retention deadline."""


@dataclass(frozen=True, repr=False)
class JobFence:
    job_id: UUID
    lease_id: UUID
    fencing_token: int


@dataclass(frozen=True, repr=False)
class SearchScope:
    tenant_id: str
    application_id: str
    collection_id: str
    principal_id: str
    role_ids: tuple[str, ...] = ()


@dataclass(frozen=True, repr=False)
class SearchHit:
    document_id: str
    source_revision: str
    chunk_id: str
    page_id: str
    page_index: int
    start: int
    end: int
    text: str
    score: float


def _keys(scope: DocumentScope) -> tuple[str, str, str, str]:
    values = (scope.tenant_id, scope.application_id, scope.collection_id, scope.document_id)
    for value in (*values, scope.source_revision):
        _identifier(value)
    if not isinstance(scope.embedding_space_id, str) or not re.fullmatch(r"[0-9a-f]{64}", scope.embedding_space_id):
        raise ValueError("invalid_scope")
    return values


def _acl(values: Sequence[str]) -> list[str]:
    if isinstance(values, (str, bytes)):
        raise ValueError("invalid_acl")
    snapshot = tuple(islice(values, 65))
    if len(snapshot) > 64:
        raise ValueError("invalid_acl")
    for value in snapshot:
        _identifier(value)
    return sorted(set(snapshot))


def _search_vector(vector: Sequence[float]) -> list[float]:
    if isinstance(vector, (str, bytes)):
        raise ValueError("invalid_search")
    snapshot = tuple(islice(vector, 8193))
    if not 1 <= len(snapshot) <= 8192 or any(
            not isinstance(value, (float, int)) or isinstance(value, bool) for value in snapshot):
        raise ValueError("invalid_search")
    try:
        values = [float(value) for value in snapshot]
    except (OverflowError, ValueError, TypeError):
        raise ValueError("invalid_search") from None
    if any(not math.isfinite(value) or abs(value) > 1 for value in values):
        raise ValueError("invalid_search")
    if abs(math.sqrt(math.fsum(value * value for value in values)) - 1) > .001:
        raise ValueError("invalid_search")
    return values


def _validate(document: PreparedDocument) -> str:
    _keys(document.scope)
    if (not isinstance(document.model_id, str) or not 1 <= len(document.model_id) <= 256
            or not isinstance(document.fingerprint, str) or not re.fullmatch(r"[0-9a-f]{64}", document.fingerprint)
            or isinstance(document.dimension, bool) or not isinstance(document.dimension, int)
            or not 1 <= document.dimension <= 8192 or type(document.chunks) is not tuple
            or not 1 <= len(document.chunks) <= MAX_CHUNKS
            or len(document.chunks) * document.dimension > MAX_VECTOR_VALUES):
        raise ValueError("invalid_projection")
    digest = hashlib.sha256()
    ids: set[str] = set()
    for item in document.chunks:
        chunk = item.chunk
        _identifier(chunk.page_id)
        if (not re.fullmatch(r"[0-9a-f]{64}", chunk.chunk_id) or chunk.chunk_id in ids
                or not isinstance(chunk.text, str) or not 1 <= len(chunk.text) <= 8192
                or hashlib.sha256(chunk.text.encode("utf-8")).hexdigest() != chunk.content_sha256
                or isinstance(chunk.page_index, bool) or not isinstance(chunk.page_index, int)
                or not 0 <= chunk.page_index < 30 or not isinstance(chunk.start, int)
                or not isinstance(chunk.end, int) or isinstance(chunk.start, bool) or isinstance(chunk.end, bool)
                or not 0 <= chunk.start < chunk.end or chunk.end - chunk.start != len(chunk.text)
                or type(item.vector) is not tuple or len(item.vector) != document.dimension
                or any(type(v) not in (float, int) or not math.isfinite(v)
                       or abs(v) > 1 for v in item.vector)
                or abs(math.sqrt(math.fsum(float(v) ** 2 for v in item.vector)) - 1) > .001):
            raise ValueError("invalid_projection")
        ids.add(chunk.chunk_id)
        material = [chunk.chunk_id, chunk.page_id, chunk.page_index, chunk.start, chunk.end,
                    chunk.content_sha256, list(item.vector)]
        encoded = json.dumps(material, ensure_ascii=True, separators=(",", ":"), allow_nan=False).encode()
        digest.update(len(encoded).to_bytes(8, "big"))
        digest.update(encoded)
    return digest.hexdigest()


class PostgresKnowledgeIndex:
    """Transactional generation checks and bounded retrieval with ACL prefiltering.

    The host authenticates and authorizes all scope values. This library does
    not expose a network endpoint, validate task leases, or configure encryption.
    """

    def __init__(self, dsn: str):
        if not isinstance(dsn, str) or not dsn.strip():
            raise ValueError("invalid_database_configuration")
        self._dsn = dsn

    def _connect(self):
        return psycopg.connect(self._dsn, connect_timeout=5)

    def _verify(self) -> None:
        """Confirm the required relations are readable without retrieving tenant data."""
        with self._connect() as connection:
            connection.execute("SET LOCAL statement_timeout = '5s'")
            connection.execute("SELECT tenant_id, allowed_principals, allowed_roles "
                               "FROM openconnect_knowledge.documents LIMIT 0")
            connection.execute("SELECT tenant_id, content, embedding FROM openconnect_knowledge.chunks LIMIT 0")
            connection.execute("SELECT tenant_id, embedding FROM openconnect_knowledge.image_vectors LIMIT 0")
            connection.execute("SELECT tenant_id, generation, operation, delivery_state "
                               "FROM openconnect_knowledge.outbox LIMIT 0")

    def expiration_tenants(self, *, limit: int = 64, after: str | None = None) -> tuple[str, ...]:
        """Find a bounded, tenant-ordered page without retrieving document content."""
        if isinstance(limit, bool) or not isinstance(limit, int) or not 1 <= limit <= 256:
            raise ValueError("invalid_limit")
        if after is not None:
            _identifier(after)
        with self._connect() as connection:
            connection.execute("SET LOCAL statement_timeout = '5s'")
            rows = connection.execute("""SELECT tenant_id FROM openconnect_knowledge.documents
                WHERE NOT deleted AND expires_at <= statement_timestamp()
                    AND (%s::text IS NULL OR tenant_id > %s)
                GROUP BY tenant_id ORDER BY tenant_id LIMIT %s""", (after, after, limit)).fetchall()
        return tuple(row[0] for row in rows)

    def expire_due(self, tenant_id: str, *, limit: int = 64) -> int:
        """Tombstone expired documents and enqueue reconciliation in one transaction."""
        _identifier(tenant_id)
        if isinstance(limit, bool) or not isinstance(limit, int) or not 1 <= limit <= 256:
            raise ValueError("invalid_limit")
        with self._connect() as connection, connection.cursor() as cursor:
            cursor.execute("SET LOCAL statement_timeout = '5s'")
            rows = cursor.execute("""SELECT application_id, collection_id, document_id, generation
                FROM openconnect_knowledge.documents
                WHERE tenant_id=%s AND NOT deleted AND expires_at <= statement_timestamp()
                ORDER BY expires_at, application_id, collection_id, document_id
                FOR UPDATE SKIP LOCKED LIMIT %s""", (tenant_id, limit)).fetchall()
            for application_id, collection_id, document_id, generation in rows:
                keys = (tenant_id, application_id, collection_id, document_id)
                cursor.execute("""DELETE FROM openconnect_knowledge.image_vectors WHERE tenant_id=%s AND application_id=%s
                    AND collection_id=%s AND document_id=%s""", keys)
                cursor.execute("""DELETE FROM openconnect_knowledge.chunks WHERE tenant_id=%s AND application_id=%s
                    AND collection_id=%s AND document_id=%s""", keys)
                cursor.execute("""UPDATE openconnect_knowledge.documents SET generation=%s, deleted=true,
                    source_revision=NULL, embedding_space_id=NULL, model_id=NULL, model_fingerprint=NULL,
                    dimension=NULL, manifest_sha256=NULL, allowed_principals='{}', allowed_roles='{}',
                    expires_at=NULL, updated_at=clock_timestamp() WHERE tenant_id=%s AND application_id=%s
                    AND collection_id=%s AND document_id=%s""", (generation + 1, *keys))
                cursor.execute("""INSERT INTO openconnect_knowledge.outbox
                    (tenant_id, application_id, collection_id, document_id, generation, operation)
                    VALUES (%s, %s, %s, %s, %s, 'delete')""", (*keys, generation + 1))
        return len(rows)

    def upsert(self, document: PreparedDocument, *, expected_generation: int,
               principals: Sequence[str], roles: Sequence[str] = (), expires_at: datetime | None = None,
               job_fence: JobFence | None = None) -> int:
        keys = _keys(document.scope)
        if isinstance(expected_generation, bool) or not isinstance(expected_generation, int) or expected_generation < 0:
            raise ValueError("invalid_generation")
        allowed_principals, allowed_roles = _acl(principals), _acl(roles)
        if not allowed_principals and not allowed_roles:
            raise ValueError("empty_acl")
        if expires_at is not None and (not isinstance(expires_at, datetime) or expires_at.utcoffset() is None):
            raise ValueError("invalid_expiry")
        manifest = _validate(document)
        with self._connect() as connection, connection.cursor() as cursor:
            cursor.execute("SET LOCAL statement_timeout = '5s'")
            if job_fence is not None:
                if (not isinstance(job_fence, JobFence) or not isinstance(job_fence.job_id, UUID)
                        or not isinstance(job_fence.lease_id, UUID) or isinstance(job_fence.fencing_token, bool)
                        or not isinstance(job_fence.fencing_token, int) or job_fence.fencing_token < 1):
                    raise ValueError("invalid_claim")
                row = cursor.execute("""SELECT payload FROM openconnect_knowledge.index_jobs
                    WHERE tenant_id=%s AND job_id=%s AND state='leased' AND lease_id=%s AND fencing_token=%s
                    AND lease_until > clock_timestamp() FOR UPDATE""",
                    (keys[0], job_fence.job_id, job_fence.lease_id, job_fence.fencing_token)).fetchone()
                if row is None:
                    raise IndexConflictError("claim_expired")
                payload = row[0]
                expected = {"tenantId": keys[0], "applicationId": keys[1], "collectionId": keys[2],
                            "documentId": keys[3], "sourceRevision": document.scope.source_revision,
                            "embeddingSpaceId": document.scope.embedding_space_id,
                            "expectedGeneration": expected_generation, "principals": allowed_principals,
                            "roles": allowed_roles, "expiresAt": expires_at.isoformat() if expires_at else None}
                if any(payload.get(key) != value for key, value in expected.items()):
                    raise IndexConflictError("claim_scope_mismatch")

            def finish_job(generation: int) -> None:
                if job_fence is None:
                    return
                result = cursor.execute("""UPDATE openconnect_knowledge.index_jobs SET state='completed',
                    lease_id=NULL, lease_until=NULL, result_generation=%s WHERE tenant_id=%s AND job_id=%s
                    AND state='leased' AND lease_id=%s AND fencing_token=%s AND lease_until > clock_timestamp()
                    RETURNING job_id""", (generation, keys[0], job_fence.job_id, job_fence.lease_id,
                                          job_fence.fencing_token)).fetchone()
                if result is None:
                    raise IndexConflictError("claim_expired")

            cursor.execute("""INSERT INTO openconnect_knowledge.documents
                (tenant_id, application_id, collection_id, document_id, generation, deleted)
                VALUES (%s, %s, %s, %s, 0, true) ON CONFLICT DO NOTHING""", keys)
            cursor.execute("""SELECT generation, deleted, source_revision, embedding_space_id, manifest_sha256,
                model_id, model_fingerprint, dimension, allowed_principals, allowed_roles, expires_at
                FROM openconnect_knowledge.documents WHERE tenant_id=%s AND application_id=%s
                AND collection_id=%s AND document_id=%s FOR UPDATE""", keys)
            row = cursor.fetchone()
            assert row is not None
            generation, deleted, revision, space, old_manifest, model, fingerprint, dimension, old_principals, old_roles, old_expiry = row
            # Evaluate retention against the database clock while holding the document lock.
            if expires_at is not None and cursor.execute(
                    "SELECT %s::timestamptz <= clock_timestamp()", (expires_at,)).fetchone()[0]:
                raise IndexRetentionExpiredError("retention_expired")
            identity = (document.scope.source_revision, document.scope.embedding_space_id, manifest,
                        document.model_id, document.fingerprint, document.dimension)
            if (not deleted and generation in (expected_generation, expected_generation + 1)
                    and (revision, space, old_manifest, model, fingerprint, dimension) == identity):
                if (old_principals, old_roles, old_expiry) == (allowed_principals, allowed_roles, expires_at):
                    finish_job(generation)
                    return generation
            if generation != expected_generation:
                raise IndexConflictError("generation_conflict")
            if (not deleted and (revision, space) == identity[:2]
                    and (old_manifest, model, fingerprint, dimension) != identity[2:]):
                raise IndexConflictError("revision_conflict")
            generation += 1
            cursor.execute("""DELETE FROM openconnect_knowledge.image_vectors WHERE tenant_id=%s AND application_id=%s
                AND collection_id=%s AND document_id=%s""", keys)
            cursor.execute("""UPDATE openconnect_knowledge.documents SET generation=%s, deleted=false,
                source_revision=%s, embedding_space_id=%s, manifest_sha256=%s, model_id=%s,
                model_fingerprint=%s, dimension=%s, allowed_principals=%s, allowed_roles=%s,
                expires_at=%s, updated_at=clock_timestamp() WHERE tenant_id=%s AND application_id=%s
                AND collection_id=%s AND document_id=%s""",
                (generation, *identity, allowed_principals, allowed_roles, expires_at, *keys))
            cursor.execute("""DELETE FROM openconnect_knowledge.chunks WHERE tenant_id=%s AND application_id=%s
                AND collection_id=%s AND document_id=%s""", keys)
            cursor.executemany("""INSERT INTO openconnect_knowledge.chunks
                (tenant_id, application_id, collection_id, document_id, chunk_id, page_id, page_index,
                 source_start, source_end, content, content_sha256, embedding)
                VALUES (%s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s)""", [
                (*keys, item.chunk.chunk_id, item.chunk.page_id, item.chunk.page_index, item.chunk.start,
                 item.chunk.end, item.chunk.text, item.chunk.content_sha256, list(item.vector))
                for item in document.chunks])
            cursor.execute("""INSERT INTO openconnect_knowledge.outbox
                (tenant_id, application_id, collection_id, document_id, generation, operation)
                VALUES (%s, %s, %s, %s, %s, 'upsert')""", (*keys, generation))
            finish_job(generation)
            return generation

    def delete(self, scope: DocumentScope, *, expected_generation: int) -> int:
        keys = _keys(scope)
        if isinstance(expected_generation, bool) or not isinstance(expected_generation, int) or expected_generation < 0:
            raise ValueError("invalid_generation")
        with self._connect() as connection, connection.cursor() as cursor:
            cursor.execute("SET LOCAL statement_timeout = '5s'")
            cursor.execute("""INSERT INTO openconnect_knowledge.documents
                (tenant_id, application_id, collection_id, document_id, generation, deleted)
                VALUES (%s, %s, %s, %s, 0, true) ON CONFLICT DO NOTHING""", keys)
            cursor.execute("""SELECT generation, deleted FROM openconnect_knowledge.documents
                WHERE tenant_id=%s AND application_id=%s AND collection_id=%s AND document_id=%s FOR UPDATE""", keys)
            generation, deleted = cursor.fetchone()
            if deleted and generation > 0:
                if expected_generation == generation - 1:
                    return generation
                raise IndexConflictError("generation_conflict")
            if generation != expected_generation:
                raise IndexConflictError("generation_conflict")
            generation += 1
            cursor.execute("""DELETE FROM openconnect_knowledge.image_vectors WHERE tenant_id=%s AND application_id=%s
                AND collection_id=%s AND document_id=%s""", keys)
            cursor.execute("""DELETE FROM openconnect_knowledge.chunks WHERE tenant_id=%s AND application_id=%s
                AND collection_id=%s AND document_id=%s""", keys)
            cursor.execute("""UPDATE openconnect_knowledge.documents SET generation=%s, deleted=true,
                source_revision=NULL, embedding_space_id=NULL, model_id=NULL, model_fingerprint=NULL,
                dimension=NULL, manifest_sha256=NULL, allowed_principals='{}', allowed_roles='{}',
                expires_at=NULL, updated_at=clock_timestamp() WHERE tenant_id=%s AND application_id=%s
                AND collection_id=%s AND document_id=%s""", (generation, *keys))
            cursor.execute("""INSERT INTO openconnect_knowledge.outbox
                (tenant_id, application_id, collection_id, document_id, generation, operation)
                VALUES (%s, %s, %s, %s, %s, 'delete')""", (*keys, generation))
            return generation

    def deletion_reconciliation(self, scope: DocumentScope, generation: int) -> str:
        """Observe metadata reconciliation state without claiming object deletion."""
        keys = _keys(scope)
        if isinstance(generation, bool) or not isinstance(generation, int) or generation < 1:
            raise ValueError("invalid_generation")
        with self._connect() as connection:
            connection.execute("SET LOCAL statement_timeout = '5s'")
            row = connection.execute("""SELECT delivery_state FROM openconnect_knowledge.outbox
                WHERE tenant_id=%s AND application_id=%s AND collection_id=%s AND document_id=%s
                    AND generation=%s AND operation='delete'""", (*keys, generation)).fetchone()
        if row is None:
            raise RuntimeError("reconciliation_unavailable")
        return row[0]

    def search(self, scope: SearchScope, query: str, *, limit: int = 10) -> tuple[SearchHit, ...]:
        for value in (scope.tenant_id, scope.application_id, scope.collection_id, scope.principal_id):
            _identifier(value)
        roles = _acl(scope.role_ids)
        if (not isinstance(query, str) or not query.strip() or len(query) > 8192
                or isinstance(limit, bool) or not isinstance(limit, int) or not 1 <= limit <= 100):
            raise ValueError("invalid_search")
        with self._connect() as connection, connection.cursor() as cursor:
            cursor.execute("SET LOCAL statement_timeout = '5s'")
            cursor.execute("""WITH authorized AS MATERIALIZED (
                SELECT document_id, source_revision FROM openconnect_knowledge.documents
                WHERE tenant_id=%s AND application_id=%s AND collection_id=%s AND NOT deleted
                AND (expires_at IS NULL OR expires_at > statement_timestamp())
                AND (%s = ANY(allowed_principals) OR allowed_roles && %s::text[])
            ), candidates AS MATERIALIZED (
                SELECT c.*, a.source_revision FROM authorized a JOIN openconnect_knowledge.chunks c
                  ON c.document_id=a.document_id AND c.tenant_id=%s AND c.application_id=%s AND c.collection_id=%s
            )
            SELECT document_id, source_revision, chunk_id, page_id, page_index, source_start, source_end,
                content, ts_rank_cd(search_text, plainto_tsquery('simple', %s)) AS rank
            FROM candidates WHERE search_text @@ plainto_tsquery('simple', %s)
            ORDER BY rank DESC, chunk_id ASC LIMIT %s""",
                (scope.tenant_id, scope.application_id, scope.collection_id, scope.principal_id, roles,
                 scope.tenant_id, scope.application_id, scope.collection_id, query, query, limit))
            return tuple(SearchHit(*row) for row in cursor.fetchall())

    def revalidate(self, scope: SearchScope, hits: Sequence[SearchHit], *,
                   embedding_space_id: str | None = None, model_id: str | None = None,
                   fingerprint: str | None = None) -> frozenset[tuple[str, str]]:
        """Recheck access, source content and optional model identity after local scoring."""
        for value in (scope.tenant_id, scope.application_id, scope.collection_id, scope.principal_id):
            _identifier(value)
        roles = _acl(scope.role_ids)
        model_identity = (embedding_space_id, model_id, fingerprint)
        if any(value is not None for value in model_identity):
            if (not isinstance(embedding_space_id, str) or not re.fullmatch(r"[0-9a-f]{64}", embedding_space_id)
                    or not isinstance(model_id, str) or not isinstance(fingerprint, str)
                    or not re.fullmatch(r"[0-9a-f]{64}", fingerprint)):
                raise ValueError("invalid_search")
            _identifier(model_id)
        if len(hits) > 32 or any(not isinstance(hit, SearchHit) for hit in hits):
            raise ValueError("invalid_search")
        if not hits:
            return frozenset()
        expected = {(hit.document_id, hit.chunk_id): hit for hit in hits}
        if len(expected) != len(hits):
            raise ValueError("invalid_search")
        with self._connect() as connection, connection.cursor() as cursor:
            cursor.execute("SET LOCAL statement_timeout = '5s'")
            cursor.execute("""SELECT c.document_id, c.chunk_id, d.source_revision, c.page_id, c.page_index,
                c.source_start, c.source_end, c.content
                FROM openconnect_knowledge.documents d JOIN openconnect_knowledge.chunks c
                ON c.tenant_id=d.tenant_id AND c.application_id=d.application_id
                AND c.collection_id=d.collection_id AND c.document_id=d.document_id
                WHERE d.tenant_id=%s AND d.application_id=%s AND d.collection_id=%s AND NOT d.deleted
                AND (d.expires_at IS NULL OR d.expires_at > statement_timestamp())
                AND (%s = ANY(d.allowed_principals) OR d.allowed_roles && %s::text[])
                AND (%s::text IS NULL OR (d.embedding_space_id=%s AND d.model_id=%s AND d.model_fingerprint=%s))
                AND c.document_id = ANY(%s::text[]) AND c.chunk_id = ANY(%s::text[])""",
                (scope.tenant_id, scope.application_id, scope.collection_id, scope.principal_id, roles,
                 embedding_space_id, embedding_space_id, model_id, fingerprint,
                 list({hit.document_id for hit in hits}), list({hit.chunk_id for hit in hits})))
            valid = set()
            for doc_id, chunk_id, revision, page, page_index, start, end, text in cursor:
                hit = expected.get((doc_id, chunk_id))
                if hit is not None and (hit.source_revision, hit.page_id, hit.page_index, hit.start, hit.end, hit.text) == (
                        revision, page, page_index, start, end, text):
                    valid.add((doc_id, chunk_id))
            return frozenset(valid)

    def hybrid_search(self, scope: SearchScope, query: str, vector: Sequence[float], *,
                      embedding_space_id: str, model_id: str, fingerprint: str,
                      limit: int = 10) -> tuple[SearchHit, ...]:
        """Fuse top lexical and exact cosine rankings within one authorized model space.

        The caller supplies a query vector made by the matching model and must
        authorize the scope and sanitize the query before invoking this method.
        Reciprocal-rank scores are not calibrated probabilities.
        """
        for value in (scope.tenant_id, scope.application_id, scope.collection_id, scope.principal_id, model_id):
            _identifier(value)
        roles = _acl(scope.role_ids)
        if (not isinstance(query, str) or not query.strip() or len(query) > 8192
                or isinstance(limit, bool) or not isinstance(limit, int) or not 1 <= limit <= 100
                or not isinstance(embedding_space_id, str) or not re.fullmatch(r"[0-9a-f]{64}", embedding_space_id)
                or not isinstance(fingerprint, str) or not re.fullmatch(r"[0-9a-f]{64}", fingerprint)):
            raise ValueError("invalid_search")
        values = _search_vector(vector)
        dimension = len(values)
        parameters = (scope.tenant_id, scope.application_id, scope.collection_id, scope.principal_id, roles,
                      embedding_space_id, model_id, fingerprint, dimension)
        # The same snapshot is used for preflight and ranking so concurrent writes
        # cannot invalidate the authorized-work bound between the two queries.
        with self._connect() as connection, connection.cursor() as cursor:
            cursor.execute("SET TRANSACTION ISOLATION LEVEL REPEATABLE READ READ ONLY")
            cursor.execute("SET LOCAL statement_timeout = '5s'")
            cursor.execute("""SELECT coalesce(bool_or(model_id <> %s OR model_fingerprint <> %s OR dimension <> %s), false)
                FROM openconnect_knowledge.documents
                WHERE tenant_id=%s AND application_id=%s AND collection_id=%s AND NOT deleted
                AND (expires_at IS NULL OR expires_at > statement_timestamp())
                AND (%s = ANY(allowed_principals) OR allowed_roles && %s::text[])
                AND embedding_space_id=%s""",
                (model_id, fingerprint, dimension, scope.tenant_id, scope.application_id, scope.collection_id,
                 scope.principal_id, roles, embedding_space_id))
            if cursor.fetchone()[0]:
                raise ValueError("embedding_space_mismatch")
            cursor.execute("""WITH authorized AS MATERIALIZED (
                SELECT document_id FROM openconnect_knowledge.documents
                WHERE tenant_id=%s AND application_id=%s AND collection_id=%s AND NOT deleted
                AND (expires_at IS NULL OR expires_at > statement_timestamp())
                AND (%s = ANY(allowed_principals) OR allowed_roles && %s::text[])
                AND embedding_space_id=%s AND model_id=%s AND model_fingerprint=%s AND dimension=%s
            ), candidates AS MATERIALIZED (
                SELECT c.embedding FROM authorized a JOIN openconnect_knowledge.chunks c
                  ON c.document_id=a.document_id AND c.tenant_id=%s AND c.application_id=%s AND c.collection_id=%s
                LIMIT 10001
            ) SELECT count(*), coalesce(bool_or(cardinality(embedding) <> %s), false) FROM candidates""",
                (*parameters, scope.tenant_id, scope.application_id, scope.collection_id, dimension))
            count, invalid_dimension = cursor.fetchone()
            if invalid_dimension or count > 10000 or count * dimension > MAX_VECTOR_VALUES:
                raise ValueError("search_capacity_or_dimension_exceeded")
            cursor.execute("""WITH authorized AS MATERIALIZED (
                SELECT document_id, source_revision FROM openconnect_knowledge.documents
                WHERE tenant_id=%s AND application_id=%s AND collection_id=%s AND NOT deleted
                AND (expires_at IS NULL OR expires_at > statement_timestamp())
                AND (%s = ANY(allowed_principals) OR allowed_roles && %s::text[])
                AND embedding_space_id=%s AND model_id=%s AND model_fingerprint=%s AND dimension=%s
            ), candidates AS MATERIALIZED (
                SELECT c.*, a.source_revision FROM authorized a JOIN openconnect_knowledge.chunks c
                  ON c.document_id=a.document_id AND c.tenant_id=%s AND c.application_id=%s AND c.collection_id=%s
            ), lexical AS (
                SELECT document_id, chunk_id,
                  row_number() OVER (ORDER BY ts_rank_cd(search_text, plainto_tsquery('simple', %s)) DESC,
                                     document_id, chunk_id) AS position
                FROM candidates WHERE search_text @@ plainto_tsquery('simple', %s)
                ORDER BY position LIMIT 100
            ), semantic_scores AS (
                SELECT c.document_id, c.chunk_id, sum(pair.x * pair.y) AS similarity
                FROM candidates c CROSS JOIN LATERAL unnest(c.embedding, %s::double precision[]) AS pair(x, y)
                GROUP BY c.document_id, c.chunk_id
            ), semantic AS (
                SELECT document_id, chunk_id,
                  row_number() OVER (ORDER BY similarity DESC, document_id, chunk_id) AS position
                FROM semantic_scores ORDER BY position LIMIT 100
            )
            SELECT c.document_id, c.source_revision, c.chunk_id, c.page_id, c.page_index, c.source_start,
                   c.source_end, c.content,
                   coalesce(1.0::double precision / (60.0 + l.position), 0) +
                   coalesce(1.0::double precision / (60.0 + s.position), 0) AS score
            FROM candidates c LEFT JOIN lexical l USING (document_id, chunk_id)
                LEFT JOIN semantic s USING (document_id, chunk_id)
            WHERE l.position IS NOT NULL OR s.position IS NOT NULL
            ORDER BY score DESC, c.document_id, c.chunk_id LIMIT %s""",
                (*parameters, scope.tenant_id, scope.application_id, scope.collection_id,
                 query, query, values, limit))
            return tuple(SearchHit(*row) for row in cursor.fetchall())
