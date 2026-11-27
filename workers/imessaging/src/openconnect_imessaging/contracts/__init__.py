"""Strict Pydantic v2 wire contracts for OpenConnect IMessaging."""

from openconnect_imessaging.contracts.models import (
    CapabilitiesResponse,
    CapabilityDescriptor,
    EnrollmentQrPayload,
    ExecuteRequest,
    ExecuteResponse,
    ExecuteResult,
    PresencePayload,
    PushDispatchPayload,
    PushWakeupPayload,
    RealtimeEventEnvelope,
    RelayPushRequest,
    RoutePayload,
    SignalPayload,
)

__all__ = [
    "CapabilitiesResponse",
    "CapabilityDescriptor",
    "EnrollmentQrPayload",
    "ExecuteRequest",
    "ExecuteResponse",
    "ExecuteResult",
    "PresencePayload",
    "PushDispatchPayload",
    "PushWakeupPayload",
    "RealtimeEventEnvelope",
    "RelayPushRequest",
    "RoutePayload",
    "SignalPayload",
]
