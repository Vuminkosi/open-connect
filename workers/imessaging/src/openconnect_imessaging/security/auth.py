"""Bearer + HMAC-SHA256 verification with key rotation, timestamp checks, and replay caches."""

from __future__ import annotations

import threading
import time
from collections import OrderedDict
from datetime import UTC, datetime

from openconnect_worker_common.security.identity import verify_bearer
from openconnect_worker_common.security.signing import verify_bytes


def verify_hmac_with_rotation(
    raw_body: bytes,
    signature_header: str | None,
    signing_keys: dict[str, bytes],
    key_id: str | None = None,
) -> bool:
    """Verify hex HMAC-SHA256 over exact serialized bytes, supporting key rotation."""
    if not signature_header or not signing_keys:
        return False
    signature = signature_header.strip().lower()
    if len(signature) != 64:
        return False

    if key_id is not None:
        key = signing_keys.get(key_id.strip())
        if key is None:
            return False
        return verify_bytes(raw_body, signature, key)

    matched = False
    for key in signing_keys.values():
        if verify_bytes(raw_body, signature, key):
            matched = True
    return matched


def verify_bearer_and_hmac(
    *,
    authorization: str | None,
    expected_token: str | None,
    raw_body: bytes,
    signature_header: str | None,
    signing_keys: dict[str, bytes],
    key_id: str | None = None,
) -> tuple[bool, str]:
    """Fail-closed Bearer + HMAC-SHA256 verification."""
    if not verify_bearer(authorization, expected_token):
        return False, "invalid_bearer"
    if not verify_hmac_with_rotation(raw_body, signature_header, signing_keys, key_id=key_id):
        return False, "invalid_signature"
    return True, "ok"


def verify_timestamp_window(
    issued_at: datetime,
    expires_at: datetime,
    *,
    now: datetime | None = None,
    max_clock_skew_seconds: int = 60,
    max_ttl_seconds: int = 300,
) -> tuple[bool, str]:
    """Enforce bounded issue/expiry window (fail closed on expired or future-dated envelopes)."""
    current = now or datetime.now(UTC)
    if issued_at.tzinfo is None or expires_at.tzinfo is None:
        return False, "naive_timestamp"
    if expires_at <= issued_at:
        return False, "invalid_window"
    if (expires_at - issued_at).total_seconds() > max_ttl_seconds:
        return False, "ttl_exceeded"
    if (issued_at - current).total_seconds() > max_clock_skew_seconds:
        return False, "issued_in_future"
    if current >= expires_at:
        return False, "expired"
    return True, "ok"


class ReplayCache:
    """Thread-safe bounded TTL replay cache for request nonces."""

    def __init__(self, *, max_entries: int = 10_000, clock=time.monotonic) -> None:
        self._max_entries = max_entries
        self._clock = clock
        self._entries: OrderedDict[str, float] = OrderedDict()
        self._lock = threading.Lock()

    def _evict_expired(self, now: float) -> None:
        expired = [k for k, exp in self._entries.items() if exp <= now]
        for key in expired:
            self._entries.pop(key, None)

    def check_and_record(self, key: str, ttl_seconds: float) -> bool:
        """Return True if key is fresh (and record it); False if it is a replay."""
        now = self._clock()
        expires_at = now + max(1.0, ttl_seconds)
        with self._lock:
            self._evict_expired(now)
            if key in self._entries:
                return False
            while len(self._entries) >= self._max_entries:
                self._entries.popitem(last=False)
            self._entries[key] = expires_at
            return True

    def clear(self) -> None:
        with self._lock:
            self._entries.clear()


class EventDedupCache:
    """Thread-safe bounded TTL deduplication cache keyed by (tenantId, eventId)."""

    def __init__(self, *, max_entries: int = 20_000, clock=time.monotonic) -> None:
        self._cache = ReplayCache(max_entries=max_entries, clock=clock)

    def is_first_seen(self, tenant_id: str, event_id: str, ttl_seconds: float = 3600.0) -> bool:
        """Return True on first observation of (tenant_id, event_id), False on duplicate."""
        return self._cache.check_and_record(f"{tenant_id}:{event_id}", ttl_seconds)

    def clear(self) -> None:
        self._cache.clear()
