import json
from pathlib import Path

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
