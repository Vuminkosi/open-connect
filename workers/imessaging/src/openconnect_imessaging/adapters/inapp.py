"""In-app communications channel adapter (the only implemented channel)."""

from __future__ import annotations

from openconnect_imessaging.adapters.base import ChannelAdapter

INAPP_CAPABILITIES: tuple[str, ...] = (
    "messaging.inapp.send",
    "messaging.inapp.receive",
    "messaging.inapp.call",
    "messaging.device.enrollment",
)


class InAppChannelAdapter(ChannelAdapter):
    """Verified in-app messaging and signaling channel adapter."""

    def __init__(self, *, enabled: bool) -> None:
        self._enabled = enabled

    @property
    def channel_id(self) -> str:
        return "inapp"

    def is_ready(self) -> bool:
        return self._enabled

    def supported_capabilities(self) -> tuple[str, ...]:
        return INAPP_CAPABILITIES if self._enabled else ()
