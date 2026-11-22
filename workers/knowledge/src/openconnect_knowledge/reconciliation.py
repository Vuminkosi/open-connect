"""Delivery orchestration for a trusted, generation-fenced reconciliation sink.

The sink must durably apply or recognize the event before returning. It must
deduplicate retries, reject stale document generations and enforce authorized
scope and object references. Neither a claim nor an event ID grants access to
an object. Do not configure a sink that only buffers work in memory.
"""

from __future__ import annotations

from dataclasses import dataclass
from enum import Enum
from typing import Protocol

from openconnect_knowledge.outbox import Claim, PostgresOutbox


@dataclass(frozen=True, repr=False)
class ReconciliationEvent:
    """Only durable routing metadata; never document text, ACLs or lease secrets."""

    event_id: int
    tenant_id: str
    application_id: str
    collection_id: str
    document_id: str
    generation: int
    operation: str


class ReconciliationSink(Protocol):
    def apply(self, event: ReconciliationEvent) -> None:
        """Durably apply or deduplicate; raise on failure, including stale unsafe work."""
        ...


class DeliveryStatus(Enum):
    IDLE = "idle"
    DELIVERED = "delivered"
    RETRY = "retry"
    LEASE_LOST = "lease_lost"


def _event(claim: Claim) -> ReconciliationEvent:
    return ReconciliationEvent(claim.event_id, claim.tenant_id, claim.application_id, claim.collection_id,
                               claim.document_id, claim.generation, claim.operation)


def run_once(outbox: PostgresOutbox, tenant_id: str, sink: ReconciliationSink, *,
             lease_seconds: int = 60) -> DeliveryStatus:
    """Claim and deliver one event; a crash after apply causes a safe sink retry.

    The sink must bound its execution time or arrange heartbeat separately;
    expired claims can never be acknowledged by this runner. A sink exception
    is not logged or returned because it might contain protected information.
    """
    claim = outbox.claim(tenant_id, lease_seconds=lease_seconds)
    if claim is None:
        return DeliveryStatus.IDLE
    # A broken or substituted outbox must not route another tenant's event to
    # the sink. Do not acknowledge a claim outside the requested tenant scope.
    if claim.tenant_id != tenant_id:
        return DeliveryStatus.LEASE_LOST
    try:
        sink.apply(_event(claim))
    except Exception:
        if not outbox.finish(tenant_id, claim.event_id, claim.token, success=False):
            return DeliveryStatus.LEASE_LOST
        return DeliveryStatus.RETRY
    if not outbox.finish(tenant_id, claim.event_id, claim.token, success=True):
        return DeliveryStatus.LEASE_LOST
    return DeliveryStatus.DELIVERED
