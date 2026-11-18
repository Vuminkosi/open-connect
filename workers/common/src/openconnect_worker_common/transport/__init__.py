"""Transport primitives shared by all workers: NATS, claims, commits, private streams."""

from openconnect_worker_common.transport.nats import (
    NatsSettings,
    NatsStreamConfig,
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

__all__ = [
    "NatsSettings",
    "NatsStreamConfig",
    "PayloadRef",
    "TaskClaim",
    "TaskOutcome",
    "TaskResult",
    "is_private_subject",
    "private_subject",
    "result_subject",
    "sanitize_token",
    "task_subject",
]
