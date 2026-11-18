"""Capability descriptor contract (contracts/jsonschema/capability.json)."""

from enum import StrEnum
from typing import Literal

from pydantic import BaseModel, ConfigDict, Field

SCHEMA_VERSION = "openconnect.v1"


class CapabilityKind(StrEnum):
    """Worker capability kinds; wire values are kebab-case."""

    CHAT = "chat"
    EMBEDDING = "embedding"
    IMAGE = "image"
    AUDIO = "audio"
    CLASSIFICATION = "classification"
    SECURITY_SCAN = "security-scan"
    TOOL = "tool"


class CapabilityState(StrEnum):
    """Lifecycle state of a capability; wire values are kebab-case."""

    DISABLED = "disabled"
    LOADING = "loading"
    READY = "ready"
    DEGRADED = "degraded"
    FAILED = "failed"


class CapabilityDescriptor(BaseModel):
    """Advertisable capability of a worker. Disabled capabilities are never advertised."""

    model_config = ConfigDict(extra="forbid")

    schemaVersion: Literal["openconnect.v1"] = SCHEMA_VERSION
    id: str = Field(min_length=1)
    kind: CapabilityKind
    state: CapabilityState
    version: str = Field(min_length=1)
    localOnly: bool = False

    def is_advertisable(self) -> bool:
        """Only ready capabilities may be advertised to the router."""
        return self.state == CapabilityState.READY
