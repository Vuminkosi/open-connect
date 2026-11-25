"""Contract schema and golden fixture validation tests."""

from __future__ import annotations

import json
from pathlib import Path

import pytest
from jsonschema import Draft202012Validator, FormatChecker

ROOT = Path(__file__).resolve().parents[4]
SCHEMAS = ROOT / "contracts" / "jsonschema"
FIXTURES = ROOT / "contracts" / "fixtures" / "security"


def _validator(schema_name: str) -> Draft202012Validator:
    schema = json.loads((SCHEMAS / schema_name).read_text(encoding="utf-8"))
    Draft202012Validator.check_schema(schema)
    return Draft202012Validator(schema, format_checker=FormatChecker())


@pytest.mark.parametrize(
    "schema_name,fixture_rel",
    [
        ("security-inspection-request-v1.json", "valid/inspection-text-request.json"),
        ("security-inspection-response-v1.json", "valid/inspection-text-response.json"),
        ("security-inspection-response-v1.json", "valid/inspection-file-response.json"),
        ("security-inspection-response-v1.json", "valid/inspection-image-response.json"),
        ("security-inspection-response-v1.json", "valid/inspection-prompt-response.json"),
        ("security-evidence-v1.json", "valid/evidence-malware-block.json"),
    ],
)
def test_valid_security_fixtures_conform_to_schemas(schema_name: str, fixture_rel: str) -> None:
    validator = _validator(schema_name)
    payload = json.loads((FIXTURES / fixture_rel).read_text(encoding="utf-8"))
    validator.validate(payload)


@pytest.mark.parametrize(
    "schema_name,fixture_rel",
    [
        ("security-inspection-request-v1.json", "invalid/inspection-request-duplicate-detectors.json"),
        ("security-inspection-response-v1.json", "invalid/inspection-response-invalid-confidence.json"),
        ("security-evidence-v1.json", "invalid/evidence-raw-content-field.json"),
    ],
)
def test_invalid_security_fixtures_are_rejected(schema_name: str, fixture_rel: str) -> None:
    validator = _validator(schema_name)
    payload = json.loads((FIXTURES / fixture_rel).read_text(encoding="utf-8"))
    assert not validator.is_valid(payload)
