"""Fenced, tenant-scoped claims for metadata-only index reconciliation.

Delivery is at least once. Sinks must be idempotent and reject stale document
generations: a lease cannot cancel a request already sent by an expired owner.
This module does not authorize callers or deliver events to external systems.
"""

from __future__ import annotations

from dataclasses import dataclass
from uuid import UUID, uuid4

import psycopg

from openconnect_knowledge.chunking import _identifier

MAX_ATTEMPTS = 8


@dataclass(frozen=True, repr=False)
class Claim:
    event_id: int
    tenant_id: str
    application_id: str
    collection_id: str
    document_id: str
    generation: int
    operation: str
    token: UUID
    attempt: int


class PostgresOutbox:
    """Claims one event at a time; earlier undelivered document events fence later ones."""

    def __init__(self, dsn: str):
        if not isinstance(dsn, str) or not dsn.strip():
            raise ValueError("invalid_database_configuration")
        self._dsn = dsn

    def claim(self, tenant_id: str, *, lease_seconds: int = 60) -> Claim | None:
        _identifier(tenant_id)
        if isinstance(lease_seconds, bool) or not isinstance(lease_seconds, int) or not 1 <= lease_seconds <= 300:
            raise ValueError("invalid_lease")
        token = uuid4()
        with psycopg.connect(self._dsn, connect_timeout=5) as connection:
            connection.execute("SET LOCAL statement_timeout = '5s'")
            # Expired leases consume an attempt, including when the worker crashed.
            connection.execute("""UPDATE openconnect_knowledge.outbox SET delivery_state='dead',
                lease_token=NULL, lease_until=NULL WHERE tenant_id=%s AND delivery_state='leased'
                AND lease_until <= clock_timestamp() AND attempts >= %s""", (tenant_id, MAX_ATTEMPTS))
            row = connection.execute("""WITH candidate AS (
                SELECT o.id FROM openconnect_knowledge.outbox o
                WHERE o.tenant_id=%s AND o.attempts < %s AND
                  ((o.delivery_state='pending' AND o.available_at <= clock_timestamp()) OR
                   (o.delivery_state='leased' AND o.lease_until <= clock_timestamp())) AND
                  NOT EXISTS (SELECT 1 FROM openconnect_knowledge.outbox prior
                    WHERE prior.tenant_id=o.tenant_id AND prior.application_id=o.application_id
                      AND prior.collection_id=o.collection_id AND prior.document_id=o.document_id
                      AND (prior.generation < o.generation OR
                           (prior.generation = o.generation AND prior.id < o.id))
                      AND prior.delivery_state <> 'delivered')
                ORDER BY o.id FOR UPDATE OF o SKIP LOCKED LIMIT 1
            ) UPDATE openconnect_knowledge.outbox o SET delivery_state='leased', lease_token=%s,
                lease_until=clock_timestamp() + (%s * interval '1 second'), attempts=o.attempts+1
                FROM candidate WHERE o.id=candidate.id
                RETURNING o.id, o.tenant_id, o.application_id, o.collection_id, o.document_id,
                          o.generation, o.operation, o.attempts""", (tenant_id, MAX_ATTEMPTS, token, lease_seconds)).fetchone()
        return Claim(*row[:7], token, row[7]) if row else None

    def finish(self, tenant_id: str, event_id: int, token: UUID, *, success: bool) -> bool:
        _identifier(tenant_id)
        if (isinstance(event_id, bool) or not isinstance(event_id, int) or event_id < 1
                or not isinstance(token, UUID) or not isinstance(success, bool)):
            raise ValueError("invalid_claim")
        with psycopg.connect(self._dsn, connect_timeout=5) as connection:
            connection.execute("SET LOCAL statement_timeout = '5s'")
            row = connection.execute("""UPDATE openconnect_knowledge.outbox SET
                delivery_state=CASE WHEN %s THEN 'delivered' WHEN attempts >= %s THEN 'dead' ELSE 'pending' END,
                delivered_at=CASE WHEN %s THEN clock_timestamp() ELSE NULL END,
                available_at=CASE WHEN %s THEN available_at ELSE clock_timestamp() +
                    (LEAST(300, power(2, LEAST(attempts, 8))) * interval '1 second') END,
                lease_token=NULL, lease_until=NULL
                WHERE tenant_id=%s AND id=%s AND delivery_state='leased' AND lease_token=%s
                    AND lease_until > clock_timestamp() RETURNING id""",
                (success, MAX_ATTEMPTS, success, success, tenant_id, event_id, token)).fetchone()
        return row is not None

    def heartbeat(self, tenant_id: str, event_id: int, token: UUID, *, lease_seconds: int = 60) -> bool:
        _identifier(tenant_id)
        if (isinstance(event_id, bool) or not isinstance(event_id, int) or event_id < 1
                or not isinstance(token, UUID) or isinstance(lease_seconds, bool)
                or not isinstance(lease_seconds, int) or not 1 <= lease_seconds <= 300):
            raise ValueError("invalid_claim")
        with psycopg.connect(self._dsn, connect_timeout=5) as connection:
            connection.execute("SET LOCAL statement_timeout = '5s'")
            row = connection.execute("""UPDATE openconnect_knowledge.outbox SET
                lease_until=clock_timestamp() + (%s * interval '1 second')
                WHERE tenant_id=%s AND id=%s AND delivery_state='leased' AND lease_token=%s
                    AND lease_until > clock_timestamp() RETURNING id""",
                (lease_seconds, tenant_id, event_id, token)).fetchone()
        return row is not None

    def retry_dead(self, tenant_id: str, event_id: int) -> bool:
        """Requeue a dead event; the host must authorize and audit this operation.

        Only a dead event can be reset. The normal claim path still enforces
        per-document ordering and issues a new lease token; this operation does
        not authorize a reconciliation sink or grant access to source objects.
        """
        _identifier(tenant_id)
        if isinstance(event_id, bool) or not isinstance(event_id, int) or event_id < 1:
            raise ValueError("invalid_event")
        with psycopg.connect(self._dsn, connect_timeout=5) as connection:
            connection.execute("SET LOCAL statement_timeout = '5s'")
            row = connection.execute("""UPDATE openconnect_knowledge.outbox SET
                delivery_state='pending', attempts=0, available_at=clock_timestamp(),
                lease_token=NULL, lease_until=NULL
                WHERE tenant_id=%s AND id=%s AND delivery_state='dead' RETURNING id""",
                (tenant_id, event_id)).fetchone()
        return row is not None
