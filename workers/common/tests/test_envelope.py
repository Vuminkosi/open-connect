"""Envelope contract tests, including conformance with the checked-in JSON schema."""

import json
from datetime import timedelta
from pathlib import Path

import pytest
from jsonschema import Draft202012Validator
from pydantic import ValidationError

from openconnect_worker_common.contracts.envelope import WorkerEnvelope

ROOT = Path(__file__).resolve().parents[3]


def test_envelope_roundtrip(envelope: WorkerEnvelope) -> None:
    assert envelope.schemaVersion == "openconnect.v1"
    assert not envelope.is_expired()


def test_envelope_rejects_expired_ordering(envelope_kwargs: dict) -> None:
    envelope_kwargs["expiresAt"] = envelope_kwargs["issuedAt"] - timedelta(seconds=1)
    with pytest.raises(ValidationError):
        WorkerEnvelope(**envelope_kwargs)


def test_envelope_is_expired(envelope: WorkerEnvelope) -> None:
    assert envelope.is_expired(now=envelope.expiresAt + timedelta(seconds=1))


def test_envelope_rejects_bad_kind(envelope_kwargs: dict) -> None:
    envelope_kwargs["expiresAt"] = envelope_kwargs["issuedAt"] + timedelta(minutes=5)
    envelope_kwargs["kind"] = "Security_Scan!"
    with pytest.raises(ValidationError):
        WorkerEnvelope(**envelope_kwargs)


def test_envelope_forbids_extra(envelope_kwargs: dict) -> None:
    envelope_kwargs["expiresAt"] = envelope_kwargs["issuedAt"] + timedelta(minutes=5)
    envelope_kwargs["surprise"] = "nope"
    with pytest.raises(ValidationError):
        WorkerEnvelope(**envelope_kwargs)


def test_envelope_matches_jsonschema(envelope: WorkerEnvelope) -> None:
    schema = json.loads((ROOT / "contracts/jsonschema/worker-envelope.json").read_text())
    Draft202012Validator(schema).validate(json.loads(envelope.model_dump_json(exclude_none=True)))
