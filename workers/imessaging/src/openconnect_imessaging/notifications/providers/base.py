"""Abstract push provider contract enforcing opaque wake-up payloads."""

from __future__ import annotations

from abc import ABC, abstractmethod
from dataclasses import dataclass
from enum import StrEnum

from openconnect_imessaging.contracts.models import PushWakeupPayload


class PushProviderStatus(StrEnum):
    """Normalized push dispatch status returned to the gateway."""

    SENT = "sent"
    INVALID_TOKEN = "invalid_token"
    QUOTA_EXCEEDED = "quota_exceeded"
    PROVIDER_UNAVAILABLE = "provider_unavailable"
    DISABLED = "disabled"


@dataclass(frozen=True)
class PushDeliveryResult:
    """Result of a single opaque push wake-up dispatch."""

    status: PushProviderStatus
    provider_message_id: str | None = None
    reason_code: str | None = None


class PushProvider(ABC):
    """Base contract for opaque push notification providers."""

    @property
    @abstractmethod
    def provider_id(self) -> str:
        """Stable provider name ('fcm' or 'webpush')."""

    @property
    @abstractmethod
    def capability_id(self) -> str:
        """Capability ID advertised when ready (e.g. 'messaging.push.fcm')."""

    @abstractmethod
    def is_ready(self) -> bool:
        """Return True only when credentials and configuration are verified ready."""

    @abstractmethod
    def send_wakeup(
        self,
        *,
        target_token: str,
        payload: PushWakeupPayload,
    ) -> PushDeliveryResult:
        """Send an opaque wake-up notification. Never logs target_token."""
