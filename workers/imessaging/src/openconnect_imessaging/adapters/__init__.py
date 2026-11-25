"""Channel adapters for OpenConnect IMessaging (in-app only)."""

from openconnect_imessaging.adapters.base import ChannelAdapter
from openconnect_imessaging.adapters.inapp import InAppChannelAdapter

__all__ = ["ChannelAdapter", "InAppChannelAdapter"]
