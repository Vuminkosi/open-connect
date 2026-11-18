"""Pydantic contracts for the NLLB translation worker."""

from typing import Literal

from pydantic import BaseModel, ConfigDict, Field, model_validator

StrictModel = ConfigDict(extra="forbid")


class TranslateInput(BaseModel):
    """Exactly one of text / inputRef must be supplied (ref fetched host-side)."""

    model_config = StrictModel

    text: str | None = Field(default=None, max_length=5000)
    inputRef: str | None = Field(default=None, max_length=2048)
    sourceLang: str = Field(min_length=1, max_length=16)
    targetLang: str = Field(min_length=1, max_length=16)

    @model_validator(mode="after")
    def _require_text_or_ref(self) -> "TranslateInput":
        if not self.text and not self.inputRef:
            raise ValueError("one of text or inputRef is required")
        if self.text and self.inputRef:
            raise ValueError("only one of text or inputRef may be set")
        return self


class TranslateResult(BaseModel):
    model_config = StrictModel

    translatedText: str
    sourceLang: str
    targetLang: str
    modelRevision: str
    tokenizerHash: str
    engine: Literal["fake", "real"]
    engineWarning: str | None = None


class ExecuteRequest(BaseModel):
    model_config = StrictModel

    schemaVersion: Literal["openconnect.v1"] = "openconnect.v1"
    taskId: str = Field(min_length=1, max_length=128)
    leaseId: str | None = Field(default=None, max_length=128)
    input: TranslateInput


class ErrorBody(BaseModel):
    model_config = StrictModel

    code: str
    message: str


class ExecuteResponse(BaseModel):
    model_config = StrictModel

    schemaVersion: Literal["openconnect.v1"] = "openconnect.v1"
    taskId: str
    status: Literal["completed", "failed"]
    result: TranslateResult | None = None
    error: ErrorBody | None = None


class Capability(BaseModel):
    model_config = StrictModel

    kind: Literal["translation"] = "translation"
    languagePairs: list[str]
    grantsTools: Literal[False] = False


class CapabilitiesResponse(BaseModel):
    model_config = StrictModel

    workerKind: Literal["nllb"] = "nllb"
    engine: Literal["fake", "real"]
    modelRevision: str | None = None
    tokenizerHash: str | None = None
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
