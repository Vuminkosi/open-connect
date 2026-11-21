from uuid import uuid4

import pytest

from openconnect_knowledge.outbox import Claim
from openconnect_knowledge.reconciliation import DeliveryStatus, run_once


class Outbox:
    def __init__(self, claim, *, finish=True):
        self.current = claim
        self.finish_result = finish
        self.acks = []

    def claim(self, tenant_id, *, lease_seconds):
        assert tenant_id == "tenant" and lease_seconds == 60
        return self.current

    def finish(self, tenant_id, event_id, token, *, success):
        self.acks.append((tenant_id, event_id, token, success))
        return self.finish_result


class Sink:
    def __init__(self, *, fail=False):
        self.events = []
        self.fail = fail

    def apply(self, event):
        self.events.append(event)
        if self.fail:
            raise RuntimeError("sensitive data must not be exposed")


@pytest.fixture
def claim():
    return Claim(5, "tenant", "app", "collection", "document", 3, "delete", uuid4(), 1)


def test_idle_does_not_call_sink():
    sink = Sink()
    assert run_once(Outbox(None), "tenant", sink) is DeliveryStatus.IDLE
    assert not sink.events


def test_durable_sink_is_called_before_ack_and_receives_no_lease_token(claim):
    sink, outbox = Sink(), Outbox(claim)
    assert run_once(outbox, "tenant", sink) is DeliveryStatus.DELIVERED
    event = sink.events[0]
    assert event.generation == 3 and event.operation == "delete" and not hasattr(event, "token")
    assert outbox.acks == [("tenant", 5, claim.token, True)]


@pytest.mark.parametrize("finish,status", [(True, DeliveryStatus.RETRY), (False, DeliveryStatus.LEASE_LOST)])
def test_sink_failure_never_acknowledges_success(claim, finish, status):
    outbox = Outbox(claim, finish=finish)
    assert run_once(outbox, "tenant", Sink(fail=True)) is status
    assert outbox.acks[0][-1] is False


def test_expired_lease_does_not_report_delivery(claim):
    assert run_once(Outbox(claim, finish=False), "tenant", Sink()) is DeliveryStatus.LEASE_LOST


def test_mismatched_tenant_claim_is_neither_delivered_nor_acknowledged():
    claim = Claim(5, "other-tenant", "app", "collection", "document", 3, "delete", uuid4(), 1)
    sink, outbox = Sink(), Outbox(claim)
    assert run_once(outbox, "tenant", sink) is DeliveryStatus.LEASE_LOST
    assert sink.events == []
    assert outbox.acks == []
