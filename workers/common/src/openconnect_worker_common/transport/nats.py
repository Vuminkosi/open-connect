"""NATS connection settings and subject conventions.

This module performs no I/O on import. The optional ``nats-py`` dependency is
only imported inside :func:`connect` so the package stays dependency-light.
"""

import os
from dataclasses import dataclass, field

SUBJECT_PREFIX = "openconnect.v1"

DEFAULT_NATS_URL = "nats://127.0.0.1:4222"


@dataclass(frozen=True)
class NatsSettings:
    """Bounded, non-blocking-friendly NATS client settings."""

    url: str = DEFAULT_NATS_URL
    connect_timeout_seconds: float = 5.0
    max_reconnect_attempts: int = 5
    request_timeout_seconds: float = 10.0

    @classmethod
    def from_environment(cls) -> "NatsSettings":
        url = os.environ.get("OPENCONNECT_NATS_URL", DEFAULT_NATS_URL)
        return cls(url=url)

    def validated(self) -> "NatsSettings":
        if not self.url.startswith(("nats://", "tls://")):
            raise ValueError("NATS url must start with nats:// or tls://")
        if self.connect_timeout_seconds <= 0 or self.request_timeout_seconds <= 0:
            raise ValueError("NATS timeouts must be positive")
        if self.max_reconnect_attempts < 0:
            raise ValueError("max_reconnect_attempts must not be negative")
        return self


def task_subject(kind: str) -> str:
    """Subject workers subscribe to for claimable tasks of a kind."""
    _require_token(kind)
    return f"{SUBJECT_PREFIX}.tasks.{kind}"


def result_subject(task_id: str) -> str:
    """Subject a task result is published to."""
    _require_token(task_id)
    return f"{SUBJECT_PREFIX}.results.{task_id}"


def _require_token(value: str) -> None:
    if not value or any(c in value for c in (" ", ".", "*", ">", "\n", "\r", "\t")):
        raise ValueError(f"invalid NATS subject token: {value!r}")


async def connect(settings: NatsSettings):  # type: ignore[no-untyped-def]
    """Connect to NATS with bounded settings. Requires the ``nats-py`` package."""
    settings.validated()
    try:
        import nats
    except ImportError as exc:
        raise RuntimeError(
            "nats-py is not installed; add it to the worker that needs live NATS access"
        ) from exc
    return await nats.connect(
        settings.url,
        connect_timeout=settings.connect_timeout_seconds,
        max_reconnect_attempts=settings.max_reconnect_attempts,
    )


@dataclass(frozen=True)
class NatsStreamConfig:
    """Declarative JetStream stream configuration (no client calls made here)."""

    name: str
    subjects: tuple[str, ...] = field(default_factory=tuple)
