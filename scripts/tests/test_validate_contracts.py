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
