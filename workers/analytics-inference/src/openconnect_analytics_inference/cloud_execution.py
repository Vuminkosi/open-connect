"""Validate exact analytics payload bytes before a one-use Cloud claim is consumed."""

from __future__ import annotations

import base64
import binascii
import re
from datetime import datetime, timedelta, timezone
from typing import TYPE_CHECKING, Callable, Protocol, TypeVar
from uuid import UUID

from pydantic import BaseModel, ConfigDict, Field, ValidationError
from openconnect_worker_common.contracts.strict_json import parse_signed_json

if TYPE_CHECKING:
    from openconnect_analytics_inference.app import ExecuteRequest

_MAX_PAYLOAD = 256 * 1024
_T = TypeVar("_T")


class ClaimConsumer(Protocol):
    def consume(self, reservation_id: str, claim_id: str, payload: bytes, key: bytes) -> str: ...


class _Envelope(BaseModel):
    model_config = ConfigDict(extra="forbid", strict=True)

    schemaVersion: str
    reservationId: UUID
    claimId: UUID
    payload: str = Field(min_length=4, max_length=349528, repr=False)
    key: str = Field(min_length=44, max_length=44, repr=False)


def _decode(value: str) -> bytes:
    decoded = base64.b64decode(value, validate=True)
    if base64.b64encode(decoded).decode("ascii") != value:
        raise ValueError("noncanonical base64")
    return decoded


def execute_claimed(
    body: bytes,
    consumer: ClaimConsumer | None,
    execute: Callable[[ExecuteRequest], _T],
    now: Callable[[], datetime] = lambda: datetime.now(timezone.utc),
) -> _T | None:
    """Consume exactly once before executing; ambiguous claim results never execute."""
    if consumer is None or not isinstance(body, bytes) or not 0 < len(body) <= 350000:
        return None
    try:
        from openconnect_analytics_inference.app import ExecuteRequest

        envelope = _Envelope.model_validate(parse_signed_json(body), strict=False)
        if (envelope.schemaVersion != "openconnect.cloud.worker.analytics/v1"
                or envelope.reservationId.int == 0 or envelope.claimId.int == 0):
            return None
        payload, key = _decode(envelope.payload), _decode(envelope.key)
        if not 0 < len(payload) <= _MAX_PAYLOAD or len(key) != 32:
            return None
        command = ExecuteRequest.model_validate(parse_signed_json(payload))
        current = now()
        if (UUID(command.taskId).int == 0
                or not re.fullmatch(r"[0-7][0-9A-HJKMNP-TV-Z]{25}", command.tenantId)
                or command.tenantId == "0" * 26 or command.issuedAt > current
                or command.expiresAt <= current or command.expiresAt <= command.issuedAt
                or command.expiresAt - command.issuedAt > timedelta(minutes=5)):
            return None
        try:
            if consumer.consume(str(envelope.reservationId), str(envelope.claimId), payload, key) != "consumed":
                return None
        except Exception:
            return None
        if command.expiresAt <= now():
            return None
    except (ValueError, TypeError, UnicodeError, binascii.Error, ValidationError, RecursionError, OverflowError):
        return None
    return execute(command)
