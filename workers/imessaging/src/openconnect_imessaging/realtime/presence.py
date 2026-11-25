"""Per-device presence lease store with TTL expiry.

Closing one device socket removes only that device's lease; other active devices
for the same principal remain online until their own leases expire or log out.
"""

from __future__ import annotations

import threading
from collections import OrderedDict
from collections.abc import Callable
from dataclasses import dataclass
from datetime import UTC, datetime, timedelta


@dataclass(frozen=True)
class PresenceLease:
    """Active ephemeral presence lease for a single (tenant, principal, device)."""

    tenant_id: str
    principal_id: str
    device_id: str
    gateway_id: str
    status: str
    expires_at: datetime


class PresenceStore:
    """Thread-safe bounded presence store keyed by (tenant_id, principal_id, device_id)."""

    def __init__(
        self,
        *,
        max_entries: int = 50_000,
        clock: Callable[[], datetime] | None = None,
    ) -> None:
        self._max_entries = max_entries
        self._clock = clock or (lambda: datetime.now(UTC))
        self._lock = threading.Lock()
        self._leases: OrderedDict[tuple[str, str, str], PresenceLease] = OrderedDict()

    def _evict_expired(self, now: datetime) -> None:
        expired = [k for k, lease in self._leases.items() if lease.expires_at <= now]
        for key in expired:
            self._leases.pop(key, None)

    def upsert(
        self,
        *,
        tenant_id: str,
        principal_id: str,
        device_id: str,
        gateway_id: str,
        status: str,
        ttl_seconds: int,
    ) -> PresenceLease | None:
        """Update or remove a device presence lease. Returns None when status == 'offline'."""
        now = self._clock()
        key = (tenant_id, principal_id, device_id)
        with self._lock:
            self._evict_expired(now)
            if status == "offline":
                self._leases.pop(key, None)
                return None
            lease = PresenceLease(
                tenant_id=tenant_id,
                principal_id=principal_id,
                device_id=device_id,
                gateway_id=gateway_id,
                status=status,
                expires_at=now + timedelta(seconds=ttl_seconds),
            )
            if key not in self._leases and len(self._leases) >= self._max_entries:
                self._leases.popitem(last=False)
            self._leases[key] = lease
            self._leases.move_to_end(key)
            return lease

    def active_devices(self, tenant_id: str, principal_id: str) -> list[PresenceLease]:
        """Return all non-expired device leases for (tenant_id, principal_id)."""
        now = self._clock()
        with self._lock:
            self._evict_expired(now)
            return [
                lease
                for (t_id, p_id, _), lease in self._leases.items()
                if t_id == tenant_id and p_id == principal_id
            ]
