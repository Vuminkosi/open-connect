"""Private per-task stream helpers: subject naming and payload references.

Payload bytes travel on private reply subjects; only the opaque ``payloadRef``
(a bounded string) is carried inside envelopes and results.
"""

import re
from dataclasses import dataclass

from openconnect_worker_common.transport.nats import SUBJECT_PREFIX

_TOKEN_RE = re.compile(r"^[A-Za-z0-9_-]{1,128}$")
_MAX_PAYLOAD_REF_LENGTH = 2048


def sanitize_token(value: str) -> str:
    """Validate a task/worker id for safe embedding in a NATS subject."""
    if not _TOKEN_RE.match(value):
        raise ValueError(f"value is not a valid subject token: {value!r}")
    return value


def private_subject(task_id: str, worker_id: str) -> str:
    """Private subject for payload chunks of one task on one worker."""
    return f"{SUBJECT_PREFIX}.private.{sanitize_token(task_id)}.{sanitize_token(worker_id)}"


def is_private_subject(subject: str) -> bool:
    """Return True when the subject is under the private namespace."""
    return subject.startswith(f"{SUBJECT_PREFIX}.private.")


@dataclass(frozen=True)
class PayloadRef:
    """Opaque reference to payload bytes held outside the envelope."""

    value: str

    def __post_init__(self) -> None:
        if not self.value or len(self.value) > _MAX_PAYLOAD_REF_LENGTH:
            raise ValueError("payloadRef must be 1..2048 characters")

    @classmethod
    def for_private_subject(cls, subject: str, sequence: int) -> "PayloadRef":
        if not is_private_subject(subject):
            raise ValueError("payload reference must point at a private subject")
        if sequence < 0:
            raise ValueError("sequence must not be negative")
        return cls(value=f"{subject}#{sequence}")

    def __str__(self) -> str:
        return self.value
