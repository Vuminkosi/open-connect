import importlib.util
import json
from pathlib import Path

import pytest

SCRIPT = Path(__file__).resolve().parents[1] / "validate-contracts.py"
SPEC = importlib.util.spec_from_file_location("validate_contracts", SCRIPT)
VALIDATOR = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(VALIDATOR)


@pytest.mark.parametrize("mode", ["valid", "invalid_fixture", "valid_in_invalid", "missing_schema"])
def test_fixture_failures_control_exit_status(tmp_path, monkeypatch, mode):
    schema_dir = tmp_path / "contracts" / "jsonschema"
    fixture_dir = tmp_path / "contracts" / "fixtures"
    schema_dir.mkdir(parents=True)
    (fixture_dir / "valid").mkdir(parents=True)
    (fixture_dir / "invalid").mkdir()
    schema = {"$schema": "https://json-schema.org/draft/2020-12/schema", "type": "integer"}
    (schema_dir / "number.json").write_text(json.dumps(schema), encoding="utf-8")
    (fixture_dir / "valid" / "number.json").write_text('"wrong"' if mode == "invalid_fixture" else "1", encoding="utf-8")
    (fixture_dir / "invalid" / "number.json").write_text("1" if mode == "valid_in_invalid" else '"wrong"', encoding="utf-8")
    if mode == "missing_schema":
        (fixture_dir / "valid" / "missing.json").write_text("{}", encoding="utf-8")
    monkeypatch.setattr(VALIDATOR, "ROOT", tmp_path)
    monkeypatch.setattr(VALIDATOR, "SCHEMA_DIR", schema_dir)
    monkeypatch.setattr(VALIDATOR, "FIXTURE_DIR", fixture_dir)
    monkeypatch.setattr("sys.argv", ["validate-contracts.py"])
    assert VALIDATOR.main() == (0 if mode == "valid" else 1)


def test_semantic_failure_reports_cleanly_for_fixture_and_compatibility(tmp_path, monkeypatch, capsys):
    schema_dir = tmp_path / "contracts" / "jsonschema"
    fixture_dir = tmp_path / "contracts" / "fixtures"
    schema_dir.mkdir(parents=True)
    (fixture_dir / "valid").mkdir(parents=True)
    schema = {"$schema": "https://json-schema.org/draft/2020-12/schema",
              "$id": "https://example.test/security-scan-response.json", "type": "object"}
    (schema_dir / "security-scan-response.json").write_text(json.dumps(schema), encoding="utf-8")
    (fixture_dir / "valid" / "security-scan-response.json").write_text(
        json.dumps({"spans": [{"start": 4, "end": 3}]}), encoding="utf-8")
    monkeypatch.setattr(VALIDATOR, "ROOT", tmp_path)
    monkeypatch.setattr(VALIDATOR, "SCHEMA_DIR", schema_dir)
    assert VALIDATOR.validate_fixture_set(fixture_dir / "valid", True) == (1, 1)
    assert not VALIDATOR.compatibility_check(fixture_dir)
    assert "span 0: start must be less than end" in capsys.readouterr().out


@pytest.mark.parametrize(("reason", "allowed", "categories", "valid"), [
    (None, False, {"EMAIL": 1}, True),
    ("not_recorded", False, {}, True),
    ("inspection_failed", False, {}, True),
    ("sensitive_detected", False, {"EMAIL": 1}, True),
    ("sensitive_detected", False, {}, False),
    ("inspection_failed", True, {}, False),
    ("allowed", False, {}, False),
    ("private diagnostic", False, {}, False),
])
def test_host_evidence_reason_schema(reason, allowed, categories, valid):
    root = SCRIPT.parents[1] / "contracts"
    schema = json.loads((root / "jsonschema" / "host-privacy-evidence.json").read_text(encoding="utf-8"))
    value = json.loads((root / "fixtures" / "valid" / "host-privacy-evidence.json").read_text(encoding="utf-8"))
    value.update(allowed=allowed, action="Allow" if allowed else "Block", categories=categories)
    if reason is None:
        value.pop("reasonCode", None)
    else:
        value["reasonCode"] = reason
    assert VALIDATOR.Draft202012Validator(schema, format_checker=VALIDATOR.FormatChecker()).is_valid(value) is valid

def test_response_schema_rejects_unusable_classification_metadata():
    root = SCRIPT.parents[1] / "contracts"
    schema = json.loads((root / "jsonschema" / "vision-analyze-response.json").read_text(encoding="utf-8"))
    response = {"schemaVersion": "openconnect.v1", "taskId": "fixture", "kind": "vision.analyze",
                "result": {"model_id": "model", "model_fingerprint": "a" * 64, "taxonomy_version": "v1",
                           "preprocessing_version": "imagenet-center224-v1", "width": 320, "height": 240,
                           "classifications": [{"label": "tree", "class_index": 2, "score": 0.7}]}}
    validator = VALIDATOR.Draft202012Validator(schema)
    assert validator.is_valid(response)
    for field, value in [("model_id", " "), ("taxonomy_version", "bad\x00version"), ("taxonomy_version", "line\n"), ("width", 16000001)]:
        modified = json.loads(json.dumps(response))
        modified["result"][field] = value
        assert not validator.is_valid(modified)
    response["result"]["classifications"][0]["label"] = "  "
    assert not validator.is_valid(response)


@pytest.mark.parametrize("filename", ["knowledge-rerank-request.json", "knowledge-rerank-response.json"])
def test_rerank_contract_rejects_unsafe_candidate_ids(filename):
    root = SCRIPT.parents[1] / "contracts"
    schema = json.loads((root / "jsonschema" / filename).read_text(encoding="utf-8"))
    value = json.loads((root / "fixtures" / "valid" / filename).read_text(encoding="utf-8"))
    validator = VALIDATOR.Draft202012Validator(schema, format_checker=VALIDATOR.FormatChecker())
    assert validator.is_valid(value)
    candidates = value["payload" if "request" in filename else "result"]["candidates"]
    for bad in ("  ", "bad\x00id", "line\n", "x" * 257):
        candidates[0]["id"] = bad
        assert not validator.is_valid(value)
