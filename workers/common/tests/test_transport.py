"""Transport tests: NATS subjects, claims, commits, private streams."""

from datetime import timedelta
from uuid import uuid4

import pytest

from openconnect_worker_common.contracts.errors import WorkerError
from openconnect_worker_common.transport.nats import (
    NatsSettings,
    result_subject,
    task_subject,
)
from openconnect_worker_common.transport.private_stream import (
    PayloadRef,
    is_private_subject,
    private_subject,
    sanitize_token,
)
from openconnect_worker_common.transport.task_claim import TaskClaim
from openconnect_worker_common.transport.task_commit import TaskOutcome, TaskResult


def test_subject_builders() -> None:
    assert task_subject("security-scan") == "openconnect.v1.tasks.security-scan"
    assert result_subject("task-1") == "openconnect.v1.results.task-1"


def test_subject_builders_reject_wildcards() -> None:
    for bad in ("a b", "a.b", "a*b", "a>b", ""):
        with pytest.raises(ValueError):
            task_subject(bad)
        with pytest.raises(ValueError):
            sanitize_token(bad)


def test_nats_settings_validation() -> None:
    assert NatsSettings().validated().url.startswith("nats://")
    with pytest.raises(ValueError):
        NatsSettings(url="http://x").validated()
    with pytest.raises(ValueError):
        NatsSettings(connect_timeout_seconds=0).validated()
    with pytest.raises(ValueError):
        NatsSettings(max_reconnect_attempts=-1).validated()


def test_claim_from_envelope(envelope) -> None:
    claim = TaskClaim.from_envelope(envelope, "worker-1")
    assert claim.taskId == envelope.taskId
    assert claim.fencingToken == envelope.lease.fencingToken
    assert not claim.is_expired()


def test_claim_rejects_expired_envelope(envelope) -> None:
    envelope.expiresAt = envelope.issuedAt - timedelta(seconds=1)
    with pytest.raises(ValueError):
        TaskClaim.from_envelope(envelope, "worker-1")


def test_fencing_token_must_increase() -> None:
    assert TaskClaim.is_fencing_token_valid(2, 1) is True
    assert TaskClaim.is_fencing_token_valid(1, 1) is False
    assert TaskClaim.is_fencing_token_valid(0, 1) is False


def test_failed_result_requires_safe_error(envelope) -> None:
    error = WorkerError(code="privacy_blocked", message="blocked")
    failed = TaskResult(
        taskId=envelope.taskId,
        leaseId=envelope.lease.leaseId,
        fencingToken=2,
        outcome=TaskOutcome.FAILED,
        error=error,
        completedAt=envelope.issuedAt,
    )
    assert failed.error is not None
    with pytest.raises(Exception):
        TaskResult(
            taskId=envelope.taskId,
            leaseId=envelope.lease.leaseId,
            fencingToken=2,
            outcome=TaskOutcome.FAILED,
            completedAt=envelope.issuedAt,
        )


def test_completed_result_forbids_error(envelope) -> None:
    error = WorkerError(code="conflict", message="stale")
    with pytest.raises(Exception):
        TaskResult(
            taskId=envelope.taskId,
            leaseId=envelope.lease.leaseId,
            fencingToken=2,
            outcome=TaskOutcome.COMPLETED,
            error=error,
            completedAt=envelope.issuedAt,
        )


def test_commit_validity_checks_lease_and_fencing(envelope) -> None:
    result = TaskResult(
        taskId=envelope.taskId,
        leaseId=envelope.lease.leaseId,
        fencingToken=2,
        outcome=TaskOutcome.COMPLETED,
        completedAt=envelope.issuedAt,
    )
    assert result.is_commit_valid_for_claim(envelope.lease.leaseId, 1) is True
    assert result.is_commit_valid_for_claim(envelope.lease.leaseId, 2) is False
    assert result.is_commit_valid_for_claim(uuid4(), 1) is False


def test_private_subjects() -> None:
    subject = private_subject("task-1", "worker-1")
    assert subject == "openconnect.v1.private.task-1.worker-1"
    assert is_private_subject(subject) is True
    assert is_private_subject("openconnect.v1.tasks.security-scan") is False
    ref = PayloadRef.for_private_subject(subject, 3)
    assert str(ref).endswith("#3")
    with pytest.raises(ValueError):
        PayloadRef.for_private_subject("openconnect.v1.tasks.x", 0)
    with pytest.raises(ValueError):
        PayloadRef(value="")
