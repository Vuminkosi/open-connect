"""Pydantic models matching the checked-in security scan & inspection JSON contracts."""

from __future__ import annotations

import math
from typing import Annotated, Literal
from uuid import UUID

from pydantic import BaseModel, ConfigDict, Field, field_validator, model_validator

MAX_INSPECTION_TEXT_BYTES = 64 * 1024  # 64 KiB bounded text input on v1 inspection API

LegacyDetectorId = Literal[
    "rules",
    "gliner-pii",
    "deberta-injection",
    "qwen3guard",
    "paddle-ocr",
    "yunet",
]

InspectionDetectorId = Literal[
    "rules",
    "gliner-pii",
    "deberta-injection",
    "qwen3guard",
    "paddle-ocr",
    "yunet",
    "malware",
]

EntityCategory = Literal[
    "PERSON",
    "EMAIL",
    "PHONE",
    "ADDRESS",
    "DATE_OF_BIRTH",
    "GOVERNMENT_ID",
    "ACCOUNT_NUMBER",
    "PAYMENT_CARD",
    "IP_ADDRESS",
    "SECRET",
    "ORG_SENSITIVE",
    "CUSTOM",
]

ImageRegionCategory = Literal[
    "FACE",
    "TEXT_PII",
    "PERSON",
    "EMAIL",
    "PHONE",
    "ADDRESS",
    "DATE_OF_BIRTH",
    "GOVERNMENT_ID",
    "ACCOUNT_NUMBER",
    "PAYMENT_CARD",
    "SECRET",
    "CUSTOM",
]

PolicyAction = Literal["allow", "mask", "redact", "block", "review"]


class ScanRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")

    schemaVersion: Literal["openconnect.v1"]
    requestId: UUID
    text: str = Field(max_length=1_000_000)
    detectors: list[LegacyDetectorId] = Field(min_length=1)
    policyRevision: str | None = Field(default=None, max_length=128)


class ScanSpan(BaseModel):
    model_config = ConfigDict(extra="forbid")

    entityType: EntityCategory
    start: int = Field(ge=0)
    end: int = Field(ge=1)
    confidence: float = Field(ge=0, le=1)
    decision: PolicyAction

    @field_validator("confidence")
    @classmethod
    def _validate_finite_confidence(cls, value: float) -> float:
        if not math.isfinite(value):
            raise ValueError("confidence must be finite")
        return value

    @model_validator(mode="after")
    def validate_range(self) -> ScanSpan:
        if self.start >= self.end:
            raise ValueError("span must be a non-empty half-open range")
        return self


class ScanResponse(BaseModel):
    model_config = ConfigDict(extra="forbid")

    schemaVersion: Literal["openconnect.v1"]
    requestId: UUID
    detectorId: str = Field(min_length=1)
    detectorVersion: str = Field(min_length=1)
    spans: list[ScanSpan]


class CapabilityDescriptor(BaseModel):
    """Structured capability item compatible with Host WorkerCapabilityResponseParser."""

    model_config = ConfigDict(extra="forbid")

    schemaVersion: Literal["openconnect.v1"] = "openconnect.v1"
    id: str = Field(min_length=1, max_length=256)
    kind: Literal["security"] = "security"
    state: Literal["ready"] = "ready"
    version: str = Field(min_length=1, max_length=128)
    localOnly: Literal[True] = True


class CapabilitiesResponse(BaseModel):
    """Exact root payload for GET /internal/v1/capabilities."""

    model_config = ConfigDict(extra="forbid")

    capabilities: list[CapabilityDescriptor] = Field(max_length=64)


class InspectionRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")

    schemaVersion: Literal["openconnect.security-inspection.v1"]
    requestId: UUID
    policyRevision: str | None = Field(default=None, min_length=1, max_length=128)
    modality: Literal["text", "prompt", "image", "file"]
    detectors: list[InspectionDetectorId] = Field(min_length=1, max_length=16)
    text: str | None = Field(default=None, max_length=MAX_INSPECTION_TEXT_BYTES)
    contentSha256: str | None = Field(default=None, pattern=r"^[0-9a-f]{64}$")
    contentType: str | None = Field(default=None, max_length=128)

    @field_validator("detectors")
    @classmethod
    def _reject_duplicate_detectors(cls, value: list[str]) -> list[str]:
        if len(set(value)) != len(value):
            raise ValueError("duplicate detectors are not allowed")
        return value

    @model_validator(mode="after")
    def _validate_modality_payload(self) -> InspectionRequest:
        if self.modality in ("text", "prompt"):
            if self.text is None:
                raise ValueError("text is required for text and prompt modalities")
            if len(self.text.encode("utf-8")) > MAX_INSPECTION_TEXT_BYTES:
                raise ValueError("text exceeds 64 KiB UTF-8 byte limit")
        return self


class DetectorExecutionStatus(BaseModel):
    model_config = ConfigDict(extra="forbid")

    id: InspectionDetectorId
    version: str = Field(min_length=1, max_length=128)
    status: Literal["completed", "unavailable", "unsupported", "limit_exceeded", "error"]
    reasonCode: str | None = Field(default=None, max_length=64)


class TextSpanFinding(BaseModel):
    model_config = ConfigDict(extra="forbid")

    kind: Literal["text_span"] = "text_span"
    detectorId: Literal["rules", "gliner-pii", "deberta-injection", "qwen3guard", "paddle-ocr"]
    category: EntityCategory
    start: int = Field(ge=0)
    end: int = Field(ge=1)
    confidence: float = Field(ge=0, le=1)
    recommendedAction: PolicyAction

    @field_validator("confidence")
    @classmethod
    def _finite_confidence(cls, value: float) -> float:
        if not math.isfinite(value):
            raise ValueError("confidence must be finite")
        return value

    @model_validator(mode="after")
    def _validate_span_bounds(self) -> TextSpanFinding:
        if self.start >= self.end:
            raise ValueError("start must be strictly less than end")
        return self


class ImageRegionFinding(BaseModel):
    model_config = ConfigDict(extra="forbid")

    kind: Literal["image_region"] = "image_region"
    detectorId: Literal["paddle-ocr", "yunet"]
    category: ImageRegionCategory
    x: int = Field(ge=0)
    y: int = Field(ge=0)
    width: int = Field(ge=1)
    height: int = Field(ge=1)
    confidence: float = Field(ge=0, le=1)
    recommendedAction: PolicyAction

    @field_validator("confidence")
    @classmethod
    def _finite_confidence(cls, value: float) -> float:
        if not math.isfinite(value):
            raise ValueError("confidence must be finite")
        return value


class MalwareFinding(BaseModel):
    model_config = ConfigDict(extra="forbid")

    kind: Literal["malware"] = "malware"
    detectorId: Literal["malware"] = "malware"
    outcome: Literal["clean", "detected", "unavailable", "unsupported", "limit_exceeded"]
    reasonCode: str = Field(min_length=1, max_length=64)
    signatureVersion: str | None = Field(default=None, max_length=128)


class InjectionFinding(BaseModel):
    model_config = ConfigDict(extra="forbid")

    kind: Literal["injection"] = "injection"
    detectorId: Literal["deberta-injection", "qwen3guard"]
    label: Literal["benign", "injection", "uncertain"]
    score: float = Field(ge=0, le=1)
    threshold: float = Field(ge=0, le=1)
    modelRevision: str = Field(min_length=1, max_length=128)

    @field_validator("score", "threshold")
    @classmethod
    def _finite_scores(cls, value: float) -> float:
        if not math.isfinite(value):
            raise ValueError("score and threshold must be finite")
        return value


InspectionFinding = Annotated[
    TextSpanFinding | ImageRegionFinding | MalwareFinding | InjectionFinding,
    Field(discriminator="kind"),
]


class ImageMetadata(BaseModel):
    model_config = ConfigDict(extra="forbid")

    width: int = Field(ge=1)
    height: int = Field(ge=1)
    coordinateSpace: Literal["top-left-pixels"] = "top-left-pixels"
    orientationNormalized: bool


class InspectionResponse(BaseModel):
    model_config = ConfigDict(extra="forbid")

    schemaVersion: Literal["openconnect.security-inspection.v1"] = (
        "openconnect.security-inspection.v1"
    )
    requestId: UUID
    modality: Literal["text", "prompt", "image", "file"]
    status: Literal["completed", "partial", "unavailable", "unsupported", "limit_exceeded"]
    detectors: list[DetectorExecutionStatus] = Field(min_length=1, max_length=16)
    findings: list[InspectionFinding]
    offsetUnit: Literal["unicode-scalar"] | None = None
    contentSha256: str | None = Field(default=None, pattern=r"^[0-9a-f]{64}$")
    contentType: str | None = Field(default=None, max_length=128)
    imageMetadata: ImageMetadata | None = None
    durationMs: int = Field(ge=0)
