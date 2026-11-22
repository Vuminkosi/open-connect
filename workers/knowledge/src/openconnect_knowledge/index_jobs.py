"""Durable indexing claims containing only a tenant-authorized object reference.

The host must authorize tenant, object, target scope and ACL before submission.
Claims alone do not grant storage or document access. Consumers must check the
live claim atomically with index publication and only claim their model space.
"""

from __future__ import annotations

import hashlib
import json
import re
from dataclasses import dataclass
from datetime import datetime
from uuid import UUID, uuid4

import psycopg
from psycopg.types.json import Jsonb

from openconnect_knowledge.chunking import DocumentScope, _identifier
from openconnect_knowledge.postgres_index import _acl, _keys

MAX_ATTEMPTS = 8


def _space_filter(value: str | None) -> None:
    if value is not None and (not isinstance(value, str) or not re.fullmatch(r"[0-9a-f]{64}", value)):
        raise ValueError("invalid_embedding_space")


class IndexWorkerUnavailable(RuntimeError):
    """No live worker accepts this embedding space."""


@dataclass(frozen=True, repr=False)
class IndexJob:
    scope: DocumentScope
    object_id: str
    object_sha256: str
    object_size: int
    object_version: str
    expected_generation: int
    principals: tuple[str, ...]
    roles: tuple[str, ...] = ()
    expires_at: datetime | None = None


@dataclass(frozen=True, repr=False)
class IndexClaim:
    tenant_id: str
    job_id: UUID
    lease_id: UUID
    fencing_token: int
    attempt: int
    payload: dict


@dataclass(frozen=True)
class IndexJobStatus:
    state: str
    attempts: int
    result_generation: int | None
    retry_at: datetime | None


def _payload(job: IndexJob) -> dict:
    if not isinstance(job, IndexJob):
        raise ValueError("invalid_job")
    _keys(job.scope)
    if (not re.fullmatch(r"[0-7][0-9A-HJKMNP-TV-Z]{25}", job.scope.tenant_id)
            or not isinstance(job.object_id, str) or not re.fullmatch(r"[0-9a-f]{64}", job.object_id)
            or not isinstance(job.object_sha256, str) or not re.fullmatch(r"[0-9a-f]{64}", job.object_sha256)
            or isinstance(job.object_size, bool) or not isinstance(job.object_size, int)
            or not 1 <= job.object_size <= 10 * 1024 * 1024
            or not isinstance(job.object_version, str) or not 1 <= len(job.object_version) <= 1024 or job.object_version == "null"
            or any(ord(c) < 32 or ord(c) == 127 for c in job.object_version)
            or isinstance(job.expected_generation, bool) or not isinstance(job.expected_generation, int)
            or not 0 <= job.expected_generation < 2**63 - 1):
        raise ValueError("invalid_job")
    principals, roles = _acl(job.principals), _acl(job.roles)
    if not principals and not roles:
        raise ValueError("invalid_job")
    if job.expires_at is not None and (not isinstance(job.expires_at, datetime) or job.expires_at.utcoffset() is None):
        raise ValueError("invalid_job")
    try:
        job.object_version.encode("utf-8")
    except UnicodeEncodeError as exc:
        raise ValueError("invalid_job") from exc
    scope = job.scope
    return {"tenantId": scope.tenant_id, "applicationId": scope.application_id, "collectionId": scope.collection_id,
            "documentId": scope.document_id, "sourceRevision": scope.source_revision,
            "embeddingSpaceId": scope.embedding_space_id, "objectId": job.object_id,
            "objectSha256": job.object_sha256, "objectSize": job.object_size, "objectVersion": job.object_version,
            "expectedGeneration": job.expected_generation, "principals": principals, "roles": roles,
            "expiresAt": job.expires_at.isoformat() if job.expires_at else None}


class PostgresIndexJobs:
    def __init__(self, dsn: str):
        if not isinstance(dsn, str) or not dsn.strip():
            raise ValueError("invalid_database_configuration")
        self._dsn = dsn

    def record_worker(self, worker_id: UUID, embedding_space_id: str, *, lifetime_seconds: int = 90) -> None:
        if (not isinstance(worker_id, UUID) or not isinstance(embedding_space_id, str)
                or not re.fullmatch(r"[0-9a-f]{64}", embedding_space_id)
                or isinstance(lifetime_seconds, bool) or not isinstance(lifetime_seconds, int)
                or not 1 <= lifetime_seconds <= 300):
            raise ValueError("invalid_worker")
        with psycopg.connect(self._dsn, connect_timeout=5) as connection:
            connection.execute("SET LOCAL statement_timeout = '5s'")
            connection.execute("""INSERT INTO openconnect_knowledge.index_workers
                (worker_id, embedding_space_id, last_seen, expires_at)
                VALUES (%s, %s, clock_timestamp(), clock_timestamp()+(%s * interval '1 second'))
                ON CONFLICT (worker_id) DO UPDATE SET embedding_space_id=excluded.embedding_space_id,
                    last_seen=excluded.last_seen, expires_at=excluded.expires_at""",
                (worker_id, embedding_space_id, lifetime_seconds))

    def live_worker_spaces(self) -> frozenset[str]:
        with psycopg.connect(self._dsn, connect_timeout=5) as connection:
            connection.execute("SET LOCAL statement_timeout = '5s'")
            rows = connection.execute("SELECT DISTINCT embedding_space_id FROM openconnect_knowledge.index_workers "
                                      "WHERE expires_at > clock_timestamp() LIMIT 256").fetchall()
        return frozenset(row[0] for row in rows)

    def retire_worker(self, worker_id: UUID) -> None:
        if not isinstance(worker_id, UUID):
            raise ValueError("invalid_worker")
        with psycopg.connect(self._dsn, connect_timeout=5) as connection:
            connection.execute("SET LOCAL statement_timeout = '5s'")
            connection.execute("UPDATE openconnect_knowledge.index_workers SET expires_at=clock_timestamp() "
                               "WHERE worker_id=%s", (worker_id,))

    def status(self, tenant_id: str, job_id: UUID, *, application_id: str, collection_id: str,
               document_id: str, principal_id: str, roles: tuple[str, ...] = ()) -> IndexJobStatus | None:
        """Return minimal status only while the caller has job and current document access."""
        for value in (tenant_id, application_id, collection_id, document_id, principal_id):
            _identifier(value)
        if not isinstance(job_id, UUID):
            raise ValueError("invalid_job")
        role_ids = _acl(roles)
        with psycopg.connect(self._dsn, connect_timeout=5) as connection:
            connection.execute("SET LOCAL statement_timeout = '5s'")
            row = connection.execute("""SELECT j.state, j.attempts, j.result_generation,
                CASE WHEN j.state='pending' AND j.attempts > 0 THEN j.available_at ELSE NULL END
                FROM openconnect_knowledge.index_jobs j
                WHERE j.tenant_id=%s AND j.job_id=%s AND j.payload->>'applicationId'=%s
                  AND j.payload->>'collectionId'=%s AND j.payload->>'documentId'=%s
                  AND ((j.payload->'principals') ? %s OR (j.payload->'roles') ?| %s::text[])
                  AND (NOT EXISTS (SELECT 1 FROM openconnect_knowledge.documents d
                       WHERE d.tenant_id=j.tenant_id AND d.application_id=%s AND d.collection_id=%s AND d.document_id=%s)
                       OR EXISTS (SELECT 1 FROM openconnect_knowledge.documents d
                       WHERE d.tenant_id=j.tenant_id AND d.application_id=%s AND d.collection_id=%s AND d.document_id=%s
                         AND NOT d.deleted AND (d.expires_at IS NULL OR d.expires_at > statement_timestamp())
                         AND (%s = ANY(d.allowed_principals) OR d.allowed_roles && %s::text[])))""",
                (tenant_id, job_id, application_id, collection_id, document_id, principal_id, role_ids,
                 application_id, collection_id, document_id, application_id, collection_id, document_id,
                 principal_id, role_ids)).fetchone()
        return IndexJobStatus(*row) if row is not None else None

    def ready_tenants(self, *, limit: int = 64, embedding_space_id: str | None = None,
                      after: str | None = None) -> tuple[str, ...]:
        """Find a bounded tenant-ordered page of jobs in the requested model space."""
        if isinstance(limit, bool) or not isinstance(limit, int) or not 1 <= limit <= 256:
            raise ValueError("invalid_limit")
        _space_filter(embedding_space_id)
        if after is not None:
            _identifier(after)
        with psycopg.connect(self._dsn, connect_timeout=5) as connection:
            connection.execute("SET LOCAL statement_timeout = '5s'")
            rows = connection.execute("""SELECT tenant_id FROM openconnect_knowledge.index_jobs
                WHERE ((state='pending' AND available_at <= clock_timestamp()) OR
                      (state='leased' AND lease_until <= clock_timestamp()))
                  AND (%s::text IS NULL OR payload->>'embeddingSpaceId' = %s)
                  AND (%s::text IS NULL OR tenant_id > %s)
                GROUP BY tenant_id ORDER BY tenant_id LIMIT %s""",
                (embedding_space_id, embedding_space_id, after, after, limit)).fetchall()
        return tuple(row[0] for row in rows)

    def submit(self, job_id: UUID, job: IndexJob, *, require_worker: bool = False) -> bool:
        """Insert once; an identical replay returns False and a changed replay conflicts."""
        if not isinstance(job_id, UUID):
            raise ValueError("invalid_job")
        payload = _payload(job)
        digest = hashlib.sha256(json.dumps(payload, sort_keys=True, ensure_ascii=True,
                                           separators=(",", ":")).encode()).hexdigest()
        with psycopg.connect(self._dsn, connect_timeout=5) as connection:
            connection.execute("SET LOCAL statement_timeout = '5s'")
            if require_worker:
                existing = connection.execute("SELECT payload_sha256 FROM openconnect_knowledge.index_jobs "
                    "WHERE tenant_id=%s AND job_id=%s", (job.scope.tenant_id, job_id)).fetchone()
                if existing is not None:
                    if existing[0] != digest:
                        raise ValueError("job_conflict")
                    return False
                available = connection.execute("SELECT 1 FROM openconnect_knowledge.index_workers "
                    "WHERE embedding_space_id=%s AND expires_at > clock_timestamp() LIMIT 1",
                    (job.scope.embedding_space_id,)).fetchone()
                if available is None:
                    raise IndexWorkerUnavailable("capability_unavailable")
            row = connection.execute("""INSERT INTO openconnect_knowledge.index_jobs
                (tenant_id, job_id, payload, payload_sha256) VALUES (%s, %s, %s, %s)
                ON CONFLICT (tenant_id, job_id) DO NOTHING RETURNING job_id""",
                (job.scope.tenant_id, job_id, Jsonb(payload), digest)).fetchone()
            if row:
                return True
            existing = connection.execute("SELECT payload_sha256 FROM openconnect_knowledge.index_jobs "
                                          "WHERE tenant_id=%s AND job_id=%s", (job.scope.tenant_id, job_id)).fetchone()
            if existing is None or existing[0] != digest:
                raise ValueError("job_conflict")
            return False

    def claim(self, tenant_id: str, *, lease_seconds: int = 60,
              embedding_space_id: str | None = None) -> IndexClaim | None:
        _identifier(tenant_id)
        _space_filter(embedding_space_id)
        if isinstance(lease_seconds, bool) or not isinstance(lease_seconds, int) or not 1 <= lease_seconds <= 300:
            raise ValueError("invalid_lease")
        lease = uuid4()
        with psycopg.connect(self._dsn, connect_timeout=5) as connection:
            connection.execute("SET LOCAL statement_timeout = '5s'")
            connection.execute("""UPDATE openconnect_knowledge.index_jobs SET state='dead', lease_id=NULL, lease_until=NULL
                WHERE tenant_id=%s AND state='leased' AND lease_until <= clock_timestamp() AND attempts >= %s
                  AND (%s::text IS NULL OR payload->>'embeddingSpaceId' = %s)""",
                (tenant_id, MAX_ATTEMPTS, embedding_space_id, embedding_space_id))
            row = connection.execute("""WITH candidate AS (
                SELECT tenant_id,job_id FROM openconnect_knowledge.index_jobs
                WHERE tenant_id=%s AND attempts < %s AND (%s::text IS NULL OR payload->>'embeddingSpaceId' = %s) AND
                  ((state='pending' AND available_at <= clock_timestamp()) OR
                   (state='leased' AND lease_until <= clock_timestamp()))
                ORDER BY created_at, job_id FOR UPDATE SKIP LOCKED LIMIT 1
            ) UPDATE openconnect_knowledge.index_jobs j SET state='leased',lease_id=%s,
                lease_until=clock_timestamp()+(%s * interval '1 second'),attempts=j.attempts+1,
                fencing_token=j.fencing_token+1 FROM candidate c WHERE j.tenant_id=c.tenant_id AND j.job_id=c.job_id
                RETURNING j.job_id,j.fencing_token,j.attempts,j.payload""",
                (tenant_id, MAX_ATTEMPTS, embedding_space_id, embedding_space_id, lease, lease_seconds)).fetchone()
        return IndexClaim(tenant_id, row[0], lease, row[1], row[2], row[3]) if row else None

    def heartbeat(self, claim: IndexClaim, *, lease_seconds: int = 60) -> bool:
        if (not isinstance(claim, IndexClaim) or isinstance(lease_seconds, bool)
                or not isinstance(lease_seconds, int) or not 1 <= lease_seconds <= 300):
            raise ValueError("invalid_claim")
        with psycopg.connect(self._dsn, connect_timeout=5) as connection:
            connection.execute("SET LOCAL statement_timeout = '5s'")
            row = connection.execute("""UPDATE openconnect_knowledge.index_jobs SET
                lease_until=clock_timestamp()+(%s * interval '1 second')
                WHERE tenant_id=%s AND job_id=%s AND state='leased' AND lease_id=%s AND fencing_token=%s
                AND lease_until > clock_timestamp() RETURNING job_id""",
                (lease_seconds, claim.tenant_id, claim.job_id, claim.lease_id, claim.fencing_token)).fetchone()
        return row is not None

    def fail(self, claim: IndexClaim, *, terminal: bool = False) -> bool:
        """Schedule bounded retry or dead-letter a permanent failure under the live lease."""
        if not isinstance(claim, IndexClaim) or not isinstance(terminal, bool):
            raise ValueError("invalid_claim")
        with psycopg.connect(self._dsn, connect_timeout=5) as connection:
            connection.execute("SET LOCAL statement_timeout = '5s'")
            row = connection.execute("""UPDATE openconnect_knowledge.index_jobs SET
                state=CASE WHEN %s OR attempts >= %s THEN 'dead' ELSE 'pending' END,
                available_at=clock_timestamp()+(LEAST(300,power(2,LEAST(attempts,8))) * interval '1 second'),
                lease_id=NULL,lease_until=NULL WHERE tenant_id=%s AND job_id=%s AND state='leased'
                AND lease_id=%s AND fencing_token=%s AND lease_until > clock_timestamp() RETURNING job_id""",
                (terminal, MAX_ATTEMPTS, claim.tenant_id, claim.job_id, claim.lease_id, claim.fencing_token)).fetchone()
        return row is not None
