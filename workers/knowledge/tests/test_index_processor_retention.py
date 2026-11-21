from datetime import datetime, timedelta, timezone
import hashlib
from types import SimpleNamespace
from uuid import uuid4

import pytest

from openconnect_knowledge.chunking import DocumentScope
from openconnect_knowledge.index_jobs import IndexJob, _payload
from openconnect_knowledge.index_processor import IndexStatus, process_one
from openconnect_knowledge import index_processor
from openconnect_knowledge.postgres_index import IndexConflictError, IndexRetentionExpiredError
from openconnect_worker_common.security.clamav import MalwareDetectedError, MalwareScanUnavailableError


def test_foreign_tenant_claim_never_reads_or_acknowledges():
    tenant = "01ARZ3NDEKTSV4RRFFQ69G5FAV"

    class Jobs:
        def claim(self, *args, **kwargs):
            return SimpleNamespace(tenant_id="other-tenant")

        def fail(self, *args, **kwargs):
            pytest.fail("foreign lease was acknowledged")

    class Never:
        def __getattr__(self, name):
            pytest.fail(f"foreign claim reached {name}")

    assert process_one(Jobs(), Never(), tenant, resolver=Never(), scanner=Never(), parser=Never(),
                       embedder=Never(), embedding_space_id="a" * 64) is IndexStatus.LEASE_LOST


def test_expired_job_does_not_read_source_or_publish():
    tenant = "01ARZ3NDEKTSV4RRFFQ69G5FAV"
    scope = DocumentScope(tenant, "app", "collection", "doc", "v1", "a" * 64)
    job = IndexJob(scope, "b" * 64, "c" * 64, 123, "version-id", 0, ("alice",),
                   expires_at=datetime.now(timezone.utc) - timedelta(seconds=1))
    claim = SimpleNamespace(tenant_id=tenant, payload=_payload(job), job_id=uuid4(), lease_id=uuid4(), fencing_token=1)

    class Jobs:
        def claim(self, *args, **kwargs):
            return claim

        def fail(self, value, *, terminal=False):
            assert value is claim
            assert terminal
            return True

    accessed = []

    class Never:
        def __getattr__(self, name):
            accessed.append(name)
            raise AssertionError(f"expired job accessed {name}")

    assert process_one(Jobs(), Never(), tenant, resolver=Never(), scanner=Never(), parser=Never(),
                       embedder=Never(), embedding_space_id="a" * 64) is IndexStatus.FAILED
    assert accessed == []


def test_expiry_during_source_read_prevents_further_processing(monkeypatch):
    tenant = "01ARZ3NDEKTSV4RRFFQ69G5FAV"
    scope = DocumentScope(tenant, "app", "collection", "doc", "v1", "a" * 64)
    content = b"private document"
    start = datetime.now(timezone.utc)
    job = IndexJob(scope, "b" * 64, hashlib.sha256(content).hexdigest(), len(content), "version-id", 0,
                   ("alice",), expires_at=start + timedelta(seconds=1))
    claim = SimpleNamespace(tenant_id=tenant, payload=_payload(job), job_id=uuid4(), lease_id=uuid4(), fencing_token=1)
    clock = [start]

    class Clock(datetime):
        @classmethod
        def now(cls, tz=None):
            return clock[0]

    class Jobs:
        def claim(self, *args, **kwargs):
            return claim

        def heartbeat(self, *args, **kwargs):
            return True

        def fail(self, value, *, terminal=False):
            assert terminal
            return True

    class Resolver:
        def read(self, *args):
            clock[0] += timedelta(seconds=2)
            return content

    accessed = []

    class Never:
        def __getattr__(self, name):
            accessed.append(name)
            raise AssertionError(name)

    monkeypatch.setattr(index_processor, "datetime", Clock)
    assert process_one(Jobs(), Never(), tenant, resolver=Resolver(), scanner=Never(), parser=Never(),
                       embedder=Never(), embedding_space_id="a" * 64) is IndexStatus.FAILED
    assert accessed == []


def test_noncanonical_byte_object_never_reaches_scanner_or_parser():
    tenant = "01ARZ3NDEKTSV4RRFFQ69G5FAV"
    scope = DocumentScope(tenant, "app", "collection", "doc", "v1", "a" * 64)
    content = b"private document"
    job = IndexJob(scope, "b" * 64, hashlib.sha256(content).hexdigest(), len(content), "version-id", 0, ("alice",))
    claim = SimpleNamespace(tenant_id=tenant, payload=_payload(job), job_id=uuid4(), lease_id=uuid4(), fencing_token=1)

    class Jobs:
        def claim(self, *args, **kwargs):
            return claim

        def heartbeat(self, *args, **kwargs):
            accessed.append("heartbeat")
            return True

        def fail(self, value):
            return True

    class ByteSubclass(bytes):
        pass

    class Resolver:
        def read(self, *args):
            return ByteSubclass(content)

    accessed = []

    class Never:
        def __getattr__(self, name):
            accessed.append(name)
            raise AssertionError(f"unexpected processing: {name}")

    assert process_one(Jobs(), Never(), tenant, resolver=Resolver(), scanner=Never(), parser=Never(),
                       embedder=Never(), embedding_space_id="a" * 64) is IndexStatus.RETRY
    assert accessed == []


def test_detected_malware_is_terminal_but_scanner_outage_is_retried():
    tenant = "01ARZ3NDEKTSV4RRFFQ69G5FAV"
    scope = DocumentScope(tenant, "app", "collection", "doc", "v1", "a" * 64)
    content = b"synthetic document"
    job = IndexJob(scope, "b" * 64, hashlib.sha256(content).hexdigest(), len(content), "version-id", 0, ("alice",))
    claim = SimpleNamespace(tenant_id=tenant, payload=_payload(job), job_id=uuid4(), lease_id=uuid4(), fencing_token=1)

    class Jobs:
        terminal = None

        def claim(self, *args, **kwargs):
            return claim

        def heartbeat(self, *args, **kwargs):
            return True

        def fail(self, value, *, terminal=False):
            self.terminal = terminal
            return True

    class Resolver:
        def read(self, *args):
            return content

    class Scanner:
        def __init__(self, error):
            self.error = error

        def scan(self, data):
            raise self.error("safe error")

    for error, expected, terminal in [(MalwareDetectedError, IndexStatus.FAILED, True),
                                       (MalwareScanUnavailableError, IndexStatus.RETRY, False)]:
        jobs = Jobs()
        result = process_one(jobs, object(), tenant, resolver=Resolver(), scanner=Scanner(error), parser=object(),
                             embedder=object(), embedding_space_id="a" * 64)
        assert result is expected and jobs.terminal is terminal


def test_database_retention_deadline_is_a_terminal_failure(monkeypatch):
    tenant = "01ARZ3NDEKTSV4RRFFQ69G5FAV"
    scope = DocumentScope(tenant, "app", "collection", "doc", "v1", "a" * 64)
    content = b"synthetic document"
    job = IndexJob(scope, "b" * 64, hashlib.sha256(content).hexdigest(), len(content), "version-id", 0, ("alice",),
                   expires_at=datetime.now(timezone.utc) + timedelta(hours=1))
    claim = SimpleNamespace(tenant_id=tenant, payload=_payload(job), job_id=uuid4(), lease_id=uuid4(), fencing_token=1)

    class Jobs:
        def claim(self, *args, **kwargs):
            return claim

        def heartbeat(self, *args, **kwargs):
            return True

        def fail(self, value, *, terminal=False):
            assert value is claim and terminal
            return True

    class Resolver:
        def read(self, *args):
            return content

    class Scanner:
        def scan(self, data):
            assert data == content

    class Parser:
        def parse(self, tenant_id, data):
            return []

    class Index:
        def upsert(self, *args, **kwargs):
            raise IndexRetentionExpiredError("retention_expired")

    monkeypatch.setattr(index_processor, "prepare_document", lambda *args: object())
    assert process_one(Jobs(), Index(), tenant, resolver=Resolver(), scanner=Scanner(), parser=Parser(),
                       embedder=object(), embedding_space_id="a" * 64) is IndexStatus.FAILED


@pytest.mark.parametrize("reason,finish,status,terminal", [
    ("generation_conflict", True, IndexStatus.FAILED, True),
    ("generation_conflict", False, IndexStatus.LEASE_LOST, True),
    ("revision_conflict", True, IndexStatus.FAILED, True),
    ("claim_scope_mismatch", True, IndexStatus.FAILED, True),
    ("claim_expired", True, IndexStatus.LEASE_LOST, None),
    ("other_conflict", True, IndexStatus.RETRY, False),
])
def test_index_conflicts_are_classified_without_replaying_stale_jobs(monkeypatch, reason, finish, status, terminal):
    tenant = "01ARZ3NDEKTSV4RRFFQ69G5FAV"
    scope = DocumentScope(tenant, "app", "collection", "doc", "v1", "a" * 64)
    content = b"synthetic document"
    job = IndexJob(scope, "b" * 64, hashlib.sha256(content).hexdigest(), len(content), "version-id", 0, ("alice",))
    claim = SimpleNamespace(tenant_id=tenant, payload=_payload(job), job_id=uuid4(), lease_id=uuid4(), fencing_token=1)

    class Jobs:
        observed = None
        def claim(self, *args, **kwargs):
            return claim
        def heartbeat(self, *args, **kwargs):
            return True
        def fail(self, value, *, terminal=False):
            assert value is claim
            self.observed = terminal
            return finish

    class Resolver:
        def read(self, *args):
            return content

    class Scanner:
        def scan(self, data):
            pass

    class Parser:
        def parse(self, tenant_id, data):
            return []

    class Index:
        def upsert(self, *args, **kwargs):
            raise IndexConflictError(reason)

    monkeypatch.setattr(index_processor, "prepare_document", lambda *args: object())
    jobs = Jobs()
    assert process_one(jobs, Index(), tenant, resolver=Resolver(), scanner=Scanner(), parser=Parser(),
                       embedder=object(), embedding_space_id="a" * 64) is status
    assert jobs.observed is terminal
