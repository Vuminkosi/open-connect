"""Redacted logging wrapper and sanitized in-process counters (zero plaintext/secrets)."""

from __future__ import annotations

import logging
import threading

from openconnect_worker_common.security.redacted_logs import get_redacted_logger


def get_logger(name: str) -> logging.Logger:
    """Return a redacted logger scoped to openconnect_imessaging."""
    return get_redacted_logger(name)


class MetricsRegistry:
    """Thread-safe sanitized operation counters without high-cardinality raw identities."""

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._counters: dict[str, int] = {}

    def increment(self, metric: str, amount: int = 1) -> None:
        with self._lock:
            self._counters[metric] = self._counters.get(metric, 0) + amount

    def snapshot(self) -> dict[str, int]:
        with self._lock:
            return dict(self._counters)
