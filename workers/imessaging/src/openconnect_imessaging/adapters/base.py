"""Abstract channel adapter contract.

External messaging networks (WhatsApp, Telegram, Discord) are planned designs only
and MUST NOT be implemented or advertised as ready capabilities.
"""

from __future__ import annotations

from abc import ABC, abstractmethod


class ChannelAdapter(ABC):
    """Base contract for verified OpenConnect messaging channel adapters."""

    @property
    @abstractmethod
    def channel_id(self) -> str:
        """Stable channel identifier (e.g. 'inapp')."""

    @abstractmethod
    def is_ready(self) -> bool:
        """Return True only when the channel is genuinely operational."""

    @abstractmethod
    def supported_capabilities(self) -> tuple[str, ...]:
        """Return capability IDs that may be advertised when ready."""
