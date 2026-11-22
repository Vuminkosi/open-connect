import importlib.util
import json
from pathlib import Path

from openconnect_worker_common.contracts.capability import CapabilityDescriptor

ROOT = Path(__file__).resolve().parents[3]
FIXTURES = ROOT / "contracts" / "fixtures" / "worker-capability"


def test_structured_fixture_matches_worker_descriptor_contract():
    value = json.loads((FIXTURES / "structured.json").read_text(encoding="utf-8"))
    assert [CapabilityDescriptor.model_validate(item).model_dump(mode="json") for item in value["capabilities"]] == value["capabilities"]


def test_inference_fixture_matches_existing_worker_contract():
    path = ROOT / "workers" / "inference" / "src" / "openconnect_inference" / "schemas.py"
    spec = importlib.util.spec_from_file_location("inference_capability_contract", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    value = json.loads((FIXTURES / "inference.json").read_text(encoding="utf-8"))
    assert module.CapabilityResponse.model_validate(value).model_dump(mode="json") == value
