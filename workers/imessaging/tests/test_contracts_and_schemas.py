"""Contract and JSON Schema validation tests for OpenConnect IMessaging."""

from __future__ import annotations

import json
from pathlib import Path

import jsonschema
import pytest
from pydantic import ValidationError
from referencing import Registry, Resource

from openconnect_imessaging.contracts.models import (
    EnrollmentQrPayload,
    ExecuteRequest,
    ExecuteResponse,
    PushWakeupPayload,
    RealtimeEventEnvelope,
)

REPO_ROOT = Path(__file__).resolve().parents[3]
SCHEMA_DIR = REPO_ROOT / "contracts" / "jsonschema"
FIXTURE_DIR = REPO_ROOT / "contracts" / "fixtures" / "imessaging"


def _schema_registry() -> Registry:
    resources = []
    for path in SCHEMA_DIR.glob("imessaging-*.json"):
        doc = json.loads(path.read_text(encoding="utf-8"))
        resources.append((doc["$id"], Resource.from_contents(doc)))
    return Registry().with_resources(resources)


def _validate_against_schema(schema_name: str, instance: dict) -> None:
    schema = json.loads((SCHEMA_DIR / schema_name).read_text(encoding="utf-8"))
    validator = jsonschema.Draft202012Validator(schema, registry=_schema_registry())
    validator.validate(instance)


@pytest.mark.parametrize(
    ("fixture_name", "schema_name", "model_cls"),
    [
        ("execute-route-request-valid.json", "imessaging-execute-request-v1.json", ExecuteRequest),
        (
            "execute-presence-request-valid.json",
            "imessaging-execute-request-v1.json",
            ExecuteRequest,
        ),
        ("execute-signal-request-valid.json", "imessaging-execute-request-v1.json", ExecuteRequest),
        ("execute-push-request-valid.json", "imessaging-execute-request-v1.json", ExecuteRequest),
        (
            "execute-route-response-valid.json",
            "imessaging-execute-response-v1.json",
            ExecuteResponse,
        ),
        ("event-message-committed-valid.json", "imessaging-event-v1.json", RealtimeEventEnvelope),
        ("enrollment-qr-valid.json", "imessaging-enrollment-qr-v1.json", EnrollmentQrPayload),
        ("push-wakeup-valid.json", "imessaging-push-wakeup-v1.json", PushWakeupPayload),
    ],
)
def test_golden_valid_fixtures_match_schemas_and_models(
    fixture_name: str,
    schema_name: str,
    model_cls: type,
) -> None:
    data = json.loads((FIXTURE_DIR / fixture_name).read_text(encoding="utf-8"))
    _validate_against_schema(schema_name, data)
    parsed = model_cls.model_validate(data)
    assert parsed is not None


def test_invalid_plaintext_push_fixture_rejected_by_schema_and_model() -> None:
    data = json.loads((FIXTURE_DIR / "push-wakeup-invalid-plaintext.json").read_text("utf-8"))
    with pytest.raises(jsonschema.ValidationError):
        _validate_against_schema("imessaging-push-wakeup-v1.json", data)
    with pytest.raises(ValidationError):
        PushWakeupPayload.model_validate(data)


@pytest.mark.parametrize(
    "bad_body",
    [
        "Open https://gateway.company.example/thread/1",
        "Bearer eyJhbGciOiJIUzI1NiJ9.payload.sig",
        "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTYifQ",
        "Contact alice@company.example now",
        "-----BEGIN PRIVATE KEY-----",
        "Line1\nLine2",
    ],
)
def test_push_wakeup_rejects_sensitive_content_in_display_text(bad_body: str) -> None:
    with pytest.raises(ValidationError):
        PushWakeupPayload(
            notificationId="notif-1",
            profileHandle="prof-1",
            eventClass="message",
            displayTitle="New message",
            displayBody=bad_body,
            ttlSeconds=60,
        )


@pytest.mark.parametrize(
    "bad_url",
    [
        "http://gateway.company.example/",
        "https://user:pass@gateway.company.example/",
        "https://gateway.company.example/#fragment",
        "https://gateway.company.example/?token=leak",
    ],
)
def test_enrollment_qr_rejects_unsafe_gateway_urls(bad_url: str) -> None:
    with pytest.raises(ValidationError):
        EnrollmentQrPayload(
            baseGatewayUrl=bad_url,
            tenantId="tenant-1",
            applicationId="app-1",
            pairingId="pair-1",
            enrollmentToken="A" * 44,
            expiresAt="2026-11-25T12:02:00Z",
        )
