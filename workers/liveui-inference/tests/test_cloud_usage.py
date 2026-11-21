import json
from pathlib import Path

import pytest

from app.cloud_usage import TokenUsage, model_usage
from app.inference import InferenceEngine
from app.schemas import PlanRequest
from app.settings import Settings


def request(intent: str = "Select the Completed tab.") -> PlanRequest:
    return PlanRequest.model_validate({
        "requestId": "fixture", "schemaVersion": "v1", "intent": intent,
        "context": {"route": "todo.home", "availableActions": [{"target": "todo.completed", "action": "activate"}]},
        "generation": {"maxActions": 2, "maxOutputTokens": 128},
    })


def settings() -> Settings:
    return Settings(
        True, "owner/model", "0" * 40, "model.gguf", "0" * 64, Path("model.gguf"), 2048, 1, 1, 1, 5, "token"
    )


class Model:
    def __init__(self, usage: object, target: str = "todo.completed") -> None:
        self.usage = usage
        self.target = target
        self.calls = 0

    def create_chat_completion(self, **_: object) -> dict:
        self.calls += 1
        return {"usage": self.usage, "choices": [{"message": {"content": json.dumps({
            "schemaVersion": "v1", "actions": [{"type": "activate", "target": self.target}]
        })}}]}


def test_metered_model_reports_native_usage_before_returning_an_allowed_plan():
    observed: list[TokenUsage] = []
    engine = InferenceEngine(Model({"prompt_tokens": 120, "completion_tokens": 12, "total_tokens": 132}), settings())
    plan, provider = engine.generate_metered(request(), observed.append)
    assert provider == "qwen3-0.6b"
    assert plan.actions[0].target == "todo.completed"
    assert observed == [TokenUsage(120, 12)]


def test_deterministic_plan_reports_zero_without_calling_model():
    model = Model(None)
    observed: list[TokenUsage] = []
    _, provider = InferenceEngine(model, settings()).generate_metered(request("Show completed tasks"), observed.append)
    assert provider == "deterministic"
    assert model.calls == 0
    assert observed == [TokenUsage(0, 0)]


def test_known_usage_is_reported_even_when_model_proposes_an_unlisted_action():
    observed: list[TokenUsage] = []
    model = Model({"prompt_tokens": 120, "completion_tokens": 12, "total_tokens": 132}, "unlisted")
    engine = InferenceEngine(model, settings())
    with pytest.raises(ValueError):
        engine.generate_metered(request(), observed.append)
    assert observed == [TokenUsage(120, 12)]


@pytest.mark.parametrize("usage", [
    None, {}, {"prompt_tokens": True, "completion_tokens": 1, "total_tokens": 2},
    {"prompt_tokens": 0, "completion_tokens": 0, "total_tokens": 0},
    {"prompt_tokens": 10, "completion_tokens": 129, "total_tokens": 139},
    {"prompt_tokens": 2040, "completion_tokens": 10, "total_tokens": 2050},
    {"prompt_tokens": 10, "completion_tokens": 4, "total_tokens": 13},
])
def test_missing_invalid_or_over_budget_native_usage_is_not_reported(usage):
    observed: list[TokenUsage] = []
    with pytest.raises(ValueError):
        InferenceEngine(Model(usage), settings()).generate_metered(request(), observed.append)
    assert observed == []


def test_legacy_generation_does_not_require_native_usage_metadata():
    plan, provider = InferenceEngine(Model(None), settings()).generate(request())
    assert provider == "qwen3-0.6b"
    assert plan.actions[0].target == "todo.completed"


def test_model_usage_rejects_non_dictionary_response():
    with pytest.raises(ValueError):
        model_usage(None, 2048, 128)
