"""Pydantic contracts for the classification worker."""

from typing import Literal

from pydantic import BaseModel, ConfigDict, Field, model_validator

StrictModel = ConfigDict(extra="forbid")


class ClassifyInput(BaseModel):
    """Exactly one of text / inputRef must be supplied (ref fetched host-side)."""

    model_config = StrictModel

    text: str | None = Field(default=None, max_length=20000)
    inputRef: str | None = Field(default=None, max_length=2048)
    labelSetVersion: str = Field(default="v1", max_length=32)
    localeHint: str | None = Field(default=None, max_length=16)
    threshold: float = Field(default=0.5, ge=0.0, le=1.0)
    maxLabels: int = Field(default=3, ge=1, le=10)

    @model_validator(mode="after")
    def _require_text_or_ref(self) -> "ClassifyInput":
        if not self.text and not self.inputRef:
            raise ValueError("one of text or inputRef is required")
        if self.text and self.inputRef:
            raise ValueError("only one of text or inputRef may be set")
        return self


class ClassifyResult(BaseModel):
    model_config = StrictModel

    labels: list[str]
    scores: dict[str, float]
    abstained: bool
    modelRevision: str
    labelSetVersion: str
    engine: Literal["fake", "real"]
    engineWarning: str | None = None
    grantsTools: Literal[False] = False


class ExecuteRequest(BaseModel):
    model_config = StrictModel

    schemaVersion: Literal["openconnect.v1"] = "openconnect.v1"
    taskId: str = Field(min_length=1, max_length=128)
    leaseId: str | None = Field(default=None, max_length=128)
    input: ClassifyInput


class ErrorBody(BaseModel):
    model_config = StrictModel

    code: str
    message: str


class ExecuteResponse(BaseModel):
    model_config = StrictModel

    schemaVersion: Literal["openconnect.v1"] = "openconnect.v1"
    taskId: str
    status: Literal["completed", "failed"]
    result: ClassifyResult | None = None
    error: ErrorBody | None = None


class Capability(BaseModel):
    model_config = StrictModel

    kind: Literal["classification"] = "classification"
    labelSets: list[str]
    defaultLabelSet: str
    grantsTools: Literal[False] = False


class CapabilitiesResponse(BaseModel):
    model_config = StrictModel

    workerKind: Literal["classification"] = "classification"
    engine: Literal["fake", "real"]
    modelRevision: str | None = None
    capabilities: list[Capability]
    fakeEngineWarning: str | None = None


class ClaimRequest(BaseModel):
    model_config = StrictModel

    taskId: str = Field(min_length=1, max_length=128)


class ClaimResponse(BaseModel):
    model_config = StrictModel

    taskId: str
    leaseId: str
    fencingToken: int
    expiresAt: str


class HeartbeatRequest(BaseModel):
    model_config = StrictModel

    taskId: str = Field(min_length=1, max_length=128)
    leaseId: str = Field(min_length=1, max_length=128)


class CompleteRequest(BaseModel):
    model_config = StrictModel

    taskId: str = Field(min_length=1, max_length=128)
    leaseId: str = Field(min_length=1, max_length=128)


class FailRequest(BaseModel):
    model_config = StrictModel

    taskId: str = Field(min_length=1, max_length=128)
    leaseId: str = Field(min_length=1, max_length=128)
    code: str = Field(min_length=1, max_length=64)
    message: str = Field(min_length=1, max_length=512)
