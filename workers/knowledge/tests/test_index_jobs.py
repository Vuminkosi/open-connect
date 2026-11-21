import os
import hashlib
import secrets
from datetime import datetime, timedelta, timezone
from concurrent.futures import ThreadPoolExecutor
from dataclasses import replace
from uuid import uuid4

import psycopg
import pytest
from psycopg.conninfo import conninfo_to_dict

from openconnect_knowledge.chunking import DocumentScope
from openconnect_knowledge.index_jobs import IndexJob, PostgresIndexJobs
from openconnect_knowledge.index_processor import IndexStatus, process_one
from openconnect_knowledge.postgres_index import IndexConflictError, JobFence, PostgresKnowledgeIndex, SearchScope
from test_postgres_index import Engine, projection

DSN = os.getenv("OC_TEST_KNOWLEDGE_PG_DSN")
pytestmark = pytest.mark.skipif(not DSN, reason="Set OC_TEST_KNOWLEDGE_PG_DSN for disposable PostgreSQL tests")


@pytest.fixture
def setup_job():
    settings = conninfo_to_dict(DSN)
    assert settings.get("dbname") == "openconnect_test" and settings.get("host") in {"127.0.0.1", "::1", "localhost"}
    tenant = "0" + "".join(secrets.choice("0123456789ABCDEFGHJKMNPQRSTVWXYZ") for _ in range(25))
    scope = DocumentScope(tenant, "app", "collection", "doc", "v1", "a" * 64)
    job = IndexJob(scope, "c" * 64, "d" * 64, 123, "version-id", 0, ("alice",))
    return PostgresIndexJobs(DSN), PostgresKnowledgeIndex(DSN), job


def fence(claim):
    return JobFence(claim.job_id, claim.lease_id, claim.fencing_token)


def test_submit_is_idempotent_and_cannot_change_payload(setup_job):
    jobs, _, job = setup_job
    job_id = uuid4()
    assert jobs.submit(job_id, job) and not jobs.submit(job_id, job)
    with pytest.raises(ValueError, match="job_conflict"):
        jobs.submit(job_id, replace(job, object_id="e" * 64))
    with psycopg.connect(DSN) as connection:
        payload = connection.execute("SELECT payload::text FROM openconnect_knowledge.index_jobs "
                                     "WHERE tenant_id=%s AND job_id=%s", (job.scope.tenant_id, job_id)).fetchone()[0]
    assert "forest" not in payload and job.object_id in payload


def test_worker_readiness_expires_and_can_be_retired(setup_job):
    jobs, _, _ = setup_job
    worker, space = uuid4(), hashlib.sha256(uuid4().bytes).hexdigest()
    jobs.record_worker(worker, space)
    assert space in jobs.live_worker_spaces()
    jobs.retire_worker(worker)
    assert space not in jobs.live_worker_spaces()
    jobs.record_worker(worker, space)
    with psycopg.connect(DSN) as connection:
        connection.execute("UPDATE openconnect_knowledge.index_workers SET expires_at=clock_timestamp()-interval '1 second' "
                           "WHERE worker_id=%s", (worker,))
    assert space not in jobs.live_worker_spaces()


def test_workers_claim_only_their_embedding_space(setup_job):
    jobs, _, original = setup_job
    space_a = hashlib.sha256(uuid4().bytes).hexdigest()
    space_b = hashlib.sha256(uuid4().bytes).hexdigest()
    job = replace(original, scope=replace(original.scope, embedding_space_id=space_a))
    other = replace(job, scope=replace(job.scope, document_id="other", embedding_space_id=space_b))
    first_id, other_id = uuid4(), uuid4()
    jobs.submit(first_id, job)
    jobs.submit(other_id, other)
    tenant = job.scope.tenant_id
    assert tenant in jobs.ready_tenants(embedding_space_id=job.scope.embedding_space_id)
    assert tenant in jobs.ready_tenants(embedding_space_id=other.scope.embedding_space_id)
    first = jobs.claim(tenant, embedding_space_id=job.scope.embedding_space_id)
    assert first.job_id == first_id and first.attempt == 1
    assert tenant not in jobs.ready_tenants(embedding_space_id=job.scope.embedding_space_id)
    assert tenant in jobs.ready_tenants(embedding_space_id=other.scope.embedding_space_id)
    assert jobs.claim(tenant, embedding_space_id=job.scope.embedding_space_id) is None
    with psycopg.connect(DSN) as connection:
        assert connection.execute("SELECT state, attempts FROM openconnect_knowledge.index_jobs "
                                  "WHERE tenant_id=%s AND job_id=%s", (tenant, other_id)).fetchone() == ("pending", 0)
    second = jobs.claim(tenant, embedding_space_id=other.scope.embedding_space_id)
    assert second.job_id == other_id and second.attempt == 1
    with psycopg.connect(DSN) as connection:
        connection.execute("UPDATE openconnect_knowledge.index_jobs SET attempts=8, "
                           "lease_until=clock_timestamp()-interval '1 second' WHERE tenant_id=%s AND job_id=%s",
                           (tenant, other_id))
    assert jobs.claim(tenant, embedding_space_id=job.scope.embedding_space_id) is None
    with psycopg.connect(DSN) as connection:
        assert connection.execute("SELECT state FROM openconnect_knowledge.index_jobs "
                                  "WHERE tenant_id=%s AND job_id=%s", (tenant, other_id)).fetchone()[0] == "leased"
    assert jobs.claim(tenant, embedding_space_id=other.scope.embedding_space_id) is None
    with psycopg.connect(DSN) as connection:
        assert connection.execute("SELECT state FROM openconnect_knowledge.index_jobs "
                                  "WHERE tenant_id=%s AND job_id=%s", (tenant, other_id)).fetchone()[0] == "dead"


def test_ready_tenant_pages_are_scoped_to_embedding_space(setup_job):
    jobs, _, original = setup_job
    space = hashlib.sha256(uuid4().bytes).hexdigest()
    prefix = original.scope.tenant_id[:-1]
    first, second = prefix + "0", prefix + "1"
    for tenant in (first, second):
        job = replace(original, scope=replace(original.scope, tenant_id=tenant, embedding_space_id=space))
        assert jobs.submit(uuid4(), job)

    assert jobs.ready_tenants(limit=1, embedding_space_id=space) == (first,)
    assert jobs.ready_tenants(limit=1, embedding_space_id=space, after=first) == (second,)
    assert not jobs.ready_tenants(limit=1, embedding_space_id=space, after=second)
    assert not jobs.ready_tenants(limit=1, embedding_space_id=hashlib.sha256(uuid4().bytes).hexdigest())


def test_concurrent_claims_and_atomic_index_completion(setup_job):
    jobs, index, job = setup_job
    job_id = uuid4()
    jobs.submit(job_id, job)
    with ThreadPoolExecutor(max_workers=2) as pool:
        claims = list(pool.map(lambda _: jobs.claim(job.scope.tenant_id), range(2)))
    assert sum(claim is not None for claim in claims) == 1
    claim = next(claim for claim in claims if claim is not None)
    assert jobs.heartbeat(claim)
    with pytest.raises(IndexConflictError, match="claim_scope_mismatch"):
        index.upsert(projection(job.scope), expected_generation=0, principals=["bob"], job_fence=fence(claim))
    assert index.upsert(projection(job.scope), expected_generation=0, principals=["alice"], job_fence=fence(claim)) == 1
    assert not jobs.heartbeat(claim)
    with psycopg.connect(DSN) as connection:
        row = connection.execute("SELECT state, result_generation FROM openconnect_knowledge.index_jobs "
                                 "WHERE tenant_id=%s AND job_id=%s", (job.scope.tenant_id, job_id)).fetchone()
    assert row == ("completed", 1)
    assert index.search(SearchScope(job.scope.tenant_id, "app", "collection", "alice"), "forest")


def test_expired_owner_cannot_publish_and_reclaimed_owner_can(setup_job):
    jobs, index, job = setup_job
    jobs.submit(uuid4(), job)
    first = jobs.claim(job.scope.tenant_id)
    with psycopg.connect(DSN) as connection:
        connection.execute("UPDATE openconnect_knowledge.index_jobs SET lease_until=clock_timestamp()-interval '1 second' "
                           "WHERE tenant_id=%s AND job_id=%s", (first.tenant_id, first.job_id))
    second = jobs.claim(job.scope.tenant_id)
    assert second.fencing_token == first.fencing_token + 1 and second.lease_id != first.lease_id
    assert not jobs.heartbeat(first) and not jobs.fail(first)
    with pytest.raises(IndexConflictError, match="claim_expired"):
        index.upsert(projection(job.scope), expected_generation=0, principals=["alice"], job_fence=fence(first))
    assert not index.search(SearchScope(job.scope.tenant_id, "app", "collection", "alice"), "forest")
    assert index.upsert(projection(job.scope), expected_generation=0, principals=["alice"], job_fence=fence(second)) == 1


def test_failure_retries_with_new_fence_and_backoff(setup_job):
    jobs, _, job = setup_job
    jobs.submit(uuid4(), job)
    first = jobs.claim(job.scope.tenant_id)
    assert jobs.fail(first) and jobs.claim(job.scope.tenant_id) is None
    with psycopg.connect(DSN) as connection:
        connection.execute("UPDATE openconnect_knowledge.index_jobs SET available_at=clock_timestamp() "
                           "WHERE tenant_id=%s AND job_id=%s", (first.tenant_id, first.job_id))
    second = jobs.claim(job.scope.tenant_id)
    assert second.fencing_token == first.fencing_token + 1 and second.attempt == 2


def test_status_is_scope_and_acl_filtered_and_observes_revocation(setup_job):
    jobs, index, job = setup_job
    job_id = uuid4()
    jobs.submit(job_id, job)
    scope = {"application_id": "app", "collection_id": "collection", "document_id": "doc"}
    def get(principal, **extra):
        return jobs.status(job.scope.tenant_id, job_id, principal_id=principal, **(scope | extra))
    assert get("alice").state == "pending" and get("bob") is None
    assert get("alice", application_id="other") is None
    assert jobs.status("other-tenant", job_id, principal_id="alice", **scope) is None
    claim = jobs.claim(job.scope.tenant_id)
    assert get("alice").state == "leased"
    assert index.upsert(projection(job.scope), expected_generation=0, principals=["alice"], job_fence=fence(claim)) == 1
    status = get("alice")
    assert status.state == "completed" and status.result_generation == 1
    index.upsert(projection(job.scope), expected_generation=1, principals=["bob"])
    assert get("alice") is None and get("bob") is None


def test_status_supports_authorized_roles_and_hides_deleted_documents(setup_job):
    jobs, index, original = setup_job
    job = replace(original, principals=(), roles=("reader",))
    job_id = uuid4()
    jobs.submit(job_id, job)
    scope = {"application_id": "app", "collection_id": "collection", "document_id": "doc", "principal_id": "bob"}
    assert jobs.status(job.scope.tenant_id, job_id, roles=("reader",), **scope).state == "pending"
    assert jobs.status(job.scope.tenant_id, job_id, roles=("other",), **scope) is None
    claim = jobs.claim(job.scope.tenant_id)
    assert index.upsert(projection(job.scope), expected_generation=0, principals=[], roles=["reader"],
                        job_fence=fence(claim)) == 1
    assert jobs.status(job.scope.tenant_id, job_id, roles=("reader",), **scope).state == "completed"
    index.delete(job.scope, expected_generation=1)
    assert jobs.status(job.scope.tenant_id, job_id, roles=("reader",), **scope) is None


def test_processor_scans_and_atomically_publishes_authorized_object(setup_job):
    from openconnect_knowledge.chunking import SourcePage

    jobs, index, original = setup_job
    content = b"forest document"
    job = replace(original, object_size=len(content), object_sha256=hashlib.sha256(content).hexdigest())
    jobs.submit(uuid4(), job)
    observed = []

    class Resolver:
        def read(self, tenant_id, object_id, version, size, sha256):
            observed.append(("read", tenant_id, object_id, version, size, sha256))
            return content

    class Scanner:
        def scan(self, data):
            assert data == content
            observed.append(("scan",))

    class Parser:
        def parse(self, tenant_id, data):
            assert tenant_id == job.scope.tenant_id
            assert observed[-1] == ("scan",) and data == content
            observed.append(("parse",))
            return [SourcePage("page", "forest document")]

    result = process_one(jobs, index, job.scope.tenant_id, resolver=Resolver(), scanner=Scanner(),
                         parser=Parser(), embedder=Engine(), embedding_space_id="a" * 64)
    assert result is IndexStatus.COMPLETED
    assert observed[0][1:] == (job.scope.tenant_id, job.object_id, job.object_version, len(content), job.object_sha256)
    assert [item[0] for item in observed] == ["read", "scan", "parse"]
    assert index.search(SearchScope(job.scope.tenant_id, "app", "collection", "alice"), "forest")


def test_database_rejects_publication_after_retention_expiry(setup_job):
    _, index, job = setup_job
    with pytest.raises(IndexConflictError, match="retention_expired"):
        index.upsert(projection(job.scope), expected_generation=0, principals=["alice"],
                     expires_at=datetime.now(timezone.utc) - timedelta(seconds=1))
    assert not index.search(SearchScope(job.scope.tenant_id, "app", "collection", "alice"), "forest")


def test_processor_does_not_scan_or_index_mismatched_object(setup_job):
    jobs, index, job = setup_job
    jobs.submit(uuid4(), job)

    class Resolver:
        def read(self, *args):
            return b"not the authorized object"

    class NeverCalled:
        def scan(self, data):
            pytest.fail("invalid object reached scanner")

        def parse(self, tenant_id, data):
            pytest.fail("invalid object reached parser")

    assert process_one(jobs, index, job.scope.tenant_id, resolver=Resolver(), scanner=NeverCalled(),
                       parser=NeverCalled(), embedder=Engine(), embedding_space_id="a" * 64) is IndexStatus.RETRY
    assert not index.search(SearchScope(job.scope.tenant_id, "app", "collection", "alice"), "forest")


def test_processor_rejects_wrong_space_before_reading_content(setup_job, monkeypatch):
    jobs, index, original = setup_job
    other = replace(original, scope=replace(original.scope, embedding_space_id="b" * 64))
    jobs.submit(uuid4(), other)
    original_claim = jobs.claim
    # Simulate an incorrectly routed claim despite the database filter.
    monkeypatch.setattr(jobs, "claim", lambda tenant, **kwargs: original_claim(tenant))

    class NeverCalled:
        def read(self, *args):
            pytest.fail("wrong-space job reached object storage")

    assert process_one(jobs, index, other.scope.tenant_id, resolver=NeverCalled(), scanner=NeverCalled(),
                       parser=NeverCalled(), embedder=Engine(), embedding_space_id="a" * 64) is IndexStatus.RETRY


def test_processor_rejects_scanner_failure_before_parsing(setup_job):
    jobs, index, original = setup_job
    content = b"malware-test-fixture"
    job = replace(original, object_size=len(content), object_sha256=hashlib.sha256(content).hexdigest())
    jobs.submit(uuid4(), job)

    class Resolver:
        def read(self, *args):
            return content

    class Scanner:
        def scan(self, data):
            raise RuntimeError("sensitive scanner detail")

    class Parser:
        def parse(self, tenant_id, data):
            pytest.fail("unscanned object reached parser")

    assert process_one(jobs, index, job.scope.tenant_id, resolver=Resolver(), scanner=Scanner(),
                       parser=Parser(), embedder=Engine(), embedding_space_id="a" * 64) is IndexStatus.RETRY
    assert not index.search(SearchScope(job.scope.tenant_id, "app", "collection", "alice"), "forest")


def test_terminal_job_failure_is_dead_lettered_under_live_fence(setup_job):
    jobs, _, job = setup_job
    job_id = uuid4()
    jobs.submit(job_id, job)
    claim = jobs.claim(job.scope.tenant_id)
    assert jobs.fail(claim, terminal=True)
    assert jobs.claim(job.scope.tenant_id) is None
    with psycopg.connect(DSN) as connection:
        state = connection.execute("SELECT state, lease_id, lease_until FROM openconnect_knowledge.index_jobs "
                                   "WHERE tenant_id=%s AND job_id=%s", (job.scope.tenant_id, job_id)).fetchone()
    assert state == ("dead", None, None)
    assert not jobs.fail(claim, terminal=True)


def test_processor_loses_lease_during_scan_and_never_publishes(setup_job):
    jobs, index, original = setup_job
    content = b"forest document"
    job = replace(original, object_size=len(content), object_sha256=hashlib.sha256(content).hexdigest())
    job_id = uuid4()
    jobs.submit(job_id, job)

    class Resolver:
        def read(self, *args):
            return content

    class Scanner:
        def scan(self, data):
            with psycopg.connect(DSN) as connection:
                connection.execute("UPDATE openconnect_knowledge.index_jobs SET lease_until=clock_timestamp()-interval '1 second' "
                                   "WHERE tenant_id=%s AND job_id=%s", (job.scope.tenant_id, job_id))

    class Parser:
        def parse(self, tenant_id, data):
            pytest.fail("expired lease reached parser")

    assert process_one(jobs, index, job.scope.tenant_id, resolver=Resolver(), scanner=Scanner(),
                       parser=Parser(), embedder=Engine(), embedding_space_id="a" * 64) is IndexStatus.LEASE_LOST
    assert not index.search(SearchScope(job.scope.tenant_id, "app", "collection", "alice"), "forest")
