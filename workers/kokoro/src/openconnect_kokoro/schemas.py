"""Pydantic models for the Kokoro worker API."""

from typing import Literal
from uuid import UUID

from pydantic import BaseModel, ConfigDict, Field

EXECUTE_KINDS = ("speech.synthesize",)


class SynthesizePayload(BaseModel):
    model_config = ConfigDict(extra="forbid")

    schemaVersion: Literal["openconnect.v1"]
    text: str = Field(min_length=1, max_length=5000)
    voiceId: str | None = Field(default=None, max_length=64)
    locale: str = Field(default="en-US", max_length=16)
    format: Literal["wav", "mp3", "pcm"] = "wav"
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
