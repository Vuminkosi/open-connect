import base64
import json
from datetime import datetime, timedelta, timezone

from openconnect_analytics_inference.cloud_execution import execute_claimed

NOW = datetime(2030, 1, 1, tzinfo=timezone.utc)
ID = "11111111-1111-1111-1111-111111111111"


def envelope():
    command = {"schemaVersion": "openconnect.v1", "taskId": ID, "kind": "analytics.analyze",
               "tenantId": "01ARZ3NDEKTSV4RRFFQ69G5FAV", "issuedAt": NOW.isoformat(),
               "expiresAt": (NOW + timedelta(minutes=1)).isoformat(),
               "payload": {"feature": "test", "unit": "kg", "lineage": "fixture", "values": [1.0]}}
    raw = json.dumps(command).encode()
    value = {"schemaVersion": "openconnect.cloud.worker.analytics/v1", "reservationId": ID,
             "claimId": ID, "payload": base64.b64encode(raw).decode(),
             "key": base64.b64encode(bytes(32)).decode()}
    return value, raw


class Consumer:
    def __init__(self, status="consumed"):
        self.status = status
        self.calls = []

    def consume(self, *args):
        self.calls.append(args)
        return self.status


def test_consumes_exact_payload_before_execution():
    value, raw = envelope()
    consumer = Consumer()
    result = execute_claimed(json.dumps(value).encode(), consumer, lambda command: command.taskId, lambda: NOW)
    assert result == ID
    assert consumer.calls == [(ID, ID, raw, bytes(32))]


def test_unavailable_duplicate_or_invalid_claim_never_executes():
    value, _ = envelope()
    calls = []
    for status in ("unavailable", "already_consumed"):
        assert execute_claimed(json.dumps(value).encode(), Consumer(status), calls.append, lambda: NOW) is None
    value["claimId"] = "00000000-0000-0000-0000-000000000000"
    consumer = Consumer()
    assert execute_claimed(json.dumps(value).encode(), consumer, calls.append, lambda: NOW) is None
    assert not consumer.calls and not calls


def test_duplicate_json_and_expiry_after_consumption_fail_closed():
    value, _ = envelope()
    encoded = json.dumps(value).encode()
    duplicate = encoded[:-1] + b', "key": "duplicate"}'
    consumer = Consumer()
    assert execute_claimed(duplicate, consumer, lambda _: True, lambda: NOW) is None
    assert not consumer.calls
    times = iter((NOW, NOW + timedelta(minutes=2)))
    assert execute_claimed(encoded, consumer, lambda _: True, lambda: next(times)) is None
    assert len(consumer.calls) == 1


def test_consumer_failure_and_invalid_payload_do_not_execute():
    class FailedConsumer:
        def consume(self, *_args):
            raise OSError("connection failed")

    value, raw = envelope()
    assert execute_claimed(json.dumps(value).encode(), FailedConsumer(), lambda _: True, lambda: NOW) is None
    value["payload"] = base64.b64encode(raw[:-1] + b', "taskId": "other"}').decode()
    consumer = Consumer()
    assert execute_claimed(json.dumps(value).encode(), consumer, lambda _: True, lambda: NOW) is None
    assert not consumer.calls


def test_noncanonical_or_empty_tenant_never_consumes_a_claim():
    for tenant in ("not-a-tenant", "00000000000000000000000000", "81ARZ3NDEKTSV4RRFFQ69G5FAV"):
        value, raw = envelope()
        command = json.loads(raw)
        command["tenantId"] = tenant
        value["payload"] = base64.b64encode(json.dumps(command).encode()).decode()
        consumer = Consumer()
        assert execute_claimed(json.dumps(value).encode(), consumer, lambda _: True, lambda: NOW) is None
        assert not consumer.calls
