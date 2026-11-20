"""Cloud execution envelope validation and single-use admission."""

from __future__ import annotations

import base64
import binascii
from functools import lru_cache
import json
import os
from pathlib import Path
from typing import Callable
from uuid import UUID

from pydantic import BaseModel, ConfigDict, Field, ValidationError

from openconnect_classification.cloud_consumption import CloudConsumptionClient, _unique_object
from openconnect_classification.label_sets import get_label_set
from openconnect_classification.schemas import ExecuteRequest, ExecuteResponse


class CloudEnvelope(BaseModel):
    model_config = ConfigDict(extra="forbid", strict=True)

    schemaVersion: str
    reservationId: UUID
    claimId: UUID
    payload: str = Field(min_length=4, max_length=174764)
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
        raise ValueError("noncanonical encoding")
    return decoded


def _parse_json(data: bytes) -> object:
    def invalid_constant(_: str) -> None:
        raise ValueError("invalid JSON constant")

    return json.loads(data.decode("utf-8"), object_pairs_hook=_unique_object, parse_constant=invalid_constant)


def execute_cloud(
    body: bytes, client: CloudConsumptionClient | None, execute: Callable[[ExecuteRequest], ExecuteResponse],
    max_text_chars: int,
) -> ExecuteResponse | None:
    """Validate and bind the exact bytes before invoking the engine once."""
    if client is None:
        return None
    try:
        envelope = CloudEnvelope.model_validate(_parse_json(body), strict=False)
        if envelope.schemaVersion != "openconnect.cloud.worker.classification/v1":
            return None
        payload = _decode(envelope.payload)
        key = _decode(envelope.key)
        if len(key) != 32 or not 0 < len(payload) <= 131072:
            return None
        request = ExecuteRequest.model_validate(_parse_json(payload))
        if (request.input.text is None or len(request.input.text) > max_text_chars or
                request.input.inputRef is not None or request.leaseId is not None):
            return None
        if UUID(request.taskId).int == 0:
            return None
        get_label_set(request.input.labelSetVersion)
        if client.consume(str(envelope.reservationId), str(envelope.claimId), payload, key) != "consumed":
            return None
    except (ValueError, TypeError, UnicodeError, binascii.Error, ValidationError, RecursionError, KeyError):
        return None
    return execute(request)
