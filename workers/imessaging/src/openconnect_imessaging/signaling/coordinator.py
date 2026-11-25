"""Targeted call signaling fanout for messaging.signal."""

from __future__ import annotations

from datetime import UTC, datetime

from openconnect_imessaging.contracts.models import ExecuteResult, SignalPayload
from openconnect_imessaging.security.auth import EventDedupCache
from openconnect_imessaging.transport.nats_transport import (
    BrokerUnavailableError,
    MessagingNatsTransport,
    QueueFullError,
)


class CallSignalingRouter:
    """Routes opaque call signaling frames to explicitly authorized target gateways."""

    def __init__(
        self,
        transport: MessagingNatsTransport,
        dedup_cache: EventDedupCache,
    ) -> None:
        self._transport = transport
        self._dedup = dedup_cache

    def dispatch(self, tenant_id: str, payload: SignalPayload) -> tuple[str, ExecuteResult]:
        now = datetime.now(UTC)
        if payload.expiresAt is not None and now >= payload.expiresAt:
            return (
                "rejected",
                ExecuteResult(
                    eventId=payload.eventId,
                    dispatchedCount=0,
                    failedTargets=list(payload.targetGateways),
                    reasonCode="signal_expired",
                    processedAt=now,
                ),
            )

        if not self._dedup.is_first_seen(tenant_id, payload.eventId):
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

        wire_signal = {
            "eventId": payload.eventId,
            "tenantId": tenant_id,
            "callId": payload.callId,
            "conversationId": payload.conversationId,
            "signalType": payload.signalType,
            "targetDeviceIds": payload.targetDeviceIds,
            "opaquePayloadBase64": payload.opaquePayloadBase64,
            "occurredAt": payload.occurredAt.isoformat(),
        }

        dispatched = 0
        failed: list[str] = []
        reason_code: str | None = None
        for gateway_id in dict.fromkeys(payload.targetGateways):
            try:
                self._transport.publish_live(tenant_id, gateway_id, wire_signal)
                dispatched += 1
            except QueueFullError:
                failed.append(gateway_id)
                reason_code = "queue_overflow"
            except BrokerUnavailableError:
                failed.append(gateway_id)
                reason_code = "broker_unavailable"
            except ValueError:
                failed.append(gateway_id)
                reason_code = "invalid_target"

        if failed:
            return (
                "degraded",
                ExecuteResult(
                    eventId=payload.eventId,
                    dispatchedCount=dispatched,
                    failedTargets=failed,
                    providerStatus="provider_unavailable" if dispatched == 0 else "queued",
                    reasonCode=reason_code or "signal_delivery_failed",
                    processedAt=now,
                ),
            )

        return (
            "accepted",
            ExecuteResult(
                eventId=payload.eventId,
                dispatchedCount=dispatched,
                failedTargets=[],
                providerStatus="sent",
                processedAt=now,
            ),
        )
