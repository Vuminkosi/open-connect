import base64
import json
from datetime import datetime, timedelta, timezone

import pytest

from openconnect_knowledge.cloud_execution import execute_claimed

NOW = datetime(2030, 1, 1, tzinfo=timezone.utc)
ID = "11111111-1111-1111-1111-111111111111"
TENANT = "00000000000000000000000001"
KEY = b"k" * 32


def encoded(value):
    return base64.b64encode(value).decode("ascii")


def command():
    return {"schemaVersion": "openconnect.v1", "taskId": ID, "kind": "knowledge.embed", "tenantId": TENANT,
            "issuedAt": NOW.isoformat(), "expiresAt": (NOW + timedelta(minutes=1)).isoformat(),
            "payload": {"inputKind": "query", "texts": ["private text"]}}


def envelope(payload=None):
    payload = json.dumps(command()).encode() if payload is None else payload
    return {"schemaVersion": "openconnect.cloud.worker.knowledge-embed/v1", "reservationId": ID,
            "claimId": ID, "payload": encoded(payload), "key": encoded(KEY)}


class Consumer:
    def __init__(self, status="consumed"):
        self.status = status
        self.calls = []

    def consume(self, reservation_id, claim_id, payload, key):
        self.calls.append((reservation_id, claim_id, payload, key))
        return self.status


def test_executes_only_after_exact_claim_consumption():
    consumer = Consumer()
    payload = json.dumps(command()).encode()
    result = execute_claimed(json.dumps(envelope(payload)).encode(), consumer,
                             lambda value: value.payload.texts, now=lambda: NOW)
    assert result == ["private text"]
    assert consumer.calls == [(ID, ID, payload, KEY)]


@pytest.mark.parametrize("status", ["already_consumed", "unavailable", "denied"])
def test_ambiguous_or_replayed_claim_never_executes(status):
    consumer = Consumer(status)
    assert execute_claimed(json.dumps(envelope()).encode(), consumer,
                           lambda _: pytest.fail("must not execute"), now=lambda: NOW) is None
    assert len(consumer.calls) == 1


@pytest.mark.parametrize("change", [
    lambda value: value.update(schemaVersion="other"),
    lambda value: value.update(reservationId="00000000-0000-0000-0000-000000000000"),
    lambda value: value.update(key=encoded(b"short")),
    lambda value: value.update(extra="private"),
    lambda value: value.update(payload="!!!!"),
])
def test_invalid_envelope_never_consumes_claim(change):
    value = envelope()
    change(value)
    consumer = Consumer()
    assert execute_claimed(json.dumps(value).encode(), consumer, lambda _: pytest.fail("must not execute"),
                           now=lambda: NOW) is None
    assert consumer.calls == []


def test_expiry_before_or_during_consumption_never_executes():
    for times in ([NOW + timedelta(minutes=2)], [NOW, NOW + timedelta(minutes=2)]):
        remaining = iter(times)
        consumer = Consumer()
        assert execute_claimed(json.dumps(envelope()).encode(), consumer, lambda _: pytest.fail("must not execute"),
                               now=lambda: next(remaining)) is None


def test_duplicate_payload_keys_are_rejected_before_consumption():
    payload = json.dumps(command()).replace('"kind":', '"kind":"other","kind":').encode()
    consumer = Consumer()
    assert execute_claimed(json.dumps(envelope(payload)).encode(), consumer, lambda _: pytest.fail("must not execute"),
                           now=lambda: NOW) is None
    assert consumer.calls == []
