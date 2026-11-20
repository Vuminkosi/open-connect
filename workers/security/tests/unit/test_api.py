import json
from pathlib import Path

import pytest
from fastapi.testclient import TestClient
from jsonschema import Draft202012Validator

from openconnect_security import app as app_module
from openconnect_security.app import app
from openconnect_security.config import Settings

client = TestClient(app)
ROOT = Path(__file__).resolve().parents[4]


def test_live_and_ready_are_distinct() -> None:
    assert client.get("/health/live").json() == {"status": "live"}
    assert client.get("/health/ready").json() == {"status": "ready", "capabilities": ["rules"]}


def test_scan_authentication_and_response_contract(monkeypatch) -> None:
    monkeypatch.setattr(app_module, "settings", Settings(scan_token="local-test-token"))
    request = {
        "schemaVersion": "openconnect.v1",
        "requestId": "123e4567-e89b-42d3-a456-426614174000",
        "text": "email me at a@example.org",
        "detectors": ["rules"],
    }
    assert client.post("/scan", json=request).status_code == 401
    response = client.post("/scan", json=request, headers={"Authorization": "Bearer wrong"})
    assert response.status_code == 401
    response = client.post(
        "/scan", json=request, headers={"Authorization": "Bearer local-test-token"}
    )
    assert response.status_code == 200
    schema = json.loads((ROOT / "contracts/jsonschema/security-scan-response.json").read_text())
    Draft202012Validator(schema).validate(response.json())


def test_fixtures_match_request_schema() -> None:
    schema = json.loads((ROOT / "contracts/jsonschema/security-scan.json").read_text())
    valid = json.loads((ROOT / "contracts/fixtures/valid/security-scan.json").read_text())
    invalid = json.loads((ROOT / "contracts/fixtures/invalid/security-scan.json").read_text())
    validator = Draft202012Validator(schema)
    assert validator.is_valid(valid)
    assert not validator.is_valid(invalid)


@pytest.mark.parametrize(
    "detector,flag",
    [
        ("gliner-pii", "gliner_enabled"),
        ("deberta-injection", "deberta_enabled"),
        ("qwen3guard", "qwen3guard_enabled"),
        ("paddle-ocr", "paddleocr_enabled"),
        ("yunet", "yunet_enabled"),
    ],
)
@pytest.mark.parametrize("fake", [False, True])
def test_unimplemented_detector_is_never_advertised_or_attributed(
    monkeypatch, detector, flag, fake
):
    monkeypatch.setenv("OC_ENVIRONMENT", "Development")
    monkeypatch.setattr(
        app_module,
        "settings",
        Settings(scan_token="local-test-token", fake_engine=fake, **{flag: True}),
    )
    readiness = client.get("/health/ready").json()
    assert readiness["status"] == "unready"
    assert readiness["reason"] == "detector_not_implemented"
    assert readiness["capabilities"] == ["rules"]
    for requested in ([detector], ["rules", detector]):
        response = client.post(
            "/scan",
            json={
                "schemaVersion": "openconnect.v1",
                "requestId": "123e4567-e89b-42d3-a456-426614174000",
                "text": "Contact Thandi Mokoena",
                "detectors": requested,
            },
            headers={"Authorization": "Bearer local-test-token"},
        )
        assert response.status_code == 503


def test_duplicate_rules_are_attributed_to_the_rules_detector(monkeypatch):
    monkeypatch.setattr(app_module, "settings", Settings(scan_token="local-test-token"))
    response = client.post(
        "/scan",
        json={
            "schemaVersion": "openconnect.v1",
            "requestId": "123e4567-e89b-42d3-a456-426614174000",
            "text": "a@example.org",
            "detectors": ["rules", "rules"],
        },
        headers={"Authorization": "Bearer local-test-token"},
    )
    assert response.status_code == 200
    payload = response.json()
    assert payload["detectorId"] == "rules"
    assert payload["detectorVersion"] == app_module.RULES_DETECTOR_VERSION
    schema = json.loads((ROOT / "contracts/jsonschema/security-scan-response.json").read_text())
    Draft202012Validator(schema).validate(payload)
