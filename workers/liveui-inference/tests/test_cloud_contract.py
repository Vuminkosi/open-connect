import json
from pathlib import Path

import pytest
from pydantic import ValidationError

from app.schemas import PlanRequest


def test_shared_cloud_fixture_maps_to_existing_worker_contract():
    root = Path(__file__).resolve().parents[3]
    fixture = json.loads((root / "contracts/fixtures/valid/cloud-liveui-request-v1.json").read_text(encoding="utf-8"))
    worker_payload = {"requestId": fixture["requestId"], **fixture["input"]}
    request = PlanRequest.model_validate(worker_payload)
    assert request.context.route == "route.root"
    assert [(item.target, item.action) for item in request.context.availableActions] == [("tasks.tab", "activate")]
    assert request.generation.maxOutputTokens == 64


def test_worker_contract_rejects_unknown_cloud_capability_metadata():
    root = Path(__file__).resolve().parents[3]
    fixture = json.loads((root / "contracts/fixtures/invalid/cloud-liveui-request-v1.json").read_text(encoding="utf-8"))
    with pytest.raises(ValidationError):
        PlanRequest.model_validate({"requestId": fixture["requestId"], **fixture["input"]})
