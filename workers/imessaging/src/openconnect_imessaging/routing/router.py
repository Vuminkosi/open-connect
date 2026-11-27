"""Targeted gateway fanout for opaque encrypted message envelopes."""

from __future__ import annotations

from datetime import UTC, datetime

from openconnect_imessaging.contracts.models import ExecuteResult, RoutePayload
from openconnect_imessaging.security.auth import EventDedupCache
from openconnect_imessaging.transport.nats_transport import (
    BrokerUnavailableError,
    MessagingNatsTransport,
    QueueFullError,
)


class MessageRouter:
    """Routes gateway-authorized ciphertext envelopes to specific target gateways only."""

    def __init__(
        self,
        transport: MessagingNatsTransport,
        dedup_cache: EventDedupCache,
    ) -> None:
        self._transport = transport
        self._dedup = dedup_cache

    def route(self, tenant_id: str, payload: RoutePayload) -> tuple[str, ExecuteResult]:
        """Fan out ciphertext to targetGateways; deduplicate by (tenantId, eventId)."""
        now = datetime.now(UTC)
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

        wire_event = {
            "eventId": payload.eventId,
            "tenantId": tenant_id,
            "conversationId": payload.conversationId,
            "sequence": payload.sequence,
            "targetDeviceIds": payload.targetDeviceIds,
            "securityProfile": payload.securityProfile,
            "protocol": payload.protocol,
            "epoch": payload.epoch,
            "ciphertextBase64": payload.ciphertextBase64,
            "occurredAt": payload.occurredAt.isoformat(),
        }

        dispatched = 0
        failed: list[str] = []
        reason_code: str | None = None
        for gateway_id in dict.fromkeys(payload.targetGateways):
            try:
                self._transport.publish_live(tenant_id, gateway_id, wire_event)
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

        if failed and dispatched == 0:
            return (
                "degraded",
                ExecuteResult(
                    eventId=payload.eventId,
                    dispatchedCount=0,
                    failedTargets=failed,
                    providerStatus="provider_unavailable",
                    reasonCode=reason_code or "delivery_failed",
                    processedAt=now,
                ),
            )
        if failed:
            return (
                "degraded",
                ExecuteResult(
                    eventId=payload.eventId,
                    dispatchedCount=dispatched,
                    failedTargets=failed,
                    providerStatus="queued",
                    reasonCode=reason_code or "partial_delivery",
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
