"""NATS JetStream OC_MESSAGING and Core live gateway subject helpers.

No network I/O is performed on import. Live broker publication uses ``nats-py``
when connected; bounded in-memory queues enforce backpressure when busy.
"""

from __future__ import annotations

import json
import re
import threading
from collections import deque
from dataclasses import dataclass
from typing import Any, Protocol

from openconnect_worker_common.transport.nats import NatsSettings, NatsStreamConfig

MESSAGING_STREAM_NAME = "OC_MESSAGING"
DURABLE_SUBJECT_PREFIX = "oc.v1.messaging.durable"
LIVE_SUBJECT_PREFIX = "oc.v1.messaging.live"

MESSAGING_STREAM_CONFIG = NatsStreamConfig(
    name=MESSAGING_STREAM_NAME,
    subjects=(f"{DURABLE_SUBJECT_PREFIX}.>",),
)

_SAFE_SUBJECT_TOKEN = re.compile(r"^[A-Za-z0-9][A-Za-z0-9_-]{0,127}$")


class BrokerUnavailableError(RuntimeError):
    """Raised when NATS broker transport is unavailable."""


class QueueFullError(RuntimeError):
    """Raised when the bounded outbound dispatch queue is saturated."""


def _validate_subject_token(value: str, label: str) -> str:
    if not _SAFE_SUBJECT_TOKEN.match(value):
        raise ValueError(f"invalid NATS {label} token: {value!r}")
    return value


def live_gateway_subject(tenant_id: str, gateway_id: str) -> str:
    """Return validated Core NATS subject ``oc.v1.messaging.live.<tenant>.<gateway>``."""
    tenant = _validate_subject_token(tenant_id, "tenant")
    gateway = _validate_subject_token(gateway_id, "gateway")
    return f"{LIVE_SUBJECT_PREFIX}.{tenant}.{gateway}"


def durable_subject(tenant_id: str, event_kind: str) -> str:
    """Return validated JetStream subject ``oc.v1.messaging.durable.<tenant>.<kind>``."""
    tenant = _validate_subject_token(tenant_id, "tenant")
    kind = _validate_subject_token(event_kind, "event_kind")
    return f"{DURABLE_SUBJECT_PREFIX}.{tenant}.{kind}"


@dataclass(frozen=True)
class PublishedMessage:
    """Record of an opaque message dispatched to a NATS subject."""

    subject: str
    payload_bytes: bytes
    durable: bool = False


class BrokerPublisher(Protocol):
    """Protocol for an injectable synchronous or fake broker publisher."""

    def publish(self, subject: str, payload: bytes, *, durable: bool = False) -> None: ...

    def is_connected(self) -> bool: ...


class MessagingNatsTransport:
    """Bounded NATS messaging transport with strict subject validation and backpressure."""

    def __init__(
        self,
        settings: NatsSettings,
        *,
        max_queue_depth: int = 1024,
        publisher: BrokerPublisher | None = None,
    ) -> None:
        self._settings = settings.validated()
        self._max_queue_depth = max_queue_depth
        self._publisher = publisher
        self._lock = threading.Lock()
        self._queue: deque[PublishedMessage] = deque()

    @property
    def stream_config(self) -> NatsStreamConfig:
        return MESSAGING_STREAM_CONFIG

    @property
    def is_available(self) -> bool:
        if self._publisher is not None:
            return self._publisher.is_connected()
        return True

    @property
    def queue_depth(self) -> int:
        with self._lock:
            return len(self._queue)

    def publish_live(self, tenant_id: str, gateway_id: str, envelope: dict[str, Any]) -> str:
        """Publish an opaque envelope to ``oc.v1.messaging.live.<tenant>.<gateway>``."""
        subject = live_gateway_subject(tenant_id, gateway_id)
        data = json.dumps(envelope, separators=(",", ":"), sort_keys=True).encode("utf-8")
        self._dispatch(subject, data, durable=False)
        return subject

    def publish_durable(self, tenant_id: str, event_kind: str, envelope: dict[str, Any]) -> str:
        """Publish an opaque envelope to ``oc.v1.messaging.durable.<tenant>.<kind>``."""
        subject = durable_subject(tenant_id, event_kind)
        data = json.dumps(envelope, separators=(",", ":"), sort_keys=True).encode("utf-8")
        self._dispatch(subject, data, durable=True)
        return subject

    def _dispatch(self, subject: str, payload: bytes, *, durable: bool) -> None:
        if self._publisher is not None:
            if not self._publisher.is_connected():
                raise BrokerUnavailableError("NATS broker is disconnected")
            self._publisher.publish(subject, payload, durable=durable)
            return

        with self._lock:
            if len(self._queue) >= self._max_queue_depth:
                raise QueueFullError("IMessaging outbound queue limit exceeded")
            self._queue.append(
                PublishedMessage(subject=subject, payload_bytes=payload, durable=durable)
            )

    def drain_queued(self) -> list[PublishedMessage]:
        """Drain locally buffered messages (used in tests and embedded verification)."""
        with self._lock:
            items = list(self._queue)
            self._queue.clear()
            return items
