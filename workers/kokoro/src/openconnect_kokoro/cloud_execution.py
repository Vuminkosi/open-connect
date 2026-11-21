"""Validate and consume a Cloud speech claim before executing its exact payload."""

import base64
import binascii
import json
import os
from functools import lru_cache
from pathlib import Path
from typing import Callable
from uuid import UUID

from pydantic import BaseModel, ConfigDict, Field, ValidationError

from openconnect_kokoro.cloud_consumption import CloudConsumptionClient, unique_object
from openconnect_kokoro.schemas import ExecuteRequest, SynthesizePayload


class CloudEnvelope(BaseModel):
    model_config = ConfigDict(extra="forbid")
    schemaVersion: str
    reservationId: UUID
    claimId: UUID
    payload: str = Field(min_length=4, max_length=87384)
    key: str = Field(min_length=44, max_length=44)


@lru_cache(maxsize=1)
def configured_client() -> CloudConsumptionClient | None:
    enabled = os.environ.get("OPENCONNECT_CLOUD_CONSUMPTION_ENABLED", "false").lower()
    if enabled == "false":
        return None
    if enabled != "true":
        raise ValueError("invalid cloud consumption configuration")
    values = [os.environ.get(name) for name in (
        "OPENCONNECT_CLOUD_AUTHORITY_ORIGIN", "OPENCONNECT_CLOUD_AUTHORITY_CA_FILE",
        "OPENCONNECT_CLOUD_WORKER_CERT_FILE", "OPENCONNECT_CLOUD_WORKER_KEY_FILE",
    )]
    if not all(values):
        raise ValueError("incomplete cloud consumption configuration")
    origin, ca, cert, key = values
    assert origin is not None and ca is not None and cert is not None and key is not None
    return CloudConsumptionClient(origin, Path(ca), Path(cert), Path(key))


def _decode(value: str) -> bytes:
    decoded = base64.b64decode(value, validate=True)
    if base64.b64encode(decoded).decode("ascii") != value:
        raise ValueError("noncanonical base64")
    return decoded


def _parse(data: bytes) -> object:
    def invalid_constant(_: str) -> None:
        raise ValueError("invalid JSON constant")
    return json.loads(data.decode("utf-8"), object_pairs_hook=unique_object, parse_constant=invalid_constant)


def execute_cloud(body: bytes, client: CloudConsumptionClient | None,
                  execute: Callable[[ExecuteRequest], dict[str, object]]) -> dict[str, object] | None:
    if client is None:
        return None
    try:
        envelope = CloudEnvelope.model_validate(_parse(body))
        if (envelope.schemaVersion != "openconnect.cloud.worker.voice-synthesis/v1" or
                envelope.reservationId.int == 0 or envelope.claimId.int == 0):
            return None
        payload, key = _decode(envelope.payload), _decode(envelope.key)
        if not 0 < len(payload) <= 65536 or len(key) != 32:
            return None
        request = ExecuteRequest.model_validate(_parse(payload))
        speech = SynthesizePayload.model_validate(request.payload)
        if (request.schemaVersion != "openconnect.v1" or request.kind != "speech.synthesize" or
                UUID(request.taskId).int == 0 or request.idempotencyKey is not None or
                set(request.payload) != {"schemaVersion", "text", "voiceId", "locale", "format"} or
                speech.idempotencyKey is not None or speech.speechEpoch is not None or
                speech.voiceId != "af_heart" or not speech.text.strip() or len(speech.text) > 2000 or
                not speech.locale or any(0xD800 <= ord(char) <= 0xDFFF for char in speech.text + speech.locale)):
            return None
        if client.consume(str(envelope.reservationId), str(envelope.claimId), payload, key) != "consumed":
            return None
    except (ValueError, TypeError, UnicodeError, binascii.Error, ValidationError, RecursionError):
        return None
    return execute(request)
