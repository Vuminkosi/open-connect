"""NATS subject validation, broker outage, signaling expiry, and redacted logging tests."""

from __future__ import annotations

import json
import logging
import secrets
from datetime import UTC, datetime, timedelta

import pytest
from fastapi.testclient import TestClient
from openconnect_worker_common.security.signing import sign_bytes
from openconnect_worker_common.transport.nats import NatsSettings

from openconnect_imessaging.api.app import create_app
from openconnect_imessaging.config import Settings
from openconnect_imessaging.telemetry.logging import get_logger
from openconnect_imessaging.transport.nats_transport import (
    MESSAGING_STREAM_CONFIG,
    MessagingNatsTransport,
    durable_subject,
    live_gateway_subject,
)


class _DisconnectedPublisher:
    def is_connected(self) -> bool:
        return False

    def publish(self, subject: str, payload: bytes, *, durable: bool = False) -> None:
        raise AssertionError("publish must not be called while disconnected")


def test_nats_stream_and_subject_conventions() -> None:
    assert MESSAGING_STREAM_CONFIG.name == "OC_MESSAGING"
    assert MESSAGING_STREAM_CONFIG.subjects == ("oc.v1.messaging.durable.>",)
    assert live_gateway_subject("tenant-a", "gw-01") == "oc.v1.messaging.live.tenant-a.gw-01"
    assert durable_subject("tenant-a", "message") == "oc.v1.messaging.durable.tenant-a.message"


@pytest.mark.parametrize("bad_token", ["", "*", ">", "a.b", "a b", "tenant\ninjected"])
def test_nats_subject_rejects_wildcards_and_injection(bad_token: str) -> None:
    with pytest.raises(ValueError):
        live_gateway_subject(bad_token, "gw-01")
    with pytest.raises(ValueError):
        live_gateway_subject("tenant-a", bad_token)


def test_broker_outage_fails_readiness_when_required_and_degrades_signaling() -> None:
    key = bytes.fromhex("33" * 32)
    token = "imsg-test-token-999"
    transport = MessagingNatsTransport(NatsSettings(), publisher=_DisconnectedPublisher())
    app = create_app(
        settings=Settings(
            enabled=True,
            worker_token=token,
            signing_key=key,
            nats_required=True,
        ),
        transport=transport,
    )
    client = TestClient(app)

    ready = client.get("/health/ready")
    assert ready.status_code == 503
    assert ready.json()["reason"] == "broker_unavailable"

    now = datetime.now(UTC)
    sig_body = json.dumps(
        {
            "schemaVersion": "openconnect.v1",
            "taskId": "sig-1",
            "kind": "messaging.signal",
            "tenantId": "tenant-a",
            "nonce": secrets.token_hex(16),
            "issuedAt": (now - timedelta(seconds=1)).isoformat(),
            "expiresAt": (now + timedelta(seconds=60)).isoformat(),
            "payload": {
                "eventId": "evt-sig-1",
                "callId": "call-1",
                "conversationId": "conv-1",
                "signalType": "invite",
                "targetGateways": ["gw-1"],
                "targetDeviceIds": ["dev-1"],
                "expiresAt": (now + timedelta(seconds=30)).isoformat(),
                "occurredAt": now.isoformat(),
            },
        },
        separators=(",", ":"),
    ).encode("utf-8")
    resp = client.post(
        "/internal/v1/execute",
        content=sig_body,
        headers={
            "Authorization": f"Bearer {token}",
            "Content-Type": "application/json",
            "X-OpenConnect-Signature": sign_bytes(sig_body, key),
        },
    )
    assert resp.status_code == 200
    assert resp.json()["status"] == "degraded"
    assert resp.json()["result"]["reasonCode"] == "broker_unavailable"


def test_redacted_logger_scrubs_secrets(caplog: pytest.LogCaptureFixture) -> None:
    logger = get_logger("openconnect_imessaging.test")
    with caplog.at_level(logging.INFO, logger="openconnect_imessaging.test"):
        logger.info("auth failed for alice@example.com Bearer supersecret1234567890")
    output = " ".join(record.getMessage() for record in caplog.records)
    assert "alice@example.com" not in output
    assert "supersecret1234567890" not in output
