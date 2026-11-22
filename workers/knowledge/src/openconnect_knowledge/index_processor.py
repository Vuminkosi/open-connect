"""One bounded indexing attempt over an authorized, immutable object handle."""

from __future__ import annotations

import hashlib
from datetime import datetime, timezone
from enum import Enum
from typing import Protocol, Sequence

from openconnect_knowledge.chunking import DocumentScope, SourcePage
from openconnect_knowledge.index_jobs import IndexJob, PostgresIndexJobs, _payload, _space_filter
from openconnect_knowledge.index_preparation import PassageEmbedder, prepare_document
from openconnect_knowledge.postgres_index import IndexConflictError, IndexRetentionExpiredError, JobFence, PostgresKnowledgeIndex
from openconnect_worker_common.security.clamav import MalwareDetectedError


class DocumentResolver(Protocol):
    def read(self, tenant_id: str, object_id: str, version: str, size: int, sha256: str) -> bytes: ...


class Scanner(Protocol):
    def scan(self, content: bytes) -> None: ...


class DocumentParser(Protocol):
    def parse(self, tenant_id: str, content: bytes) -> Sequence[SourcePage]: ...


class IndexStatus(Enum):
    IDLE = "idle"
    COMPLETED = "completed"
    RETRY = "retry"
    FAILED = "failed"
    LEASE_LOST = "lease_lost"


class RetentionExpiredError(ValueError):
    """The authorized retention window has elapsed and cannot be retried."""


def _ensure_not_expired(expires: datetime | None) -> None:
    if expires is not None and expires <= datetime.now(timezone.utc):
        raise RetentionExpiredError("retention_expired")


def process_one(jobs: PostgresIndexJobs, index: PostgresKnowledgeIndex, tenant_id: str, *,
                resolver: DocumentResolver, scanner: Scanner, parser: DocumentParser,
                embedder: PassageEmbedder, embedding_space_id: str, lease_seconds: int = 60) -> IndexStatus:
    """Resolve, scan, parse and embed before atomically publishing under the live fence.

    The trusted host authorizes submission and configures a private MinIO
    resolver, ClamAV scanner and sandboxed parser. Dependencies must bound
    execution and never persist content in a task message or log.
    """
    if embedding_space_id is None:
        raise ValueError("invalid_embedding_space")
    _space_filter(embedding_space_id)
    claim = jobs.claim(tenant_id, lease_seconds=lease_seconds, embedding_space_id=embedding_space_id)
    if claim is None:
        return IndexStatus.IDLE
    # Never read a source object or acknowledge a lease from another tenant,
    # even if an outbox implementation returns an inconsistent claim.
    if claim.tenant_id != tenant_id:
        return IndexStatus.LEASE_LOST
    try:
        data = claim.payload
        scope = DocumentScope(data["tenantId"], data["applicationId"], data["collectionId"], data["documentId"],
                              data["sourceRevision"], data["embeddingSpaceId"])
        expires = datetime.fromisoformat(data["expiresAt"]) if data["expiresAt"] else None
        job = IndexJob(scope, data["objectId"], data["objectSha256"], data["objectSize"], data["objectVersion"],
                       data["expectedGeneration"], tuple(data["principals"]), tuple(data["roles"]), expires)
        if (_payload(job) != data or scope.tenant_id != tenant_id
                or scope.embedding_space_id != embedding_space_id):
            raise ValueError("invalid_job")
        _ensure_not_expired(expires)
        content = resolver.read(tenant_id, job.object_id, job.object_version, job.object_size, job.object_sha256)
        if (type(content) is not bytes or len(content) != job.object_size
                or hashlib.sha256(content).hexdigest() != job.object_sha256):
            raise ValueError("object_integrity_failed")
        if not jobs.heartbeat(claim, lease_seconds=lease_seconds):
            return IndexStatus.LEASE_LOST
        _ensure_not_expired(expires)
        scanner.scan(content)
        if not jobs.heartbeat(claim, lease_seconds=lease_seconds):
            return IndexStatus.LEASE_LOST
        _ensure_not_expired(expires)
        pages = parser.parse(tenant_id, content)
        if not jobs.heartbeat(claim, lease_seconds=lease_seconds):
            return IndexStatus.LEASE_LOST
        _ensure_not_expired(expires)
        document = prepare_document(scope, pages, embedder)
        if not jobs.heartbeat(claim, lease_seconds=lease_seconds):
            return IndexStatus.LEASE_LOST
        _ensure_not_expired(expires)
        try:
            index.upsert(document, expected_generation=job.expected_generation, principals=job.principals, roles=job.roles,
                         expires_at=job.expires_at, job_fence=JobFence(claim.job_id, claim.lease_id, claim.fencing_token))
        except IndexConflictError as exc:
            # These conflicts cannot be resolved by replaying the same immutable job.
            # A lost fence must never be treated as a successful or failed live claim.
            if exc.args == ("claim_expired",):
                return IndexStatus.LEASE_LOST
            if exc.args in (("generation_conflict",), ("revision_conflict",), ("claim_scope_mismatch",)):
                return IndexStatus.FAILED if jobs.fail(claim, terminal=True) else IndexStatus.LEASE_LOST
            raise
        return IndexStatus.COMPLETED
    except (RetentionExpiredError, IndexRetentionExpiredError, MalwareDetectedError):
        return IndexStatus.FAILED if jobs.fail(claim, terminal=True) else IndexStatus.LEASE_LOST
    except Exception:
        # Dependencies may include protected content or credentials in errors.
        return IndexStatus.RETRY if jobs.fail(claim) else IndexStatus.LEASE_LOST
