"""Validate, consume and settle a Cloud claim around one exact translation payload."""

from __future__ import annotations

import base64
import json
import re
from collections.abc import Callable
from typing import Protocol
from uuid import UUID

from pydantic import BaseModel, ConfigDict, Field

from openconnect_nllb.languages import is_supported_pair
from openconnect_nllb.schemas import ExecuteRequest, ExecuteResponse


class ClaimClient(Protocol):
    def consume(self, reservation_id: str, claim_id: str, payload: bytes, key: bytes) -> str: ...
    def complete(self, reservation_id: str, claim_id: str) -> str: ...


class CloudEnvelope(BaseModel):
    model_config = ConfigDict(extra="forbid")
    schemaVersion: str
    reservationId: UUID
    claimId: UUID
    payload: str = Field(min_length=4, max_length=87384)
    key: str = Field(min_length=44, max_length=44)


def _unique(pairs: list[tuple[str, object]]) -> dict[str, object]:
    result: dict[str, object] = {}
    for name, value in pairs:
        if name in result:
            raise ValueError("duplicate property")
        result[name] = value
    return result


def _parse(data: bytes) -> object:
    def invalid_constant(_value: str) -> None:
        raise ValueError("invalid JSON constant")

    return json.loads(data.decode("utf-8"), object_pairs_hook=_unique, parse_constant=invalid_constant)


def _decode(value: str) -> bytes:
    decoded = base64.b64decode(value, validate=True)
    if base64.b64encode(decoded).decode("ascii") != value:
        raise ValueError("noncanonical base64")
    return decoded


def _well_formed(value: str) -> bool:
    return not any(0xD800 <= ord(char) <= 0xDFFF for char in value)


def execute_claimed_translation(
    body: bytes,
    client: ClaimClient | None,
    execute: Callable[[ExecuteRequest], ExecuteResponse],
    *,
    model_revision: str,
    tokenizer_hash: str,
) -> ExecuteResponse | None:
    """Do not execute before consumption or release a result before settlement."""
    if (client is None or not isinstance(body, bytes) or not 0 < len(body) <= 100000 or
            not isinstance(model_revision, str) or not model_revision.strip() or len(model_revision) > 256 or
            not isinstance(tokenizer_hash, str) or not re.fullmatch(r"[0-9a-f]{64}", tokenizer_hash)):
        return None
    try:
        envelope = CloudEnvelope.model_validate(_parse(body))
        if (envelope.schemaVersion != "openconnect.cloud.worker.translation/v1" or
                envelope.reservationId.int == 0 or envelope.claimId.int == 0):
            return None
        payload, key = _decode(envelope.payload), _decode(envelope.key)
        if not 0 < len(payload) <= 65536 or len(key) != 32:
            return None
        parsed = _parse(payload)
        if not isinstance(parsed, dict) or set(parsed) != {"schemaVersion", "taskId", "input"}:
            return None
        raw_input = parsed["input"]
        if not isinstance(raw_input, dict) or set(raw_input) != {"text", "sourceLang", "targetLang"}:
            return None
        request = ExecuteRequest.model_validate(parsed)
        if (str(UUID(request.taskId)) != request.taskId.lower() or UUID(request.taskId).int == 0 or
                request.leaseId is not None or request.input.inputRef is not None or
                request.input.text is None or not request.input.text.strip() or
                not _well_formed(request.input.text) or
                not is_supported_pair(request.input.sourceLang, request.input.targetLang)):
            return None
        if client.consume(str(envelope.reservationId), str(envelope.claimId), payload, key) != "consumed":
            return None
        response = execute(request)
        result = response.result
        if (response.schemaVersion != "openconnect.v1" or response.taskId != request.taskId or
                response.status != "completed" or response.error is not None or result is None or
                result.engine != "real" or result.engineWarning is not None or
                result.sourceLang != request.input.sourceLang or result.targetLang != request.input.targetLang or
                result.modelRevision != model_revision or result.tokenizerHash != tokenizer_hash or
                not result.translatedText.strip() or len(result.translatedText) > 20000 or
                not _well_formed(result.translatedText)):
            return None
        if client.complete(str(envelope.reservationId), str(envelope.claimId)) not in ("completed", "already_completed"):
            return None
        return response
    except Exception:
        return None
