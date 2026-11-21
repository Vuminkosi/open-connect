"""Single-use Cloud LiveUI execution with authoritative model binding."""

import base64
import binascii
import json
import re
from typing import Any, Literal
from uuid import UUID

from pydantic import BaseModel, ConfigDict

from .cloud_client import _unique
from .schemas import ActionPlan, PlanRequest


class Envelope(BaseModel):
    model_config = ConfigDict(extra="forbid", strict=True)
    schemaVersion: Literal["openconnect.cloud.worker.liveui/v1"]
    reservationId: str
    claimId: str
    payload: str
    key: str


class Payload(BaseModel):
    model_config = ConfigDict(extra="forbid", strict=True)
    schemaVersion: Literal["openconnect.cloud.liveui.worker/v1"]
    requestId: str
    modelId: str
    input: dict[str, Any]


def _json(data: bytes) -> object:
    def invalid(_: str) -> None:
        raise ValueError("invalid constant")

    return json.loads(data.decode("utf-8"), object_pairs_hook=_unique, parse_constant=invalid)


def _decode(value: str) -> bytes:
    decoded = base64.b64decode(value, validate=True)
    if base64.b64encode(decoded).decode("ascii") != value:
        raise ValueError("invalid encoding")
    return decoded


def _id(value: str) -> bool:
    return (
        re.fullmatch(r"[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}", value) is not None
        and UUID(value).int != 0
    )


def execute_claimed(body: bytes, engine, settings, client) -> dict | None:
    """Consume the exact request once, generate only after approval, and settle before returning."""
    if (
        not isinstance(body, bytes)
        or not 0 < len(body) <= 180000
        or engine is None
        or settings is None
        or client is None
    ):
        return None
    try:
        envelope = Envelope.model_validate(_json(body))
        if not _id(envelope.reservationId) or not _id(envelope.claimId):
            return None
        payload_bytes, key = _decode(envelope.payload), _decode(envelope.key)
        if not 0 < len(payload_bytes) <= 131072 or len(key) != 32:
            return None
        payload = Payload.model_validate(_json(payload_bytes))
        if (
            not _id(payload.requestId)
            or not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._:/-]{0,255}", payload.modelId)
            or payload.modelId != settings.model_id
            or set(payload.input) != {"schemaVersion", "intent", "context", "generation"}
            or not isinstance(payload.input["generation"], dict)
            or set(payload.input["generation"]) != {"maxActions", "maxOutputTokens"}
        ):
            return None
        request = PlanRequest.model_validate({"requestId": payload.requestId, **payload.input}, strict=True)
        admission = client.consume(envelope.reservationId, envelope.claimId, payload_bytes, key)
        if (
            not isinstance(admission, dict)
            or admission.get("status") != "consumed"
            or admission.get("modelId") != settings.model_id
            or admission.get("modelRevision") != settings.model_revision
            or admission.get("modelDigest") != "sha256:" + settings.model_sha256
        ):
            return None
        plan, provider = engine.generate(request)
        if (
            not isinstance(plan, ActionPlan)
            or provider not in {"deterministic", "qwen3-0.6b"}
            or plan.schemaVersion != request.schemaVersion
            or not 1 <= len(plan.actions) <= request.generation.maxActions
        ):
            return None
        allowed = {(item.target, item.action) for item in request.context.availableActions}
        supported = {"activate", "navigate", "setValue", "select", "highlight", "focus", "scroll", "inspect"}
        for action in plan.actions:
            if (
                action.type not in supported
                or (action.target, action.type) not in allowed
                or (action.type in {"setValue", "select"}) != (action.value is not None)
            ):
                return None
        result = {
            "requestId": request.requestId,
            "schemaVersion": request.schemaVersion,
            "provider": provider,
            "status": "completed",
            "plan": plan.model_dump(exclude_none=True),
        }
        if len(json.dumps(result, ensure_ascii=False).encode("utf-8")) > 131072 or not client.settle(
            envelope.reservationId, envelope.claimId
        ):
            return None
        return result
    except (ValueError, TypeError, UnicodeError, binascii.Error, RecursionError, AttributeError):
        return None
    except Exception:
        # Model and provider exceptions may contain request content.
        return None
