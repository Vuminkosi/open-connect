"""Pydantic contracts for the inference worker private API."""

from typing import Literal
from uuid import UUID

from pydantic import BaseModel, ConfigDict, Field

_ALLOWED_CAPABILITIES = (
    "chat",
    "stream",
    "tools",
    "structured-output",
    "embeddings",
)


class ChatMessage(BaseModel):
    model_config = ConfigDict(extra="forbid")

    role: Literal["system", "user", "assistant", "tool"]
    content: str = Field(max_length=32_768)


class InferenceInput(BaseModel):
    model_config = ConfigDict(extra="forbid")

    messages: list[ChatMessage] = Field(min_length=1, max_length=64)
    maxTokens: int = Field(default=256, ge=1, le=4_096)
    stream: bool = False
    requestedCapabilities: list[
        Literal["chat", "stream", "tools", "structured-output", "embeddings"]
    ] = Field(default_factory=list, max_length=5)


class ExecuteRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")

    schemaVersion: Literal["openconnect.v1"]
    requestId: UUID
    input: InferenceInput
    idempotencyKey: UUID | None = None


class StreamChunk(BaseModel):
    model_config = ConfigDict(extra="forbid")

    seq: int = Field(ge=0)
    delta: str = Field(max_length=32_768)
    done: bool


class Usage(BaseModel):
    model_config = ConfigDict(extra="forbid")

    inputChars: int = Field(ge=0)
    outputChars: int = Field(ge=0)


class ExecuteResponse(BaseModel):
    model_config = ConfigDict(extra="forbid")

    schemaVersion: Literal["openconnect.v1"]
    requestId: UUID
    backend: str
    modelId: str | None = None
    output: str
    usage: Usage
    capabilitiesUsed: list[str] = Field(default_factory=list)
    chunks: list[StreamChunk] | None = None


class CapabilityResponse(BaseModel):
    model_config = ConfigDict(extra="forbid")

    worker: Literal["inference"]
    version: str
    backend: str
    engine: Literal["fake-bounded", "real"]
    modelId: str | None = None
    modelRevision: str | None = None
    modelLoaded: bool
    capabilities: list[str]
    privacyMode: Literal["strict-local"] = "strict-local"


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
