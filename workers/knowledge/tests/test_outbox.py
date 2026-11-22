import os
from concurrent.futures import ThreadPoolExecutor
from uuid import uuid4

import psycopg
import pytest
from psycopg.conninfo import conninfo_to_dict

from openconnect_knowledge.outbox import MAX_ATTEMPTS, PostgresOutbox
from openconnect_knowledge.reconciliation import DeliveryStatus, run_once

DSN = os.getenv("OC_TEST_KNOWLEDGE_PG_DSN")
pytestmark = pytest.mark.skipif(not DSN, reason="Set OC_TEST_KNOWLEDGE_PG_DSN for disposable PostgreSQL tests")


@pytest.fixture
def events():
    settings = conninfo_to_dict(DSN)
    assert settings.get("dbname") == "openconnect_test" and settings.get("host") in {"127.0.0.1", "::1", "localhost"}
    tenant = str(uuid4())
    with psycopg.connect(DSN) as connection:
        connection.execute("""INSERT INTO openconnect_knowledge.documents
            (tenant_id, application_id, collection_id, document_id, generation, deleted)
            VALUES (%s, 'app', 'collection', 'doc', 2, true)""", (tenant,))
        connection.execute("""INSERT INTO openconnect_knowledge.outbox
            (tenant_id, application_id, collection_id, document_id, generation, operation)
            VALUES (%s, 'app', 'collection', 'doc', 1, 'upsert'),
                   (%s, 'app', 'collection', 'doc', 2, 'delete')""", (tenant, tenant))
    return PostgresOutbox(DSN), tenant


def test_claims_are_tenant_scoped_ordered_and_fenced(events):
    store, tenant = events
    with ThreadPoolExecutor(max_workers=2) as pool:
        claims = list(pool.map(lambda _: store.claim(tenant), range(2)))
    claim = next(item for item in claims if item is not None)
    assert sum(item is not None for item in claims) == 1 and claim.generation == 1
    assert not store.heartbeat("other-tenant", claim.event_id, claim.token)
    assert not store.heartbeat(tenant, claim.event_id, uuid4())
    assert store.heartbeat(tenant, claim.event_id, claim.token)
    assert not store.finish("other-tenant", claim.event_id, claim.token, success=True)
    assert not store.finish(tenant, claim.event_id, uuid4(), success=True)
    assert store.finish(tenant, claim.event_id, claim.token, success=True)
    assert not store.finish(tenant, claim.event_id, claim.token, success=True)
    second = store.claim(tenant)
    assert second.generation == 2 and second.operation == "delete"


def test_same_generation_events_are_serialized_by_event_id(events):
    store, tenant = events
    with psycopg.connect(DSN) as connection:
        connection.execute("""INSERT INTO openconnect_knowledge.outbox
            (tenant_id, application_id, collection_id, document_id, generation, operation)
            VALUES (%s, 'app', 'collection', 'doc', 1, 'delete')""", (tenant,))
    first = store.claim(tenant)
    assert first.generation == 1 and first.operation == "upsert"
    assert store.claim(tenant) is None
    assert store.finish(tenant, first.event_id, first.token, success=True)
    second = store.claim(tenant)
    assert second.generation == 1 and second.operation == "delete"
    assert store.claim(tenant) is None
    assert store.finish(tenant, second.event_id, second.token, success=True)
    assert store.claim(tenant).generation == 2


def test_expired_lease_cannot_acknowledge_and_dead_letter_blocks_newer_event(events):
    store, tenant = events
    claim = store.claim(tenant)
    with psycopg.connect(DSN) as connection:
        connection.execute("""UPDATE openconnect_knowledge.outbox SET lease_until=clock_timestamp()-interval '1 second',
            attempts=%s WHERE id=%s""", (MAX_ATTEMPTS, claim.event_id))
    assert not store.finish(tenant, claim.event_id, claim.token, success=True)
    assert store.claim(tenant) is None
    with psycopg.connect(DSN) as connection:
        assert connection.execute("SELECT delivery_state FROM openconnect_knowledge.outbox WHERE id=%s",
                                  (claim.event_id,)).fetchone()[0] == "dead"


def test_host_authorized_retry_restores_order_and_new_fencing_token(events):
    store, tenant = events
    old = store.claim(tenant)
    with psycopg.connect(DSN) as connection:
        connection.execute("""UPDATE openconnect_knowledge.outbox SET lease_until=clock_timestamp()-interval '1 second',
            attempts=%s WHERE id=%s""", (MAX_ATTEMPTS, old.event_id))
    assert store.claim(tenant) is None
    assert not store.retry_dead("other-tenant", old.event_id)
    assert store.retry_dead(tenant, old.event_id)
    assert not store.retry_dead(tenant, old.event_id)
    retried = store.claim(tenant)
    assert retried.event_id == old.event_id and retried.token != old.token and retried.attempt == 1
    assert not store.finish(tenant, old.event_id, old.token, success=True)
    assert store.finish(tenant, retried.event_id, retried.token, success=True)
    assert store.claim(tenant).generation == 2


def test_failed_delivery_retries_after_backoff_with_new_token(events):
    store, tenant = events
    first = store.claim(tenant)
    assert store.finish(tenant, first.event_id, first.token, success=False)
    assert store.claim(tenant) is None
    with psycopg.connect(DSN) as connection:
        connection.execute("UPDATE openconnect_knowledge.outbox SET available_at=clock_timestamp() WHERE id=%s",
                           (first.event_id,))
    second = store.claim(tenant)
    assert second.event_id == first.event_id and second.token != first.token and second.attempt == 2
    assert not store.finish(tenant, first.event_id, first.token, success=True)


def test_crashed_owner_is_reclaimed_with_a_new_fence(events):
    store, tenant = events
    first = store.claim(tenant)
    with psycopg.connect(DSN) as connection:
        connection.execute("UPDATE openconnect_knowledge.outbox SET lease_until=clock_timestamp()-interval '1 second' "
                           "WHERE id=%s", (first.event_id,))
    assert not store.heartbeat(tenant, first.event_id, first.token)
    second = store.claim(tenant)
    assert second.event_id == first.event_id and second.attempt == 2 and second.token != first.token
    assert not store.finish(tenant, first.event_id, first.token, success=True)
    assert store.finish(tenant, second.event_id, second.token, success=True)


def test_runner_delivers_in_document_order_and_preserves_failed_event(events):
    store, tenant = events

    class Sink:
        def __init__(self):
            self.seen = []
            self.fail = True

        def apply(self, event):
            self.seen.append((event.generation, event.operation))
            if self.fail:
                raise RuntimeError("sensitive downstream error")

    sink = Sink()
    assert run_once(store, tenant, sink) is DeliveryStatus.RETRY
    assert run_once(store, tenant, sink) is DeliveryStatus.IDLE
    with psycopg.connect(DSN) as connection:
        connection.execute("""UPDATE openconnect_knowledge.outbox SET available_at=clock_timestamp()
            WHERE tenant_id=%s AND generation=1""", (tenant,))
    sink.fail = False
    assert run_once(store, tenant, sink) is DeliveryStatus.DELIVERED
    assert run_once(store, tenant, sink) is DeliveryStatus.DELIVERED
    assert sink.seen == [(1, "upsert"), (1, "upsert"), (2, "delete")]
