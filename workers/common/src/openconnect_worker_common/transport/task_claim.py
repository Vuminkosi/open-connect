"""Task claim: exactly-once-oriented lease acquisition with fencing tokens."""

from datetime import datetime, timezone
from uuid import UUID

from pydantic import AwareDatetime, BaseModel, ConfigDict, Field

from openconnect_worker_common.contracts.envelope import WorkerEnvelope


class TaskClaim(BaseModel):
    """A worker's held claim on a task. Stale fencing tokens must be rejected."""

    model_config = ConfigDict(extra="forbid")

    taskId: str = Field(min_length=1)
    leaseId: UUID
    fencingToken: int = Field(ge=1)
    workerId: str = Field(min_length=1, max_length=256)
    claimedAt: AwareDatetime
    expiresAt: AwareDatetime

    def is_expired(self, now: datetime | None = None) -> bool:
        current = now or datetime.now(timezone.utc)
        return current >= self.expiresAt

    @staticmethod
    def is_fencing_token_valid(incoming: int, current: int) -> bool:
        """A commit is valid only when its fencing token strictly increases."""
        return incoming > current

    @classmethod
    def from_envelope(cls, envelope: WorkerEnvelope, worker_id: str) -> "TaskClaim":
        """Adopt the lease carried by an envelope. Rejects expired envelopes (fail closed)."""
        if envelope.is_expired():
            raise ValueError("cannot claim an expired envelope")
        if not worker_id:
            raise ValueError("worker_id must not be empty")
        return cls(
            taskId=envelope.taskId,
            leaseId=envelope.lease.leaseId,
            fencingToken=envelope.lease.fencingToken,
            workerId=worker_id,
            claimedAt=envelope.issuedAt,
            expiresAt=envelope.expiresAt,
        )
