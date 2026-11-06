"""Pydantic models matching the checked-in security scan JSON contracts."""

from typing import Literal
from uuid import UUID

from pydantic import BaseModel, ConfigDict, Field, model_validator


class ScanRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")

    schemaVersion: Literal["openconnect.v1"]
    requestId: UUID
    text: str = Field(max_length=1_000_000)
    detectors: list[
        Literal["rules", "gliner-pii", "deberta-injection", "qwen3guard", "paddle-ocr", "yunet"]
    ] = Field(min_length=1)
    policyRevision: str | None = Field(default=None, max_length=128)


class ScanSpan(BaseModel):
    model_config = ConfigDict(extra="forbid")

    entityType: Literal[
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
    start: int = Field(ge=0)
    end: int = Field(ge=1)
    confidence: float = Field(ge=0, le=1)
    decision: Literal["allow", "mask", "redact", "block", "review"]

    @model_validator(mode="after")
    def validate_range(self) -> "ScanSpan":
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
