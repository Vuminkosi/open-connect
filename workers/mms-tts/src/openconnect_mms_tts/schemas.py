"""Pydantic models for the MMS TTS worker API."""

from typing import Literal
from uuid import UUID

from pydantic import BaseModel, ConfigDict, Field

EXECUTE_KINDS = ("mms.synthesize",)


class MmsSynthesizePayload(BaseModel):
    model_config = ConfigDict(extra="forbid")

    schemaVersion: Literal["openconnect.v1"]
    text: str = Field(min_length=1, max_length=5000)
    language: str = Field(min_length=2, max_length=8)
    speechEpoch: int | None = Field(default=None, ge=0)
    idempotencyKey: str | None = Field(default=None, max_length=128)


class ExecuteRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")

    schemaVersion: Literal["openconnect.v1"]
    taskId: str = Field(min_length=1, max_length=128)
    kind: str = Field(min_length=1, max_length=64)
    payload: dict = Field(default_factory=dict)
    idempotencyKey: str | None = Field(default=None, max_length=128)


class TaskClaimRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")

    taskId: str = Field(min_length=1, max_length=128)
    kind: str = Field(min_length=1, max_length=64)


class TaskLeaseRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")

    leaseId: UUID
    fencingToken: int = Field(ge=1)
    result: dict | None = None
    error: dict | None = None
