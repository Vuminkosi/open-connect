"""Pydantic contracts for the transcription worker private API."""

from typing import Literal
from uuid import UUID

from pydantic import BaseModel, ConfigDict, Field

AudioMime = Literal[
    "audio/wav",
    "audio/x-wav",
    "audio/vnd.wave",
    "audio/wave",
    "audio/L16",
    "audio/pcm",
]

WAV_MIMES = frozenset({"audio/wav", "audio/x-wav", "audio/vnd.wave", "audio/wave"})
PCM_MIMES = frozenset({"audio/L16", "audio/pcm"})

ALLOWED_SAMPLE_RATES = frozenset({8_000, 16_000, 22_050, 44_100, 48_000})


class TranscribeInput(BaseModel):
    model_config = ConfigDict(extra="forbid")

    # Inline base64 PCM/WAV. Bounded; audio bytes are never logged or
    # placed on NATS - only metadata (length, mime, rate) is logged.
    audioBase64: str = Field(min_length=44, max_length=7_000_000)
    mimeType: AudioMime
    sampleRateHz: int = Field(ge=8_000, le=48_000)
    channels: int = Field(ge=1, le=2)
    bitsPerSample: Literal[8, 16, 24, 32] = 16
    durationMs: int | None = Field(default=None, ge=1, le=3_600_000)
    locale: str = Field(default="en", min_length=2, max_length=8, pattern=r"^[a-z]{2}(-[A-Z]{2})?$")
    partialsRequested: bool = False


class ExecuteRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")

    schemaVersion: Literal["openconnect.v1"]
    requestId: UUID
    input: TranscribeInput
    idempotencyKey: UUID | None = None


class TranscriptSegment(BaseModel):
    model_config = ConfigDict(extra="forbid")

    startMs: int = Field(ge=0)
    endMs: int = Field(ge=1)
    text: str = Field(max_length=8_192)


class ExecuteResponse(BaseModel):
    model_config = ConfigDict(extra="forbid")

    schemaVersion: Literal["openconnect.v1"]
    requestId: UUID
    backend: str
    modelId: str | None = None
    transcript: str
    revision: int = Field(ge=1)
    isFinal: bool
    locale: str
    durationMs: int = Field(ge=0)
    segments: list[TranscriptSegment] = Field(max_length=256)


class CapabilityResponse(BaseModel):
    model_config = ConfigDict(extra="forbid")

    worker: Literal["transcription"]
    version: str
    backend: str
    engine: Literal["fake-bounded"]
    modelId: str | None = None
    modelRevision: str | None = None
    modelLoaded: bool
    capabilities: list[str]
    privacyMode: str
    remoteBlocked: bool
    supportsPartials: bool
    sampleRatesHz: list[int]
    maxDurationMs: int


class TaskClaimRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")

    taskId: UUID | None = None
    leaseMs: int = Field(default=30_000, ge=1_000, le=300_000)


class TaskClaimResponse(BaseModel):
    model_config = ConfigDict(extra="forbid")

    taskId: UUID
    leaseId: UUID
    leaseExpiresAtMs: int
    state: Literal["claimed"]


class TaskHeartbeatRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")

    leaseId: UUID
    leaseMs: int = Field(default=30_000, ge=1_000, le=300_000)


class TaskHeartbeatResponse(BaseModel):
    model_config = ConfigDict(extra="forbid")

    taskId: UUID
    leaseId: UUID
    leaseExpiresAtMs: int
    state: Literal["claimed"]


class TaskCompleteRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")

    leaseId: UUID
    resultSummary: str = Field(default="", max_length=4_096)


class TaskFailRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")

    leaseId: UUID
    error: str = Field(min_length=1, max_length=256)


class TaskTerminalResponse(BaseModel):
    model_config = ConfigDict(extra="forbid")

    taskId: UUID
    state: Literal["completed", "failed"]
