"""Negative-first security, routing, presence, signaling, push, and relay tests."""

from __future__ import annotations

import json
import secrets
from datetime import UTC, datetime, timedelta

import httpx
from fastapi.testclient import TestClient
from openconnect_worker_common.security.signing import sign_bytes
from openconnect_worker_common.transport.nats import NatsSettings

from openconnect_imessaging.api.app import create_app
from openconnect_imessaging.config import FORBIDDEN_EXTERNAL_CHANNELS, Settings
from openconnect_imessaging.notifications.providers.fcm import FcmPushProvider
from openconnect_imessaging.notifications.relay import (
    PushRelayRegistry,
    RelayRegistration,
    RelayTenantCredential,
)
from openconnect_imessaging.realtime.presence import PresenceStore
from openconnect_imessaging.transport.nats_transport import MessagingNatsTransport

TEST_TOKEN = "imsg-worker-secret-token-0123456789"
TEST_KEY_V1 = bytes.fromhex("11" * 32)
TEST_KEY_V2 = bytes.fromhex("22" * 32)


def _signed_headers(
    body: bytes,
    *,
    token: str = TEST_TOKEN,
    key: bytes = TEST_KEY_V1,
    key_id: str | None = None,
) -> dict[str, str]:
    headers = {
        "Authorization": f"Bearer {token}",
        "Content-Type": "application/json",
        "X-OpenConnect-Signature": sign_bytes(body, key),
    }
    if key_id is not None:
        headers["X-OpenConnect-Key-Id"] = key_id
    return headers


def _route_envelope(
    *,
    task_id: str = "task-1",
    event_id: str = "evt-1",
    nonce: str | None = None,
    issued_at: datetime | None = None,
    expires_at: datetime | None = None,
    tenant_id: str = "tenant-a",
) -> bytes:
    now = datetime.now(UTC)
    payload = {
        "schemaVersion": "openconnect.v1",
        "taskId": task_id,
        "kind": "messaging.route",
        "tenantId": tenant_id,
        "nonce": nonce or secrets.token_hex(16),
        "issuedAt": (issued_at or (now - timedelta(seconds=2))).isoformat(),
        "expiresAt": (expires_at or (now + timedelta(seconds=60))).isoformat(),
        "payload": {
            "eventId": event_id,
            "conversationId": "conv-1",
            "sequence": 1,
            "targetGateways": ["gw-1"],
            "targetDeviceIds": ["dev-1"],
            "securityProfile": "PrivateE2EE",
            "protocol": "mls-1.0",
            "epoch": 1,
            "ciphertextBase64": "Y2lwaGVydGV4dA==",
            "occurredAt": now.isoformat(),
        },
    }
    return json.dumps(payload, separators=(",", ":")).encode("utf-8")


def _enabled_settings(**overrides) -> Settings:
    base = {
        "enabled": True,
        "worker_token": TEST_TOKEN,
        "signing_key": TEST_KEY_V1,
        "rotation_keys": {"k1": TEST_KEY_V1, "k2": TEST_KEY_V2},
    }
    base.update(overrides)
    return Settings(**base)


def test_disabled_worker_fails_closed() -> None:
    app = create_app(settings=Settings(enabled=False))
    client = TestClient(app)

    assert client.get("/health/live").status_code == 200
    ready = client.get("/health/ready")
    assert ready.status_code == 503
    assert ready.json()["reason"] == "worker_disabled"

    assert (
        client.get(
            "/internal/v1/capabilities", headers={"Authorization": f"Bearer {TEST_TOKEN}"}
        ).status_code
        == 503
    )

    body = _route_envelope()
    assert (
        client.post("/internal/v1/execute", content=body, headers=_signed_headers(body)).status_code
        == 503
    )
    assert client.post("/internal/v1/relay/push", content=b"{}").status_code == 503


def test_unconfigured_auth_fails_closed_on_readiness() -> None:
    app = create_app(settings=Settings(enabled=True, worker_token=None, signing_key=None))
    client = TestClient(app)
    resp = client.get("/health/ready")
    assert resp.status_code == 503
    assert resp.json()["reason"] == "auth_not_configured"


def test_capabilities_advertises_only_verified_inapp_and_never_external_channels() -> None:
    app = create_app(settings=_enabled_settings(push_mode="disabled"))
    client = TestClient(app)

    assert client.get("/internal/v1/capabilities").status_code == 401
    assert (
        client.get(
            "/internal/v1/capabilities", headers={"Authorization": "Bearer wrong-token"}
        ).status_code
        == 401
    )

    resp = client.get("/internal/v1/capabilities", headers={"Authorization": f"Bearer {TEST_TOKEN}"})
    assert resp.status_code == 200
    cap_ids = {item["id"] for item in resp.json()["capabilities"]}
    assert cap_ids == {
        "messaging.inapp.send",
        "messaging.inapp.receive",
        "messaging.inapp.call",
        "messaging.device.enrollment",
    }
    assert "messaging.push.fcm" not in cap_ids
    for forbidden in FORBIDDEN_EXTERNAL_CHANNELS:
        assert all(forbidden not in cap_id for cap_id in cap_ids)


def test_capabilities_advertises_fcm_only_when_configured_and_ready() -> None:
    fcm = FcmPushProvider(
        enabled=True,
        project_id="demo-firebase-proj",
        access_token_provider=lambda: "ya29.fake-oauth-token",
    )
    app = create_app(settings=_enabled_settings(push_mode="direct"), fcm_provider=fcm)
    client = TestClient(app)

    resp = client.get("/internal/v1/capabilities", headers={"Authorization": f"Bearer {TEST_TOKEN}"})
    assert resp.status_code == 200
    cap_ids = {item["id"] for item in resp.json()["capabilities"]}
    assert "messaging.push.fcm" in cap_ids


def test_execute_rejects_bad_hmac_tampering_malformed_body_replay_and_expiry() -> None:
    app = create_app(settings=_enabled_settings(max_body_bytes=4096))
    client = TestClient(app)

    # Bad HMAC signature
    body = _route_envelope()
    bad_headers = _signed_headers(body)
    bad_headers["X-OpenConnect-Signature"] = "00" * 32
    assert client.post("/internal/v1/execute", content=body, headers=bad_headers).status_code == 401

    # Tampered body after signing
    tampered_headers = _signed_headers(body)
    tampered_body = body + b" "
    assert (
        client.post(
            "/internal/v1/execute", content=tampered_body, headers=tampered_headers
        ).status_code
        == 401
    )

    # Malformed / extra field rejected (422)
    extra_body = json.dumps({"extraField": True}).encode("utf-8")
    assert (
        client.post(
            "/internal/v1/execute", content=extra_body, headers=_signed_headers(extra_body)
        ).status_code
        == 422
    )

    # Oversized body rejected (413)
    oversized = b"x" * 5000
    assert (
        client.post(
            "/internal/v1/execute", content=oversized, headers=_signed_headers(oversized)
        ).status_code
        == 413
    )

    # Expired request rejected (410)
    now = datetime.now(UTC)
    expired_body = _route_envelope(
        issued_at=now - timedelta(seconds=120),
        expires_at=now - timedelta(seconds=10),
    )
    assert (
        client.post(
            "/internal/v1/execute", content=expired_body, headers=_signed_headers(expired_body)
        ).status_code
        == 410
    )

    # Future issuedAt rejected (401)
    future_body = _route_envelope(
        issued_at=now + timedelta(seconds=180),
        expires_at=now + timedelta(seconds=240),
    )
    assert (
        client.post(
            "/internal/v1/execute", content=future_body, headers=_signed_headers(future_body)
        ).status_code
        == 401
    )

    # Valid request with rotated key k2 succeeds, then replay of same nonce fails (409)
    fixed_nonce = secrets.token_hex(16)
    valid_body = _route_envelope(nonce=fixed_nonce, event_id="evt-unique-1")
    headers_k2 = _signed_headers(valid_body, key=TEST_KEY_V2, key_id="k2")
    first = client.post("/internal/v1/execute", content=valid_body, headers=headers_k2)
    assert first.status_code == 200
    assert first.json()["status"] == "accepted"

    replayed = client.post("/internal/v1/execute", content=valid_body, headers=headers_k2)
    assert replayed.status_code == 409
    assert replayed.json()["detail"] == "nonce_replayed"

    # Unknown key id fails closed (401)
    unknown_kid_body = _route_envelope(event_id="evt-unique-2")
    assert (
        client.post(
            "/internal/v1/execute",
            content=unknown_kid_body,
            headers=_signed_headers(unknown_kid_body, key=TEST_KEY_V2, key_id="unknown"),
        ).status_code
        == 401
    )


def test_route_deduplicates_duplicate_event_and_handles_queue_overflow() -> None:
    transport = MessagingNatsTransport(NatsSettings(), max_queue_depth=1)
    app = create_app(settings=_enabled_settings(max_queue_depth=1), transport=transport)
    client = TestClient(app)

    body1 = _route_envelope(event_id="evt-dup-1")
    r1 = client.post("/internal/v1/execute", content=body1, headers=_signed_headers(body1))
    assert r1.status_code == 200
    assert r1.json()["status"] == "accepted"
    assert r1.json()["result"]["dispatchedCount"] == 1

    # Same eventId with fresh nonce -> deduplicated, not republished
    body2 = _route_envelope(event_id="evt-dup-1")
    r2 = client.post("/internal/v1/execute", content=body2, headers=_signed_headers(body2))
    assert r2.status_code == 200
    assert r2.json()["status"] == "deduplicated"
    assert r2.json()["result"]["dispatchedCount"] == 0

    # Queue depth is 1 and already full -> new event reports degraded queue_overflow
    body3 = _route_envelope(event_id="evt-overflow-1")
    r3 = client.post("/internal/v1/execute", content=body3, headers=_signed_headers(body3))
    assert r3.status_code == 200
    assert r3.json()["status"] == "degraded"
    assert r3.json()["result"]["reasonCode"] == "queue_overflow"

    queued = transport.drain_queued()
    assert len(queued) == 1
    assert queued[0].subject == "oc.v1.messaging.live.tenant-a.gw-1"


def test_presence_ttl_and_per_device_isolation() -> None:
    clock_now = [datetime(2026, 11, 25, 12, 0, 0, tzinfo=UTC)]
    store = PresenceStore(clock=lambda: clock_now[0])
    app = create_app(settings=_enabled_settings(), presence_store=store)
    client = TestClient(app)

    now = datetime.now(UTC)
    for device_id in ("dev-desktop", "dev-mobile"):
        body = json.dumps(
            {
                "schemaVersion": "openconnect.v1",
                "taskId": f"pres-{device_id}",
                "kind": "messaging.presence",
                "tenantId": "tenant-a",
                "nonce": secrets.token_hex(16),
                "issuedAt": (now - timedelta(seconds=1)).isoformat(),
                "expiresAt": (now + timedelta(seconds=60)).isoformat(),
                "payload": {
                    "eventId": f"evt-{device_id}",
                    "principalId": "usr-1",
                    "deviceId": device_id,
                    "gatewayId": "gw-1",
                    "status": "online",
                    "conversationIds": ["conv-1"],
                    "ttlSeconds": 30,
                    "occurredAt": now.isoformat(),
                },
            },
            separators=(",", ":"),
        ).encode("utf-8")
        resp = client.post("/internal/v1/execute", content=body, headers=_signed_headers(body))
        assert resp.status_code == 200

    assert len(store.active_devices("tenant-a", "usr-1")) == 2

    # Marking desktop offline must NOT mark mobile offline
    offline_body = json.dumps(
        {
            "schemaVersion": "openconnect.v1",
            "taskId": "pres-offline",
            "kind": "messaging.presence",
            "tenantId": "tenant-a",
            "nonce": secrets.token_hex(16),
            "issuedAt": (now - timedelta(seconds=1)).isoformat(),
            "expiresAt": (now + timedelta(seconds=60)).isoformat(),
            "payload": {
                "eventId": "evt-desktop-off",
                "principalId": "usr-1",
                "deviceId": "dev-desktop",
                "gatewayId": "gw-1",
                "status": "offline",
                "conversationIds": ["conv-1"],
                "ttlSeconds": 30,
                "occurredAt": now.isoformat(),
            },
        },
        separators=(",", ":"),
    ).encode("utf-8")
    assert (
        client.post(
            "/internal/v1/execute", content=offline_body, headers=_signed_headers(offline_body)
        ).status_code
        == 200
    )
    active = store.active_devices("tenant-a", "usr-1")
    assert [lease.device_id for lease in active] == ["dev-mobile"]

    # Advance clock past 30s TTL -> mobile lease expires automatically
    clock_now[0] = clock_now[0] + timedelta(seconds=31)
    assert store.active_devices("tenant-a", "usr-1") == []


def test_fcm_dispatch_and_relay_authorization_boundaries() -> None:
    captured_fcm_bodies: list[dict] = []
    fcm_responses = [
        httpx.Response(200, json={"name": "projects/demo/messages/1"}),
        httpx.Response(404, json={"error": {"status": "NOT_FOUND", "message": "UNREGISTERED"}}),
    ]

    def handler(request: httpx.Request) -> httpx.Response:
        captured_fcm_bodies.append(json.loads(request.content.decode("utf-8")))
        return fcm_responses.pop(0)

    fcm = FcmPushProvider(
        enabled=True,
        project_id="demo-proj",
        access_token_provider=lambda: "ya29.test-oauth-token",
        http_transport=httpx.MockTransport(handler),
    )

    relay_key_a = bytes.fromhex("aa" * 32)
    registry = PushRelayRegistry()
    registry.register_tenant(
        RelayTenantCredential(
            tenant_id="tenant-a",
            application_id="app-a",
            bearer_token="relay-token-a",
            signing_keys={"v1": relay_key_a},
        )
    )
    registry.bind_registration(
        RelayRegistration(
            registration_handle="handle-a-1",
            tenant_id="tenant-a",
            application_id="app-a",
            provider="fcm",
            target_token="fcm-real-device-token-1234567890",
        )
    )
    registry.bind_registration(
        RelayRegistration(
            registration_handle="handle-b-1",
            tenant_id="tenant-b",
            application_id="app-b",
            provider="fcm",
            target_token="fcm-other-tenant-token-1234567890",
        )
    )

    app = create_app(
        settings=_enabled_settings(push_mode="relay", relay_enabled=True),
        fcm_provider=fcm,
        relay_registry=registry,
    )
    client = TestClient(app)

    now = datetime.now(UTC)

    def make_relay_body(
        *, tenant_id: str = "tenant-a", app_id: str = "app-a", handle: str = "handle-a-1"
    ) -> bytes:
        return json.dumps(
            {
                "schemaVersion": "openconnect.v1",
                "requestId": f"req-{secrets.token_hex(4)}",
                "tenantId": tenant_id,
                "applicationId": app_id,
                "registrationHandle": handle,
                "nonce": secrets.token_hex(16),
                "issuedAt": (now - timedelta(seconds=1)).isoformat(),
                "expiresAt": (now + timedelta(seconds=60)).isoformat(),
                "pushPayload": {
                    "schemaVersion": 1,
                    "notificationId": "notif-1",
                    "profileHandle": "prof-a",
                    "eventClass": "message",
                    "displayTitle": "New message",
                    "displayBody": "Open app to view",
                    "ttlSeconds": 120,
                },
            },
            separators=(",", ":"),
        ).encode("utf-8")

    # 1. Cross-tenant handle rejected with 403
    cross_body = make_relay_body(handle="handle-b-1")
    cross_resp = client.post(
        "/internal/v1/relay/push",
        content=cross_body,
        headers=_signed_headers(cross_body, token="relay-token-a", key=relay_key_a),
    )
    assert cross_resp.status_code == 403
    assert cross_resp.json()["detail"] == "registration_handle_unauthorized"

    # 2. Unauthorized tenant rejected with 403
    unauth_tenant_body = make_relay_body(tenant_id="tenant-unknown", app_id="app-x")
    assert (
        client.post(
            "/internal/v1/relay/push",
            content=unauth_tenant_body,
            headers=_signed_headers(unauth_tenant_body, token="relay-token-a", key=relay_key_a),
        ).status_code
        == 403
    )

    # 3. Valid relay push succeeds and sends strictly opaque FCM v1 payload
    ok_body = make_relay_body(handle="handle-a-1")
    ok_resp = client.post(
        "/internal/v1/relay/push",
        content=ok_body,
        headers=_signed_headers(ok_body, token="relay-token-a", key=relay_key_a),
    )
    assert ok_resp.status_code == 200
    assert ok_resp.json()["providerStatus"] == "sent"
    assert len(captured_fcm_bodies) == 1
    assert captured_fcm_bodies[0]["message"]["notification"] == {
        "title": "New message",
        "body": "Open app to view",
    }

    # 4. Second call triggers 404 UNREGISTERED -> invalid_token & auto-revokes handle
    stale_body = make_relay_body(handle="handle-a-1")
    stale_resp = client.post(
        "/internal/v1/relay/push",
        content=stale_body,
        headers=_signed_headers(stale_body, token="relay-token-a", key=relay_key_a),
    )
    assert stale_resp.status_code == 200
    assert stale_resp.json()["providerStatus"] == "invalid_token"
    assert (
        registry.resolve_for_tenant(
            tenant_id="tenant-a", application_id="app-a", registration_handle="handle-a-1"
        )
        is None
    )
