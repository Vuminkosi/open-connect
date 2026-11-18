"""Capability descriptor tests, including kebab-case wire values."""

import json
from pathlib import Path

import pytest
from jsonschema import Draft202012Validator
from pydantic import ValidationError

from openconnect_worker_common.contracts.capability import (
    CapabilityDescriptor,
    CapabilityKind,
    CapabilityState,
)

ROOT = Path(__file__).resolve().parents[3]


def test_kebab_case_wire_values() -> None:
    assert CapabilityKind.SECURITY_SCAN.value == "security-scan"
    assert all("-" not in v or v == v.lower().replace("_", "-") for v in CapabilityKind)
    assert all(v == v.lower() for v in CapabilityState)


def test_only_ready_is_advertisable() -> None:
    base = {"id": "rules", "kind": "security-scan", "version": "1.0.0"}
    assert CapabilityDescriptor(**base, state="ready").is_advertisable()
    for state in ("disabled", "loading", "degraded", "failed"):
        assert not CapabilityDescriptor(**base, state=state).is_advertisable()


def test_disabled_by_default_is_not_ready() -> None:
    descriptor = CapabilityDescriptor(
        id="qwen", kind="classification", state="disabled", version="0.0.1"
    )
    assert not descriptor.is_advertisable()
    assert descriptor.localOnly is False


def test_forbids_extra() -> None:
    with pytest.raises(ValidationError):
        CapabilityDescriptor(id="x", kind="chat", state="ready", version="1", unknown="field")


def test_matches_jsonschema() -> None:
    schema = json.loads((ROOT / "contracts/jsonschema/capability.json").read_text())
    descriptor = CapabilityDescriptor(
        id="rules", kind="security-scan", state="ready", version="1.0.0"
    )
    Draft202012Validator(schema).validate(json.loads(descriptor.model_dump_json()))
