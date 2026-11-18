"""Pydantic models for the realtime-voice worker API."""

from typing import Literal
from uuid import UUID

from pydantic import BaseModel, ConfigDict, Field

VoiceState = Literal[
    "idle",
    "listening",
    "transcribing",
    "reasoning",
    "speaking",
    "interrupted",
    "cancelled",
    "failed",
]

VoiceEvent = Literal[
    "begin-listening",
    "vad-speech-start",
    "transcript-partial",
    "transcript-final",
    "begin-reasoning",
    "begin-speaking",
    "utterance-complete",
    "barge-in",
    "resume-listening",
    "cancel",
    "fail",
]

EXECUTE_KINDS = ("voice.session-open", "voice.event")


class SessionOpenRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")

    schemaVersion: Literal["openconnect.v1"]
    sessionId: UUID


class VoiceEventRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")

    schemaVersion: Literal["openconnect.v1"]
    sessionId: UUID
    type: VoiceEvent
    speechEpoch: int | None = Field(default=None, ge=0)
    text: str | None = Field(default=None, max_length=1500)
    voice: str | None = Field(default=None, max_length=64)
    language: str | None = Field(default=None, max_length=16)


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
