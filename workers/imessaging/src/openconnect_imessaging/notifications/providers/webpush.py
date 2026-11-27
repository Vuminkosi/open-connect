"""Optional Web Push (VAPID) opaque wake-up provider for browsers."""

from __future__ import annotations

from collections.abc import Callable

from openconnect_imessaging.contracts.models import PushWakeupPayload
from openconnect_imessaging.notifications.providers.base import (
    PushDeliveryResult,
    PushProvider,
    PushProviderStatus,
)


class WebPushProvider(PushProvider):
    """VAPID Web Push provider; disabled unless VAPID keys and subject are configured."""

    def __init__(
        self,
        *,
        enabled: bool,
        vapid_public_key: str | None = None,
        vapid_private_key: str | None = None,
        vapid_subject: str | None = None,
        sender: Callable[[str, PushWakeupPayload], PushDeliveryResult] | None = None,
    ) -> None:
        self._enabled = enabled
        self._public_key = vapid_public_key
        self._private_key = vapid_private_key
        self._subject = vapid_subject
        self._sender = sender

    @property
    def provider_id(self) -> str:
        return "webpush"

    @property
    def capability_id(self) -> str:
        return "messaging.push.webpush"

    def is_ready(self) -> bool:
        return bool(
            self._enabled
            and self._public_key
            and self._private_key
            and self._subject
            and (self._subject.startswith("mailto:") or self._subject.startswith("https://"))
        )

    def send_wakeup(
        self,
        *,
        target_token: str,
        payload: PushWakeupPayload,
    ) -> PushDeliveryResult:
        if not self.is_ready():
            return PushDeliveryResult(
                status=PushProviderStatus.DISABLED,
                reason_code="webpush_not_configured",
            )
        if not target_token or len(target_token) < 16:
            return PushDeliveryResult(
                status=PushProviderStatus.INVALID_TOKEN,
                reason_code="invalid_webpush_subscription",
            )
        if self._sender is not None:
            return self._sender(target_token, payload)
        return PushDeliveryResult(
            status=PushProviderStatus.PROVIDER_UNAVAILABLE,
            reason_code="webpush_transport_not_bound",
        )
