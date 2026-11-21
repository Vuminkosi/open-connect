"""Validate and consume an exact Cloud embedding claim before executing."""

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
    from openconnect_knowledge.app import ExecuteRequest

_MAX_PAYLOAD = 128 * 1024
_MAX_ENVELOPE = 176000
_T = TypeVar("_T")


class ClaimConsumer(Protocol):
    def consume(self, reservation_id: str, claim_id: str, payload: bytes, key: bytes) -> str: ...


class _Envelope(BaseModel):
    model_config = ConfigDict(extra="forbid", strict=True)
    schemaVersion: str
    reservationId: UUID
    claimId: UUID
    payload: str = Field(min_length=4, max_length=174764, repr=False)
    key: str = Field(min_length=44, max_length=44, repr=False)


def _decode(value: str) -> bytes:
    result = base64.b64decode(value, validate=True)
    if base64.b64encode(result).decode("ascii") != value:
        raise ValueError("noncanonical_base64")
    return result


def execute_claimed(body: bytes, consumer: ClaimConsumer | None, execute: Callable[[ExecuteRequest], _T],
                    now: Callable[[], datetime] = lambda: datetime.now(timezone.utc)) -> _T | None:
    """Execute only after an unambiguous one-use consumption of the bound bytes."""
    if consumer is None or type(body) is not bytes or not 0 < len(body) <= _MAX_ENVELOPE:
        return None
    try:
        from openconnect_knowledge.app import ExecuteRequest

        envelope = _Envelope.model_validate(parse_signed_json(body), strict=False)
        if (envelope.schemaVersion != "openconnect.cloud.worker.knowledge-embed/v1"
                or not envelope.reservationId.int or not envelope.claimId.int):
            return None
        payload, key = _decode(envelope.payload), _decode(envelope.key)
        if not 0 < len(payload) <= _MAX_PAYLOAD or len(key) != 32:
            return None
        command = ExecuteRequest.model_validate(parse_signed_json(payload))
        current = now()
        if (UUID(command.taskId).int == 0 or not re.fullmatch(r"[0-7][0-9A-HJKMNP-TV-Z]{25}", command.tenantId)
                or command.issuedAt > current or command.expiresAt <= current or command.expiresAt <= command.issuedAt
                or command.expiresAt - command.issuedAt > timedelta(minutes=5)
                or sum(len(text) for text in command.payload.texts) > 65536):
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
