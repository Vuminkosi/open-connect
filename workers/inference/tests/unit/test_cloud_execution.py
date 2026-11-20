import base64
import json
from uuid import uuid4

from openconnect_inference.cloud_execution import execute_claimed


def payload(**overrides):
    value = {"schemaVersion": "openconnect.cloud.inference.worker/v1", "requestId": str(uuid4()),
             "modelId": "registry/model-a", "input": {"messages": [{"role": "user", "content": "fixture"}],
             "maxTokens": 4, "stream": False}}
    value.update(overrides)
    return value


def envelope(value):
    raw = json.dumps(value, separators=(",", ":")).encode()
    return json.dumps({"schemaVersion": "openconnect.cloud.worker.inference/v1", "reservationId": str(uuid4()),
                       "claimId": str(uuid4()), "payload": base64.b64encode(raw).decode(),
                       "key": base64.b64encode(bytes(32)).decode()}).encode()


class Prepared:
    input_tokens = 3

    def __init__(self, calls):
        self.calls = calls

    def generate(self, maximum):
        self.calls.append(("generate", maximum))
        return "output", 2


class Runtime:
    model_id = "registry/model-a"
    model_revision = "revision-1"
    model_digest = "sha256:" + "a" * 64

    def __init__(self, calls):
        self.calls = calls

    def prepare(self, value):
        self.calls.append(("prepare", value.modelId))
        return Prepared(self.calls)


class Client:
    def __init__(self, calls, reserved=7, model="registry/model-a", settle=True):
        self.calls = calls
        self.reserved = reserved
        self.model = model
        self.settled = settle

    def consume(self, reservation, claim, raw, key):
        self.calls.append(("consume", json.loads(raw)["modelId"], len(key)))
        return {"status": "consumed", "reservedTokens": self.reserved, "modelId": self.model, "modelRevision": "revision-1", "modelDigest": "sha256:" + "a" * 64}

    def settle(self, reservation, claim, input_tokens, output_tokens):
        self.calls.append(("settle", input_tokens, output_tokens))
        return self.settled


def test_only_settled_bounded_execution_returns_output():
    calls = []
    result = execute_claimed(envelope(payload()), Runtime(calls), Client(calls))
    assert result is not None
    assert result["output"] == "output"
    assert result["usage"] == {"inputTokens": 3, "outputTokens": 2}
    assert calls == [("prepare", "registry/model-a"), ("consume", "registry/model-a", 32),
                     ("generate", 4), ("settle", 3, 2)]


def test_unavailable_budget_model_or_settlement_never_returns_output():
    for reserved, model, settled, expected in [(6, "registry/model-a", True, False),
                                                (7, "other", True, False),
                                                (7, "registry/model-a", False, True)]:
        calls = []
        assert execute_claimed(envelope(payload()), Runtime(calls), Client(calls, reserved, model, settled)) is None
        assert any(item[0] == "generate" for item in calls) is expected


def test_untrusted_or_streaming_payload_never_consumes():
    for value in [payload(modelId="other"), payload(input={"messages": [{"role": "user", "content": "x"}],
                 "maxTokens": 4, "stream": True}), payload(extra="unexpected")]:
        calls = []
        assert execute_claimed(envelope(value), Runtime(calls), Client(calls)) is None
        assert not any(item[0] == "consume" for item in calls)
    assert execute_claimed(b"{}", None, None) is None


def test_overreported_or_malformed_usage_is_not_settled():
    calls = []
    class BadPrepared(Prepared):
        def generate(self, maximum):
            self.calls.append(("generate", maximum))
            return "output", maximum + 1
    class BadRuntime(Runtime):
        def prepare(self, value):
            self.calls.append(("prepare", value.modelId))
            return BadPrepared(self.calls)
    assert execute_claimed(envelope(payload()), BadRuntime(calls), Client(calls)) is None
    assert not any(item[0] == "settle" for item in calls)


def test_worker_refuses_changed_model_revision_or_digest():
    for field, value in [("modelRevision", "other"), ("modelDigest", "sha256:" + "b" * 64)]:
        calls = []
        class MismatchedClient(Client):
            def consume(self, reservation, claim, raw, key):
                result = super().consume(reservation, claim, raw, key)
                result[field] = value
                return result
        assert execute_claimed(envelope(payload()), Runtime(calls), MismatchedClient(calls)) is None
        assert not any(item[0] == "generate" for item in calls)
