"""Worker request envelope contract (contracts/jsonschema/worker-envelope.json)."""

from datetime import datetime, timezone
from typing import Literal
from uuid import UUID

from pydantic import AwareDatetime, BaseModel, ConfigDict, Field, model_validator

SCHEMA_VERSION = "openconnect.v1"


class Lease(BaseModel):
    """Task ownership lease; fencingToken must increase monotonically per task."""

    model_config = ConfigDict(extra="forbid")

    leaseId: UUID
    fencingToken: int = Field(ge=1)


class WorkerEnvelope(BaseModel):
    """Envelope wrapping a unit of worker-claimable work."""

    model_config = ConfigDict(extra="forbid")

    schemaVersion: Literal["openconnect.v1"] = SCHEMA_VERSION
    messageId: UUID
    taskId: str = Field(min_length=1)
    kind: str = Field(pattern=r"^[a-z][a-z0-9-]{0,62}$")
    issuedAt: AwareDatetime
    expiresAt: AwareDatetime
    lease: Lease
    payloadRef: str = Field(min_length=1, max_length=2048)
    correlationId: str | None = Field(default=None, max_length=128)

    @model_validator(mode="after")
    def _expiry_after_issue(self) -> "WorkerEnvelope":
        if self.expiresAt <= self.issuedAt:
            raise ValueError("expiresAt must be after issuedAt")
        return self

    def is_expired(self, now: datetime | None = None) -> bool:
        """Return True when the envelope is past its expiry (fail closed)."""
        current = now or datetime.now(timezone.utc)
        return current >= self.expiresAt
