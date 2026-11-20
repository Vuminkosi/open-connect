"""Single-use Cloud inference execution boundary for an injected local runtime."""
from __future__ import annotations

import base64
import binascii
import json
import re
import threading
from typing import Literal, Protocol
from uuid import UUID

from pydantic import BaseModel, ConfigDict, Field, ValidationError
from openconnect_inference.cloud_consumption import CloudConsumptionClient, _unique_object


_slot = threading.Lock()


class _Envelope(BaseModel):
    model_config = ConfigDict(extra="forbid", strict=True)
    schemaVersion: Literal["openconnect.cloud.worker.inference/v1"]
    reservationId: str
    claimId: str
    payload: str = Field(min_length=4, max_length=174764)
    key: str = Field(min_length=44, max_length=44)


class _Message(BaseModel):
    model_config = ConfigDict(extra="forbid", strict=True)
    role: Literal["system", "user", "assistant", "tool"]
    content: str = Field(max_length=32768)


class _Input(BaseModel):
    model_config = ConfigDict(extra="forbid", strict=True)
    messages: list[_Message] = Field(min_length=1, max_length=64)
    maxTokens: int = Field(ge=1, le=4096)
    stream: bool


class _Payload(BaseModel):
    model_config = ConfigDict(extra="forbid", strict=True)
    schemaVersion: Literal["openconnect.cloud.inference.worker/v1"]
    requestId: str
    modelId: str = Field(min_length=1, max_length=256, pattern=r"^[A-Za-z0-9][A-Za-z0-9._:/-]{0,255}$")
    input: _Input


class PreparedInference(Protocol):
    input_tokens: int

    def generate(self, max_output_tokens: int) -> tuple[str, int]:
        """Generate with a hard output-token limit and return actual output-token usage."""
        ...


class InferenceRuntime(Protocol):
    model_id: str
    model_revision: str
    model_digest: str

    def prepare(self, payload: _Payload) -> PreparedInference:
        """Tokenize the exact prompt locally without running inference or contacting a provider."""
        ...


def _json(data: bytes) -> object:
    def invalid(_: str) -> None:
        raise ValueError("invalid JSON constant")

    return json.loads(data.decode("utf-8"), object_pairs_hook=_unique_object, parse_constant=invalid)


def _decode(value: str) -> bytes:
    decoded = base64.b64decode(value, validate=True)
    if base64.b64encode(decoded).decode("ascii") != value:
        raise ValueError("invalid encoding")
    return decoded


def _valid_id(value: str) -> bool:
    parsed = UUID(value)
    return parsed.int != 0 and re.fullmatch(r"[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}", value) is not None


def execute_claimed(body: bytes, runtime: InferenceRuntime | None, client: CloudConsumptionClient | None) -> dict[str, object] | None:
    """Consume exact payload, enforce budget, then settle before returning output.

    Runtime injection is not configured by the current worker; a production runtime must
    bind tokenization and generation to the same verified model and enforce the output cap.
    """
    if runtime is None or client is None or not isinstance(body, bytes) or not 0 < len(body) <= 180000:
        return None
    if not _slot.acquire(blocking=False):
        return None
    try:
        envelope = _Envelope.model_validate(_json(body))
        if not _valid_id(envelope.reservationId) or not _valid_id(envelope.claimId):
            return None
        payload_bytes, key = _decode(envelope.payload), _decode(envelope.key)
        if not 0 < len(payload_bytes) <= 131072 or len(key) != 32:
            return None
        payload = _Payload.model_validate(_json(payload_bytes))
        if (not _valid_id(payload.requestId) or payload.modelId != runtime.model_id or payload.input.stream or
                not 0 < sum(len(message.content) for message in payload.input.messages) <= 65536):
            return None
        prepared = runtime.prepare(payload)
        input_tokens = prepared.input_tokens
        if type(input_tokens) is not int or input_tokens < 0:
            return None
        admission = client.consume(envelope.reservationId, envelope.claimId, payload_bytes, key)
        if (not isinstance(admission, dict) or admission.get("status") != "consumed" or
                admission.get("modelId") != payload.modelId or admission.get("modelRevision") != runtime.model_revision or
                admission.get("modelDigest") != runtime.model_digest or type(admission.get("reservedTokens")) is not int):
            return None
        reserved = admission["reservedTokens"]
        if not isinstance(reserved, int) or input_tokens + payload.input.maxTokens > reserved:
            return None
        output, output_tokens = prepared.generate(payload.input.maxTokens)
        if (not isinstance(output, str) or len(output.encode("utf-8")) > 131072 or type(output_tokens) is not int or
                not 0 <= output_tokens <= payload.input.maxTokens or input_tokens + output_tokens > reserved):
            return None
        if not client.settle(envelope.reservationId, envelope.claimId, input_tokens, output_tokens):
            return None
        return {"schemaVersion": "openconnect.cloud.inference.result/v1", "requestId": payload.requestId,
                "modelId": payload.modelId, "output": output,
                "usage": {"inputTokens": input_tokens, "outputTokens": output_tokens}}
    except (ValueError, TypeError, UnicodeError, binascii.Error, ValidationError, RecursionError, AttributeError):
        return None
    except Exception:
        # Runtime failures may contain prompt text; never return or log exception details.
        return None
    finally:
        _slot.release()
