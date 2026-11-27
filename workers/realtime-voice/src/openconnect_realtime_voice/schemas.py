"""Pydantic models for the realtime-voice worker API."""

from typing import Literal
from uuid import UUID

from pydantic import BaseModel, ConfigDict, Field

from openconnect_realtime_voice.observations import (
    ConnectionState,
    EndpointingMode,
    InputState,
    OutputState,
    ReasoningState,
)

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

EXECUTE_KINDS = (
    "voice.session-open",
    "voice.event",
    "voice.observe",
    "voice.endpoint.evaluate",
)


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


class VoiceObserveRequest(BaseModel):
    """Concurrent observation update bound to gateway-issued epoch and capture."""

    model_config = ConfigDict(extra="forbid")

    schemaVersion: Literal["openconnect.v1", "openconnect.v2"] = "openconnect.v2"
    tenantId: str = Field(min_length=1, max_length=128)
    sessionId: str | None = Field(default=None, min_length=1, max_length=128)
    speechEpoch: int = Field(ge=0)
    captureId: str | None = Field(default=None, min_length=1, max_length=128)
    connectionState: ConnectionState | None = None
    inputState: InputState | None = None
    reasoningState: ReasoningState | None = None
    outputState: OutputState | None = None
    silenceMs: int | None = Field(default=None, ge=0, le=600_000)
    speechDurationMs: int | None = Field(default=None, ge=0, le=600_000)
    transcript: str | None = Field(default=None, max_length=4000)
    stableText: str | None = Field(default=None, max_length=4000)
    unstableText: str | None = Field(default=None, max_length=4000)
    transcriptRevision: int | None = Field(default=None, ge=0)
    assistantSpeaking: bool | None = None
    endpointingMode: EndpointingMode = "adaptive"
    evaluateEndpoint: bool = True


class EndpointEvaluateRequest(BaseModel):
    """Non-authoritative adaptive endpoint evaluation request."""

    model_config = ConfigDict(extra="forbid")

    schemaVersion: Literal["openconnect.v1", "openconnect.v2"] = "openconnect.v2"
    tenantId: str = Field(min_length=1, max_length=128)
    sessionId: str = Field(min_length=1, max_length=128)
    speechEpoch: int = Field(ge=0)
    captureId: str | None = Field(default=None, min_length=1, max_length=128)
    silenceMs: int = Field(ge=0, le=600_000)
    speechDurationMs: int | None = Field(default=None, ge=0, le=600_000)
    transcript: str = Field(default="", max_length=4000)
    stableText: str | None = Field(default=None, max_length=4000)
    unstableText: str | None = Field(default=None, max_length=4000)
    transcriptRevision: int | None = Field(default=None, ge=0)
    assistantSpeaking: bool | None = None
    outputState: OutputState | None = None
    endpointingMode: EndpointingMode = "adaptive"
