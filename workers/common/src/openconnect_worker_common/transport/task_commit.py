"""Task commit: worker result publication guarded by the claim's fencing token."""

from enum import StrEnum
from typing import Literal
from uuid import UUID

from pydantic import AwareDatetime, BaseModel, ConfigDict, Field, model_validator

from openconnect_worker_common.contracts.errors import WorkerError

SCHEMA_VERSION = "openconnect.v1"


class TaskOutcome(StrEnum):
    """Terminal task outcomes; wire values are kebab-case."""

    COMPLETED = "completed"
    FAILED = "failed"
    REJECTED = "rejected"


class TaskResult(BaseModel):
    """Result committed for a claimed task. Failed tasks must carry a safe error."""

    model_config = ConfigDict(extra="forbid")

    schemaVersion: Literal["openconnect.v1"] = SCHEMA_VERSION
    taskId: str = Field(min_length=1)
    leaseId: UUID
    fencingToken: int = Field(ge=1)
    outcome: TaskOutcome
    error: WorkerError | None = None
    completedAt: AwareDatetime
    payloadRef: str | None = Field(default=None, min_length=1, max_length=2048)

    @model_validator(mode="after")
    def _error_matches_outcome(self) -> "TaskResult":
        if self.outcome == TaskOutcome.FAILED and self.error is None:
            raise ValueError("failed tasks must include an error")
        if self.outcome == TaskOutcome.COMPLETED and self.error is not None:
            raise ValueError("completed tasks must not include an error")
        return self

    def is_commit_valid_for_claim(self, lease_id: UUID, current_fencing_token: int) -> bool:
        """Accept only commits that present the live lease id and a newer fencing token."""
        return self.leaseId == lease_id and self.fencingToken > current_fencing_token
