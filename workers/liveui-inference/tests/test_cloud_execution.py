import base64
import json
from types import SimpleNamespace
from uuid import uuid4

from app.cloud_execution import execute_claimed
from app.cloud_usage import TokenUsage
from app.schemas import ActionPlan, PlannedAction

ID = str(uuid4())
SETTINGS = SimpleNamespace(model_id="registry/model-a", model_revision="revision-1", model_sha256="a" * 64,
                           model_context_size=2048)


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

    def consume_metered(self, *args):
        self.calls.append(("consume-metered", args[-1]))
        return self.admission

    def settle_metered(self, *args):
        self.calls.append(("settle-metered", args[-2], args[-1]))
        return self.settled

    def fail_metered(self, *args):
        self.calls.append(("fail-metered", args[-2], args[-1]))
        return self.settled


class Engine:
    def __init__(self):
        self.calls = 0
        self.plan = ActionPlan(schemaVersion="v1", actions=[PlannedAction(type="activate", target="tasks.tab")])

    def generate(self, _):
        self.calls += 1
        return self.plan, "deterministic"

    def generate_metered(self, _, report):
        self.calls += 1
        report(TokenUsage(410, 31))
        return self.plan, "qwen3-0.6b"


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


def test_metered_execution_consumes_context_and_settles_exact_usage():
    client, engine = Client(), Engine()
    result = execute_claimed(json.dumps(envelope()).encode(), engine, SETTINGS, client, True)
    assert result["provider"] == "qwen3-0.6b"
    assert client.calls == [("consume-metered", 2048), ("settle-metered", 410, 31)]


def test_metered_failure_never_falls_back_or_returns_unsettled_output():
    for scenario in ("admission", "usage", "plan", "settlement"):
        client, engine = Client(), Engine()
        if scenario == "admission":
            client.admission = None
        if scenario == "usage":
            engine.generate_metered = lambda _, report: (engine.plan, "qwen3-0.6b")
        if scenario == "plan":
            engine.plan = ActionPlan(schemaVersion="v1", actions=[PlannedAction(type="activate", target="unlisted")])
        if scenario == "settlement":
            client.settled = False
        assert execute_claimed(json.dumps(envelope()).encode(), engine, SETTINGS, client, True) is None
        assert not any(call in ("consume", "settle") for call in client.calls)
        if scenario in ("admission", "usage"):
            assert len(client.calls) == 1
        if scenario == "plan":
            assert client.calls == [("consume-metered", 2048), ("fail-metered", 410, 31)]
        if scenario == "settlement":
            assert client.calls == [("consume-metered", 2048), ("settle-metered", 410, 31)]


def test_metered_failure_reports_known_usage_but_never_invents_unknown_usage():
    for report_first in (True, False):
        client, engine = Client(), Engine()
        def generate(_, report):
            if report_first:
                report(TokenUsage(410, 31))
            raise ValueError("model output rejected")
        engine.generate_metered = generate
        assert execute_claimed(json.dumps(envelope()).encode(), engine, SETTINGS, client, True) is None
        assert client.calls == [("consume-metered", 2048)] + ([("fail-metered", 410, 31)] if report_first else [])


def test_metered_execution_rejects_inconsistent_or_multiple_usage_reports():
    for values, provider in [([TokenUsage(0, 0)], "qwen3-0.6b"), ([TokenUsage(1, 0)], "deterministic"),
                             ([TokenUsage(410, 31), TokenUsage(410, 31)], "qwen3-0.6b")]:
        client, engine = Client(), Engine()
        def generate(_, report):
            for value in values:
                report(value)
            return engine.plan, provider
        engine.generate_metered = generate
        assert execute_claimed(json.dumps(envelope()).encode(), engine, SETTINGS, client, True) is None
        assert client.calls == [("consume-metered", 2048)]
