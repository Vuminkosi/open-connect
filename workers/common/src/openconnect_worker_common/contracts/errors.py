"""Stable, user-safe worker error contract.

Errors returned to clients must never contain stack traces, SQL, file paths,
credentials, or other internal detail. Developer diagnostics belong in
(redacted) server logs, not in error payloads.
"""

from enum import StrEnum

from pydantic import BaseModel, ConfigDict, Field


class ErrorCode(StrEnum):
    """Stable machine-readable error codes shared across workers."""

    TASK_NOT_FOUND = "task_not_found"
    SESSION_NOT_FOUND = "session_not_found"
    UNAUTHORIZED = "unauthorized"
    TOOL_NOT_ALLOWED = "tool_not_allowed"
    APPROVAL_REQUIRED = "approval_required"
    CAPABILITY_UNAVAILABLE = "capability_unavailable"
    PRIVACY_BLOCKED = "privacy_blocked"
    INVALID_REQUEST = "invalid_request"
    CONFLICT = "conflict"
    RATE_LIMITED = "rate_limited"


class WorkerError(BaseModel):
    """Safe, actionable error payload returned to API consumers."""

    model_config = ConfigDict(extra="forbid")

    code: ErrorCode
    message: str = Field(min_length=1, max_length=1024)
    correlationId: str | None = Field(default=None, max_length=128)
    retryable: bool = False

    def to_dict(self) -> dict[str, object]:
        """Serialize to the stable wire shape."""
        payload: dict[str, object] = {
            "code": self.code.value,
            "message": self.message,
            "retryable": self.retryable,
        }
        if self.correlationId is not None:
            payload["correlationId"] = self.correlationId
        return payload
