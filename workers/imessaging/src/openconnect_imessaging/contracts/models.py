"""Strict Pydantic v2 models matching contracts/jsonschema/imessaging-*.json."""

from __future__ import annotations

import base64
import binascii
import re
from typing import Any, Literal

from pydantic import AwareDatetime, BaseModel, ConfigDict, Field, field_validator, model_validator

OPAQUE_ID_PATTERN = r"^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$"
NONCE_PATTERN = r"^[A-Za-z0-9._:-]{16,128}$"

# Patterns strictly forbidden inside push displayTitle / displayBody.
_FORBIDDEN_PUSH_CONTENT_PATTERNS: tuple[re.Pattern[str], ...] = (
    re.compile(r"https?://", re.IGNORECASE),
    re.compile(r"\bbearer\s+[A-Za-z0-9._~+/=-]{8,}", re.IGNORECASE),
    re.compile(r"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}"),  # JWT-like
    re.compile(r"-----BEGIN [A-Z ]+KEY-----"),
    re.compile(r"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}"),
)


def _validate_safe_push_text(value: str, field_name: str) -> str:
    if any(ord(ch) < 32 or ord(ch) == 127 for ch in value):
        raise ValueError(f"{field_name} must not contain control characters")
    for pattern in _FORBIDDEN_PUSH_CONTENT_PATTERNS:
        if pattern.search(value):
            raise ValueError(
                f"{field_name} contains forbidden sensitive content (URL, token, key, or identity)"
            )
    return value


class PushWakeupPayload(BaseModel):
    """Strict allowlisted opaque push wake-up payload (imessaging-push-wakeup-v1.json)."""

    model_config = ConfigDict(extra="forbid")

    schemaVersion: Literal[1] = 1
    notificationId: str = Field(min_length=1, max_length=128, pattern=OPAQUE_ID_PATTERN)
    profileHandle: str = Field(min_length=1, max_length=128, pattern=OPAQUE_ID_PATTERN)
    eventClass: Literal["message", "call", "approval", "system"]
    displayTitle: str = Field(min_length=1, max_length=64)
    displayBody: str = Field(min_length=1, max_length=120)
    ttlSeconds: int = Field(ge=1, le=86400)
    collapseKey: str | None = Field(
        default=None, min_length=1, max_length=64, pattern=r"^[A-Za-z0-9][A-Za-z0-9._:-]{0,63}$"
    )

    @field_validator("displayTitle")
    @classmethod
    def _check_title(cls, value: str) -> str:
        return _validate_safe_push_text(value, "displayTitle")

    @field_validator("displayBody")
    @classmethod
    def _check_body(cls, value: str) -> str:
        return _validate_safe_push_text(value, "displayBody")


class RoutePayload(BaseModel):
    """Ciphertext-only targeted routing payload for messaging.route."""

    model_config = ConfigDict(extra="forbid")

    eventId: str = Field(min_length=1, max_length=128, pattern=OPAQUE_ID_PATTERN)
    conversationId: str = Field(min_length=1, max_length=128, pattern=OPAQUE_ID_PATTERN)
    sequence: int = Field(ge=1)
    targetGateways: list[str] = Field(min_length=1, max_length=64)
    targetDeviceIds: list[str] = Field(min_length=1, max_length=256)
    securityProfile: Literal["PrivateE2EE", "AgentE2EE", "Governed"]
    protocol: str = Field(min_length=1, max_length=64)
    epoch: int = Field(ge=0)
    ciphertextBase64: str = Field(min_length=1, max_length=262144, repr=False)
    occurredAt: AwareDatetime
    ttlSeconds: int | None = Field(default=None, ge=1, le=86400)

    @field_validator("targetGateways", "targetDeviceIds")
    @classmethod
    def _validate_opaque_list(cls, values: list[str]) -> list[str]:
        pattern = re.compile(OPAQUE_ID_PATTERN)
        for item in values:
            if not pattern.match(item):
                raise ValueError(f"invalid opaque target identifier: {item!r}")
        return values

    @field_validator("ciphertextBase64")
    @classmethod
    def _validate_base64(cls, value: str) -> str:
        try:
            decoded = base64.b64decode(value, validate=True)
        except (binascii.Error, ValueError) as exc:
            raise ValueError("ciphertextBase64 must be valid base64") from exc
        if not decoded:
            raise ValueError("ciphertextBase64 must not decode to empty bytes")
        return value


class PresencePayload(BaseModel):
    """Ephemeral device-scoped presence lease update for messaging.presence."""

    model_config = ConfigDict(extra="forbid")

    eventId: str = Field(min_length=1, max_length=128, pattern=OPAQUE_ID_PATTERN)
    principalId: str = Field(min_length=1, max_length=128, pattern=OPAQUE_ID_PATTERN)
    deviceId: str = Field(min_length=1, max_length=128, pattern=OPAQUE_ID_PATTERN)
    gatewayId: str = Field(min_length=1, max_length=128, pattern=OPAQUE_ID_PATTERN)
    status: Literal["online", "away", "offline"]
    conversationIds: list[str] = Field(default_factory=list, max_length=256)
    ttlSeconds: int = Field(ge=10, le=300)
    occurredAt: AwareDatetime

    @field_validator("conversationIds")
    @classmethod
    def _validate_convs(cls, values: list[str]) -> list[str]:
        pattern = re.compile(OPAQUE_ID_PATTERN)
        for item in values:
            if not pattern.match(item):
                raise ValueError(f"invalid conversationId: {item!r}")
        return values


class SignalPayload(BaseModel):
    """Opaque call signaling frame for messaging.signal."""

    model_config = ConfigDict(extra="forbid")

    eventId: str = Field(min_length=1, max_length=128, pattern=OPAQUE_ID_PATTERN)
    callId: str = Field(min_length=1, max_length=128, pattern=OPAQUE_ID_PATTERN)
    conversationId: str = Field(min_length=1, max_length=128, pattern=OPAQUE_ID_PATTERN)
    signalType: Literal[
        "invite", "ringing", "accept", "decline", "cancel", "end", "missed", "ice", "sdp"
    ]
    targetGateways: list[str] = Field(min_length=1, max_length=64)
    targetDeviceIds: list[str] = Field(min_length=1, max_length=64)
    expiresAt: AwareDatetime | None = None
    opaquePayloadBase64: str | None = Field(default=None, max_length=65536, repr=False)
    occurredAt: AwareDatetime

    @field_validator("targetGateways", "targetDeviceIds")
    @classmethod
    def _validate_targets(cls, values: list[str]) -> list[str]:
        pattern = re.compile(OPAQUE_ID_PATTERN)
        for item in values:
            if not pattern.match(item):
                raise ValueError(f"invalid opaque target identifier: {item!r}")
        return values

    @field_validator("opaquePayloadBase64")
    @classmethod
    def _validate_opaque_b64(cls, value: str | None) -> str | None:
        if value is None:
            return None
        try:
            base64.b64decode(value, validate=True)
        except (binascii.Error, ValueError) as exc:
            raise ValueError("opaquePayloadBase64 must be valid base64") from exc
        return value


class PushDispatchPayload(BaseModel):
    """Gateway-authorized push dispatch payload for messaging.push.dispatch."""

    model_config = ConfigDict(extra="forbid")

    eventId: str = Field(min_length=1, max_length=128, pattern=OPAQUE_ID_PATTERN)
    provider: Literal["fcm", "webpush"]
    registrationHandle: str = Field(min_length=1, max_length=128, pattern=OPAQUE_ID_PATTERN)
    targetToken: str | None = Field(default=None, min_length=16, max_length=4096, repr=False)
    pushPayload: PushWakeupPayload


class ExecuteRequest(BaseModel):
    """Signed gateway-to-worker execution envelope (imessaging-execute-request-v1.json)."""

    model_config = ConfigDict(extra="forbid")

    schemaVersion: Literal["openconnect.v1"] = "openconnect.v1"
    taskId: str = Field(min_length=1, max_length=128, pattern=OPAQUE_ID_PATTERN)
    kind: Literal[
        "messaging.route",
        "messaging.presence",
        "messaging.signal",
        "messaging.push.dispatch",
    ]
    tenantId: str = Field(min_length=1, max_length=128, pattern=OPAQUE_ID_PATTERN)
    nonce: str = Field(min_length=16, max_length=128, pattern=NONCE_PATTERN)
    issuedAt: AwareDatetime
    expiresAt: AwareDatetime
    payload: dict[str, Any]

    @model_validator(mode="after")
    def _validate_expiry_and_payload(self) -> ExecuteRequest:
        if self.expiresAt <= self.issuedAt:
            raise ValueError("expiresAt must be after issuedAt")
        # Validate typed payload strictly per kind.
        if self.kind == "messaging.route":
            RoutePayload.model_validate(self.payload)
        elif self.kind == "messaging.presence":
            PresencePayload.model_validate(self.payload)
        elif self.kind == "messaging.signal":
            SignalPayload.model_validate(self.payload)
        elif self.kind == "messaging.push.dispatch":
            PushDispatchPayload.model_validate(self.payload)
        return self

    def typed_payload(
        self,
    ) -> RoutePayload | PresencePayload | SignalPayload | PushDispatchPayload:
        if self.kind == "messaging.route":
            return RoutePayload.model_validate(self.payload)
        if self.kind == "messaging.presence":
            return PresencePayload.model_validate(self.payload)
        if self.kind == "messaging.signal":
            return SignalPayload.model_validate(self.payload)
        return PushDispatchPayload.model_validate(self.payload)


class ExecuteResult(BaseModel):
    """Structured execution result body."""

    model_config = ConfigDict(extra="forbid")

    eventId: str = Field(min_length=1, max_length=128)
    dispatchedCount: int = Field(ge=0)
    failedTargets: list[str] = Field(default_factory=list, max_length=256)
    providerStatus: (
        Literal[
            "sent",
            "queued",
            "invalid_token",
            "quota_exceeded",
            "provider_unavailable",
            "disabled",
            "deduplicated",
        ]
        | None
    ) = None
    leaseExpiresAt: AwareDatetime | None = None
    reasonCode: str | None = Field(default=None, min_length=1, max_length=64)
    processedAt: AwareDatetime


class ExecuteResponse(BaseModel):
    """Structured worker response envelope (imessaging-execute-response-v1.json)."""

    model_config = ConfigDict(extra="forbid")

    schemaVersion: Literal["openconnect.v1"] = "openconnect.v1"
    taskId: str = Field(min_length=1, max_length=128)
    kind: Literal[
        "messaging.route",
        "messaging.presence",
        "messaging.signal",
        "messaging.push.dispatch",
    ]
    tenantId: str = Field(min_length=1, max_length=128)
    status: Literal["accepted", "deduplicated", "degraded", "rejected"]
    result: ExecuteResult


class RelayPushRequest(BaseModel):
    """Narrowly scoped authenticated push-relay request (opaque handle only, never raw FCM token)."""

    model_config = ConfigDict(extra="forbid")

    schemaVersion: Literal["openconnect.v1"] = "openconnect.v1"
    requestId: str = Field(min_length=1, max_length=128, pattern=OPAQUE_ID_PATTERN)
    tenantId: str = Field(min_length=1, max_length=128, pattern=OPAQUE_ID_PATTERN)
    applicationId: str = Field(min_length=1, max_length=128, pattern=OPAQUE_ID_PATTERN)
    registrationHandle: str = Field(min_length=1, max_length=128, pattern=OPAQUE_ID_PATTERN)
    nonce: str = Field(min_length=16, max_length=128, pattern=NONCE_PATTERN)
    issuedAt: AwareDatetime
    expiresAt: AwareDatetime
    pushPayload: PushWakeupPayload

    @model_validator(mode="after")
    def _validate_expiry(self) -> RelayPushRequest:
        if self.expiresAt <= self.issuedAt:
            raise ValueError("expiresAt must be after issuedAt")
        return self


class RealtimeEventEnvelope(BaseModel):
    """Versioned realtime event envelope (imessaging-event-v1.json)."""

    model_config = ConfigDict(extra="forbid")

    schemaVersion: Literal[1] = 1
    eventId: str = Field(min_length=1, max_length=128, pattern=OPAQUE_ID_PATTERN)
    type: Literal[
        "messaging.message.committed",
        "messaging.message.delivered",
        "messaging.message.read",
        "messaging.membership.changed",
        "messaging.key.commit",
        "messaging.presence.changed",
        "messaging.typing.changed",
        "messaging.call.invited",
        "messaging.call.accepted",
        "messaging.call.declined",
        "messaging.call.ended",
        "messaging.call.missed",
        "messaging.followup.due",
        "messaging.approval.requested",
        "messaging.approval.resolved",
        "messaging.device.revoked",
        "messaging.capabilities.changed",
    ]
    conversationId: str | None = Field(default=None, min_length=1, max_length=128)
    sequence: int | None = Field(default=None, ge=0)
    cursor: str = Field(min_length=1, max_length=512)
    occurredAt: AwareDatetime
    payload: dict[str, Any]


class EnrollmentQrPayload(BaseModel):
    """Desktop-to-mobile QR enrollment payload (imessaging-enrollment-qr-v1.json)."""

    model_config = ConfigDict(extra="forbid")

    version: Literal[1] = 1
    type: Literal["openconnect.device.enrollment"] = "openconnect.device.enrollment"
    baseGatewayUrl: str = Field(min_length=10, max_length=512, pattern=r"^https://[^\s@#?]+$")
    tenantId: str = Field(min_length=1, max_length=128, pattern=OPAQUE_ID_PATTERN)
    applicationId: str = Field(min_length=1, max_length=128, pattern=OPAQUE_ID_PATTERN)
    pairingId: str = Field(min_length=1, max_length=128, pattern=OPAQUE_ID_PATTERN)
    enrollmentToken: str = Field(min_length=32, max_length=256, pattern=r"^[A-Za-z0-9_-]{32,256}$")
    expiresAt: AwareDatetime


class CapabilityDescriptor(BaseModel):
    """Structured capability descriptor matching WorkerCapabilityResponseParser.cs."""

    model_config = ConfigDict(extra="forbid")

    schemaVersion: Literal["openconnect.v1"] = "openconnect.v1"
    id: str = Field(min_length=1, max_length=256)
    kind: Literal["tool"] = "tool"
    state: Literal["ready"] = "ready"
    version: str = Field(min_length=1, max_length=128)
    localOnly: bool = True


class CapabilitiesResponse(BaseModel):
    """Response for GET /internal/v1/capabilities."""

    model_config = ConfigDict(extra="forbid")

    capabilities: list[CapabilityDescriptor]
