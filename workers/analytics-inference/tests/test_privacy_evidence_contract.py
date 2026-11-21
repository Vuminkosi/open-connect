"""Verify the content-free host evidence contract used by Cloud ingestion."""

import json
from pathlib import Path
from uuid import uuid4

from jsonschema import Draft202012Validator, FormatChecker


def test_host_evidence_schema_rejects_content_and_unknown_categories():
    root = Path(__file__).resolve().parents[3]
    schema = json.loads((root / "contracts/jsonschema/host-privacy-evidence.json").read_text(encoding="utf-8"))
    Draft202012Validator.check_schema(schema)
    validator = Draft202012Validator(schema, format_checker=FormatChecker())
    evidence = {"schemaVersion": "openconnect.privacy-evidence.v1", "eventId": str(uuid4()),
                "tenantId": "01ARZ3NDEKTSV4RRFFQ69G5FAV", "destination": "Output", "allowed": False,
                "action": "Block", "categories": {"EMAIL": 2}}
    assert validator.is_valid(evidence)
    assert not validator.is_valid({**evidence, "allowed": True})
    assert not validator.is_valid({**evidence, "content": "sensitive"})
    assert not validator.is_valid({**evidence, "categories": {"sensitive name": 1}})
    assert not validator.is_valid({**evidence, "categories": {"EMAIL": 20001}})
    assert not validator.is_valid({**evidence, "tenantId": "00000000000000000000000000"})
