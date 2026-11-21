import base64
import json
from types import SimpleNamespace
from uuid import uuid4

from app.cloud_execution import execute_claimed
from app.schemas import ActionPlan, PlannedAction

ID = str(uuid4())
SETTINGS = SimpleNamespace(model_id="registry/model-a", model_revision="revision-1", model_sha256="a" * 64)


def envelope():
    payload = {
        "schemaVersion": "openconnect.cloud.liveui.worker/v1",
        "requestId": ID,
        "modelId": SETTINGS.model_id,
        "input": {
            "schemaVersion": "v1",
            "intent": "Show tasks",
            "context": {"route": "route.root", "availableActions": [{"target": "tasks.tab", "action": "activate"}]},
            "generation": {"maxActions": 2, "maxOutputTokens": 64},
        },
    }
    return {
        "schemaVersion": "openconnect.cloud.worker.liveui/v1",
        "reservationId": str(uuid4()),
        "claimId": str(uuid4()),
        "payload": base64.b64encode(json.dumps(payload).encode()).decode(),
        "key": base64.b64encode(bytes(32)).decode(),
    }


class Client:
    def __init__(self):
        self.calls = []
        self.admission = {
            "status": "consumed",
            "modelId": SETTINGS.model_id,
            "modelRevision": SETTINGS.model_revision,
            "modelDigest": "sha256:" + SETTINGS.model_sha256,
        }
        self.settled = True

    def consume(self, *_):
        self.calls.append("consume")
        return self.admission

    def settle(self, *_):
        self.calls.append("settle")
        return self.settled


class Engine:
    def __init__(self):
        self.calls = 0
        self.plan = ActionPlan(schemaVersion="v1", actions=[PlannedAction(type="activate", target="tasks.tab")])

    def generate(self, _):
        self.calls += 1
        return self.plan, "deterministic"


def test_consumes_before_generating_and_settles_before_return():
    client, engine = Client(), Engine()
    result = execute_claimed(json.dumps(envelope()).encode(), engine, SETTINGS, client)
    assert result["plan"]["actions"] == [{"type": "activate", "target": "tasks.tab"}]
    assert client.calls == ["consume", "settle"]
    assert engine.calls == 1


def test_replay_mismatched_model_and_unsettled_output_fail_closed():
    for admission in (
        None,
        {"status": "already_consumed"},
        {**Client().admission, "modelDigest": "sha256:" + "b" * 64},
    ):
        client, engine = Client(), Engine()
        client.admission = admission
        assert execute_claimed(json.dumps(envelope()).encode(), engine, SETTINGS, client) is None
        assert engine.calls == 0
    client, engine = Client(), Engine()
    client.settled = False
    assert execute_claimed(json.dumps(envelope()).encode(), engine, SETTINGS, client) is None
    assert client.calls == ["consume", "settle"]


def test_worker_rejects_unavailable_action_and_extra_value():
    for action in [
        PlannedAction(type="activate", target="other"),
        PlannedAction(type="activate", target="tasks.tab", value="secret"),
    ]:
        client, engine = Client(), Engine()
        engine.plan = ActionPlan(schemaVersion="v1", actions=[action])
        assert execute_claimed(json.dumps(envelope()).encode(), engine, SETTINGS, client) is None
        assert client.calls == ["consume"]
