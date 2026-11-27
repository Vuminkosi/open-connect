"""Private FastAPI HTTP layer for OpenConnect IMessaging worker."""

from openconnect_imessaging.api.app import app, create_app

__all__ = ["app", "create_app"]
