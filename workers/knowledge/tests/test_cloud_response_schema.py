import json
from pathlib import Path

import pytest
from jsonschema import Draft202012Validator, ValidationError


def test_cloud_embedding_response_schema_fixtures():
    root = Path(__file__).resolve().parents[3] / "contracts"
    schema = json.loads((root / "jsonschema/cloud-knowledge-embed-response-v1.json").read_text(encoding="utf-8"))
    Draft202012Validator.check_schema(schema)
    validator = Draft202012Validator(schema)
    validator.validate(json.loads((root / "fixtures/valid/cloud-knowledge-embed-response-v1.json").read_text(encoding="utf-8")))
    with pytest.raises(ValidationError):
        validator.validate(json.loads((root / "fixtures/invalid/cloud-knowledge-embed-response-v1.json").read_text(encoding="utf-8")))
    valid = json.loads((root / "fixtures/valid/cloud-knowledge-embed-response-v1.json").read_text(encoding="utf-8"))
    for invalid_model in ("  ", "model\nprivate", ""):
        valid["result"]["model_id"] = invalid_model
        with pytest.raises(ValidationError):
            validator.validate(valid)
