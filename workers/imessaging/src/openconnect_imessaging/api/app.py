"""Private HTTP API for OpenConnect IMessaging worker.

Endpoints:
- ``GET /health/live``: liveness probe
- ``GET /health/ready``: fail-closed readiness probe
- ``GET /internal/v1/capabilities``: authenticated verified capability manifest
- ``POST /internal/v1/execute``: signed gateway-to-worker commands
- ``POST /internal/v1/relay/push``: optional authenticated push-relay ingress
"""

from __future__ import annotations

import json
from datetime import UTC, datetime

from fastapi import FastAPI, Header, HTTPException, Request, status
from fastapi.responses import JSONResponse
from openconnect_worker_common.health import liveness
from openconnect_worker_common.security.identity import verify_bearer
from openconnect_worker_common.transport.nats import NatsSettings
from pydantic import ValidationError

from openconnect_imessaging import __version__
from openconnect_imessaging.adapters.inapp import InAppChannelAdapter
from openconnect_imessaging.config import Settings
from openconnect_imessaging.contracts.models import (
    CapabilitiesResponse,
    CapabilityDescriptor,
    ExecuteRequest,
    ExecuteResponse,
    ExecuteResult,
    PresencePayload,
    PushDispatchPayload,
    RelayPushRequest,
    RoutePayload,
    SignalPayload,
)
from openconnect_imessaging.notifications.providers.base import PushProvider, PushProviderStatus
from openconnect_imessaging.notifications.providers.fcm import FcmPushProvider
from openconnect_imessaging.notifications.providers.webpush import WebPushProvider
from openconnect_imessaging.notifications.relay import PushRelayRegistry
from openconnect_imessaging.realtime.presence import PresenceStore
from openconnect_imessaging.routing.router import MessageRouter
from openconnect_imessaging.security.auth import (
    EventDedupCache,
    ReplayCache,
    verify_bearer_and_hmac,
    verify_timestamp_window,
)
from openconnect_imessaging.signaling.coordinator import CallSignalingRouter
from openconnect_imessaging.telemetry.logging import MetricsRegistry, get_logger
from openconnect_imessaging.transport.nats_transport import MessagingNatsTransport

logger = get_logger("openconnect_imessaging.api")


def create_app(
    *,
    settings: Settings | None = None,
    transport: MessagingNatsTransport | None = None,
    fcm_provider: PushProvider | None = None,
    webpush_provider: PushProvider | None = None,
    relay_registry: PushRelayRegistry | None = None,
    presence_store: PresenceStore | None = None,
) -> FastAPI:
    """Build an isolated IMessaging FastAPI application instance."""
    cfg = settings if settings is not None else Settings.from_environment()
    nats_transport = transport or MessagingNatsTransport(
        NatsSettings(url=cfg.nats_url),
        max_queue_depth=cfg.max_queue_depth,
    )
    inapp_adapter = InAppChannelAdapter(enabled=cfg.enabled)

    push_enabled = cfg.enabled and cfg.push_mode in {"direct", "relay"}
    fcm: PushProvider = fcm_provider or FcmPushProvider(
        enabled=push_enabled,
        project_id=cfg.fcm_project_id,
        credentials_path=cfg.fcm_credentials_path,
        access_token_provider=(lambda: cfg.fcm_access_token) if cfg.fcm_access_token else None,
    )
    webpush: PushProvider = webpush_provider or WebPushProvider(
        enabled=push_enabled,
        vapid_public_key=cfg.webpush_vapid_public_key,
        vapid_private_key=cfg.webpush_vapid_private_key,
        vapid_subject=cfg.webpush_subject,
    )
    providers: dict[str, PushProvider] = {
        "fcm": fcm,
        "webpush": webpush,
    }

    replay_cache = ReplayCache(max_entries=cfg.nonce_cache_size)
    dedup_cache = EventDedupCache(max_entries=cfg.event_dedup_size)
    presence = presence_store or PresenceStore(max_entries=cfg.presence_max_entries)
    router = MessageRouter(nats_transport, dedup_cache)
    signaling = CallSignalingRouter(nats_transport, dedup_cache)
    relay = relay_registry or PushRelayRegistry()
    metrics = MetricsRegistry()

    api = FastAPI(
        title="OpenConnect IMessaging Worker",
        version=__version__,
        docs_url=None,
        redoc_url=None,
        openapi_url=None,
    )

    # Expose internal collaborators on app.state for tests / inspection.
    api.state.settings = cfg
    api.state.transport = nats_transport
    api.state.presence = presence
    api.state.relay_registry = relay
    api.state.metrics = metrics

    def _ready_capability_ids() -> list[str]:
        if not cfg.enabled or not cfg.is_auth_configured:
            return []
        if cfg.nats_required and not nats_transport.is_available:
            return []
        caps: list[str] = []
        if inapp_adapter.is_ready():
            caps.extend(inapp_adapter.supported_capabilities())
        for provider in providers.values():
            if provider.is_ready():
                caps.append(provider.capability_id)
        return caps

    @api.get("/health/live")
    def health_live() -> dict[str, str]:
        return liveness()

    @api.get("/health/ready")
    def health_ready() -> JSONResponse:
        if not cfg.enabled:
            return JSONResponse(
                status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
                content={
                    "status": "not-ready",
                    "reason": "worker_disabled",
                    "capabilities": [],
                },
            )
        if not cfg.is_auth_configured:
            return JSONResponse(
                status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
                content={
                    "status": "not-ready",
                    "reason": "auth_not_configured",
                    "capabilities": [],
                },
            )
        if cfg.nats_required and not nats_transport.is_available:
            return JSONResponse(
                status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
                content={
                    "status": "not-ready",
                    "reason": "broker_unavailable",
                    "capabilities": [],
                },
            )
        caps = _ready_capability_ids()
        return JSONResponse(
            status_code=status.HTTP_200_OK,
            content={
                "status": "ready",
                "capabilities": caps,
                "pushMode": cfg.push_mode,
                "relayEnabled": cfg.relay_enabled,
            },
        )

    @api.get("/internal/v1/capabilities")
    def get_capabilities(
        authorization: str | None = Header(default=None),
    ) -> CapabilitiesResponse:
        if not cfg.enabled:
            raise HTTPException(
                status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
                detail="worker_disabled",
            )
        if not verify_bearer(authorization, cfg.worker_token):
            metrics.increment("auth.capabilities.denied")
            raise HTTPException(
                status_code=status.HTTP_401_UNAUTHORIZED,
                detail="unauthorized",
            )
        descriptors = [
            CapabilityDescriptor(
                schemaVersion="openconnect.v1",
                id=cap_id,
                kind="tool",
                state="ready",
                version=__version__,
                localOnly=True,
            )
            for cap_id in _ready_capability_ids()
        ]
        return CapabilitiesResponse(capabilities=descriptors)

    async def _read_bounded_body(request: Request) -> bytes:
        content_length = request.headers.get("content-length")
        if content_length is not None:
            try:
                if int(content_length) > cfg.max_body_bytes:
                    raise HTTPException(
                        status_code=status.HTTP_413_CONTENT_TOO_LARGE,
                        detail="payload_too_large",
                    )
            except ValueError as exc:
                raise HTTPException(
                    status_code=status.HTTP_400_BAD_REQUEST,
                    detail="invalid_content_length",
                ) from exc

        chunks: list[bytes] = []
        total = 0
        async for chunk in request.stream():
            total += len(chunk)
            if total > cfg.max_body_bytes:
                raise HTTPException(
                    status_code=status.HTTP_413_CONTENT_TOO_LARGE,
                    detail="payload_too_large",
                )
            chunks.append(chunk)
        return b"".join(chunks)

    @api.post("/internal/v1/execute")
    async def execute_command(
        request: Request,
        authorization: str | None = Header(default=None),
        x_openconnect_signature: str | None = Header(default=None),
        x_openconnect_key_id: str | None = Header(default=None),
    ) -> ExecuteResponse:
        if not cfg.enabled:
            metrics.increment("execute.disabled")
            raise HTTPException(
                status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
                detail="worker_disabled",
            )

        raw_body = await _read_bounded_body(request)
        ok, auth_reason = verify_bearer_and_hmac(
            authorization=authorization,
            expected_token=cfg.worker_token,
            raw_body=raw_body,
            signature_header=x_openconnect_signature,
            signing_keys=cfg.all_signing_keys,
            key_id=x_openconnect_key_id,
        )
        if not ok:
            metrics.increment(f"execute.auth.{auth_reason}")
            raise HTTPException(
                status_code=status.HTTP_401_UNAUTHORIZED,
                detail=auth_reason,
            )

        try:
            envelope = ExecuteRequest.model_validate_json(raw_body)
        except ValidationError as exc:
            metrics.increment("execute.invalid_payload")
            raise HTTPException(
                status_code=status.HTTP_422_UNPROCESSABLE_ENTITY,
                detail=json.loads(exc.json()),
            ) from exc

        now = datetime.now(UTC)
        time_ok, time_reason = verify_timestamp_window(
            envelope.issuedAt,
            envelope.expiresAt,
            now=now,
            max_clock_skew_seconds=cfg.max_clock_skew_seconds,
            max_ttl_seconds=cfg.max_request_ttl_seconds,
        )
        if not time_ok:
            metrics.increment(f"execute.time.{time_reason}")
            code = (
                status.HTTP_410_GONE
                if time_reason == "expired"
                else status.HTTP_401_UNAUTHORIZED
            )
            raise HTTPException(status_code=code, detail=time_reason)

        ttl = max(1.0, (envelope.expiresAt - now).total_seconds())
        nonce_key = f"exec:{envelope.tenantId}:{envelope.nonce}"
        if not replay_cache.check_and_record(nonce_key, ttl_seconds=ttl):
            metrics.increment("execute.replay_rejected")
            raise HTTPException(
                status_code=status.HTTP_409_CONFLICT,
                detail="nonce_replayed",
            )

        typed = envelope.typed_payload()
        if isinstance(typed, RoutePayload):
            resp_status, result = router.route(envelope.tenantId, typed)
        elif isinstance(typed, PresencePayload):
            lease = presence.upsert(
                tenant_id=envelope.tenantId,
                principal_id=typed.principalId,
                device_id=typed.deviceId,
                gateway_id=typed.gatewayId,
                status=typed.status,
                ttl_seconds=typed.ttlSeconds,
            )
            resp_status = "accepted"
            result = ExecuteResult(
                eventId=typed.eventId,
                dispatchedCount=1,
                failedTargets=[],
                providerStatus="sent",
                leaseExpiresAt=lease.expires_at if lease is not None else None,
                processedAt=datetime.now(UTC),
            )
        elif isinstance(typed, SignalPayload):
            resp_status, result = signaling.dispatch(envelope.tenantId, typed)
        elif isinstance(typed, PushDispatchPayload):
            resp_status, result = _handle_push_dispatch(
                tenant_id=envelope.tenantId,
                payload=typed,
                providers=providers,
                dedup_cache=dedup_cache,
            )
        else:  # pragma: no cover - guarded by Pydantic Literal
            raise HTTPException(status_code=status.HTTP_400_BAD_REQUEST, detail="unsupported_kind")

        metrics.increment(f"execute.{envelope.kind}.{resp_status}")
        logger.info(
            "imessaging command processed kind=%s status=%s taskId=%s",
            envelope.kind,
            resp_status,
            envelope.taskId,
        )
        return ExecuteResponse(
            schemaVersion="openconnect.v1",
            taskId=envelope.taskId,
            kind=envelope.kind,
            tenantId=envelope.tenantId,
            status=resp_status,  # type: ignore[arg-type]
            result=result,
        )

    @api.post("/internal/v1/relay/push")
    async def relay_push(
        request: Request,
        authorization: str | None = Header(default=None),
        x_openconnect_signature: str | None = Header(default=None),
        x_openconnect_key_id: str | None = Header(default=None),
    ) -> JSONResponse:
        if not cfg.enabled or not cfg.relay_enabled:
            raise HTTPException(
                status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
                detail="relay_disabled",
            )

        raw_body = await _read_bounded_body(request)
        try:
            relay_req = RelayPushRequest.model_validate_json(raw_body)
        except ValidationError as exc:
            raise HTTPException(
                status_code=status.HTTP_422_UNPROCESSABLE_ENTITY,
                detail=json.loads(exc.json()),
            ) from exc

        tenant_cred = relay.get_tenant(relay_req.tenantId, relay_req.applicationId)
        if tenant_cred is None:
            metrics.increment("relay.unknown_tenant")
            raise HTTPException(
                status_code=status.HTTP_403_FORBIDDEN,
                detail="relay_tenant_unauthorized",
            )

        ok, auth_reason = verify_bearer_and_hmac(
            authorization=authorization,
            expected_token=tenant_cred.bearer_token,
            raw_body=raw_body,
            signature_header=x_openconnect_signature,
            signing_keys=tenant_cred.signing_keys,
            key_id=x_openconnect_key_id,
        )
        if not ok:
            metrics.increment(f"relay.auth.{auth_reason}")
            raise HTTPException(
                status_code=status.HTTP_401_UNAUTHORIZED,
                detail=auth_reason,
            )

        now = datetime.now(UTC)
        time_ok, time_reason = verify_timestamp_window(
            relay_req.issuedAt,
            relay_req.expiresAt,
            now=now,
            max_clock_skew_seconds=cfg.max_clock_skew_seconds,
            max_ttl_seconds=cfg.max_request_ttl_seconds,
        )
        if not time_ok:
            code = (
                status.HTTP_410_GONE
                if time_reason == "expired"
                else status.HTTP_401_UNAUTHORIZED
            )
            raise HTTPException(status_code=code, detail=time_reason)

        ttl = max(1.0, (relay_req.expiresAt - now).total_seconds())
        nonce_key = f"relay:{relay_req.tenantId}:{relay_req.applicationId}:{relay_req.nonce}"
        if not replay_cache.check_and_record(nonce_key, ttl_seconds=ttl):
            raise HTTPException(
                status_code=status.HTTP_409_CONFLICT,
                detail="nonce_replayed",
            )

        registration = relay.resolve_for_tenant(
            tenant_id=relay_req.tenantId,
            application_id=relay_req.applicationId,
            registration_handle=relay_req.registrationHandle,
        )
        if registration is None:
            metrics.increment("relay.handle_unauthorized")
            raise HTTPException(
                status_code=status.HTTP_403_FORBIDDEN,
                detail="registration_handle_unauthorized",
            )

        provider = providers.get(registration.provider)
        if provider is None or not provider.is_ready():
            return JSONResponse(
                status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
                content={
                    "requestId": relay_req.requestId,
                    "status": "degraded",
                    "providerStatus": PushProviderStatus.DISABLED.value,
                    "reasonCode": "push_provider_unavailable",
                },
            )

        delivery = provider.send_wakeup(
            target_token=registration.target_token,
            payload=relay_req.pushPayload,
        )
        if delivery.status == PushProviderStatus.INVALID_TOKEN:
            relay.revoke_registration(registration.registration_handle)
        return JSONResponse(
            status_code=status.HTTP_200_OK,
            content={
                "requestId": relay_req.requestId,
                "status": "accepted" if delivery.status == PushProviderStatus.SENT else "degraded",
                "providerStatus": delivery.status.value,
                "reasonCode": delivery.reason_code,
            },
        )

    return api


def _handle_push_dispatch(
    *,
    tenant_id: str,
    payload: PushDispatchPayload,
    providers: dict[str, PushProvider],
    dedup_cache: EventDedupCache,
) -> tuple[str, ExecuteResult]:
    now = datetime.now(UTC)
    if not dedup_cache.is_first_seen(tenant_id, payload.eventId):
        return (
            "deduplicated",
            ExecuteResult(
                eventId=payload.eventId,
                dispatchedCount=0,
                failedTargets=[],
                providerStatus="deduplicated",
                processedAt=now,
            ),
        )

    provider = providers.get(payload.provider)
    if provider is None or not provider.is_ready():
        return (
            "degraded",
            ExecuteResult(
                eventId=payload.eventId,
                dispatchedCount=0,
                failedTargets=[payload.registrationHandle],
                providerStatus="disabled",
                reasonCode="push_provider_disabled",
                processedAt=now,
            ),
        )

    if not payload.targetToken:
        return (
            "rejected",
            ExecuteResult(
                eventId=payload.eventId,
                dispatchedCount=0,
                failedTargets=[payload.registrationHandle],
                providerStatus="invalid_token",
                reasonCode="missing_target_token",
                processedAt=now,
            ),
        )

    delivery = provider.send_wakeup(
        target_token=payload.targetToken,
        payload=payload.pushPayload,
    )
    if delivery.status == PushProviderStatus.SENT:
        return (
            "accepted",
            ExecuteResult(
                eventId=payload.eventId,
                dispatchedCount=1,
                failedTargets=[],
                providerStatus="sent",
                processedAt=now,
            ),
        )
    return (
        "degraded",
        ExecuteResult(
            eventId=payload.eventId,
            dispatchedCount=0,
            failedTargets=[payload.registrationHandle],
            providerStatus=delivery.status.value,  # type: ignore[arg-type]
            reasonCode=delivery.reason_code,
            processedAt=now,
        ),
    )


app = create_app()
