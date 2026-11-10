"""Shared pytest fixtures for openconnect-worker-common."""

from datetime import datetime, timezone
from uuid import uuid4

import pytest

from openconnect_worker_common.contracts.envelope import Lease, WorkerEnvelope


@pytest.fixture()
def envelope_kwargs() -> dict:
    now = datetime.now(timezone.utc)
    return {
        "messageId": uuid4(),
        "taskId": "task-1",
        "kind": "security-scan",
        "issuedAt": now,
        "expiresAt": now,
        "lease": Lease(leaseId=uuid4(), fencingToken=1),
        "payloadRef": "openconnect.v1.private.task-1.worker-1#0",
    }


@pytest.fixture()
def envelope(envelope_kwargs: dict) -> WorkerEnvelope:
    from datetime import timedelta

    envelope_kwargs["expiresAt"] = envelope_kwargs["issuedAt"] + timedelta(minutes=5)
    return WorkerEnvelope(**envelope_kwargs)
