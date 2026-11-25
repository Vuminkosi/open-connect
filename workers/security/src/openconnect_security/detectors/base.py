"""Base detector lifecycle types, status snapshots, and bounded execution queues."""

from __future__ import annotations

import contextlib
import threading
from collections.abc import Iterator
from dataclasses import dataclass
from typing import Literal, Protocol

DetectorLifecycleState = Literal[
    "disabled",
    "configured",
    "provisioning",
    "ready",
    "degraded",
    "unavailable",
]


class QueueTimeoutError(RuntimeError):
    """Raised when a bounded worker queue cannot admit a request within its wait budget."""


class BoundedExecutionQueue:
    """Bounded concurrency semaphore with fail-closed wait timeout.

    Keeps CPU model inference (1-2 slots) isolated from malware scanning and
    ensures `/health/live` is never blocked by inference backpressure.
    """

    def __init__(self, *, name: str, max_concurrent: int = 2, wait_timeout_seconds: float = 10.0) -> None:
        if max_concurrent < 1:
            raise ValueError("max_concurrent must be >= 1")
        if wait_timeout_seconds <= 0:
            raise ValueError("wait_timeout_seconds must be > 0")
        self.name = name
        self.max_concurrent = max_concurrent
        self.wait_timeout_seconds = wait_timeout_seconds
        self._semaphore = threading.BoundedSemaphore(max_concurrent)

    @contextlib.contextmanager
    def acquire(self, timeout_seconds: float | None = None) -> Iterator[None]:
        timeout = self.wait_timeout_seconds if timeout_seconds is None else timeout_seconds
        acquired = self._semaphore.acquire(timeout=timeout)
        if not acquired:
            raise QueueTimeoutError(f"queue_timeout:{self.name}")
        try:
            yield
        finally:
            self._semaphore.release()


@dataclass(frozen=True)
class DetectorStatusSnapshot:
    detector_id: str
    state: DetectorLifecycleState
    version: str | None
    model_id: str | None
    revision: str | None
    reason_code: str | None = None


class ManagedDetector(Protocol):
    detector_id: str
    modalities: tuple[str, ...]

    def status(self) -> DetectorStatusSnapshot: ...

    def is_ready(self) -> bool: ...
