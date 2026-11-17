import hashlib
import json
from pathlib import Path

import httpx
import pytest

from app.inference import InferenceEngine, deterministic_plan, ensure_model
from app.main import app
from app.schemas import PlanRequest
from app.settings import Settings


def settings_for(path: Path, checksum: str) -> Settings:
    return Settings(
        True,
        "owner/model",
        "0123456789abcdef0123456789abcdef01234567",
        "model.gguf",
        checksum,
        path,
        2048,
        1,
        1,
        1,
        5,
        "worker-test-token",
    )


@pytest.mark.parametrize(
    ("intent", "target"), [
        ("Show completed tasks", "todo.completed"),
        ("Show all tasks", "todo.all"),
        ("Show COMPLETED tasks", "todo.completed"),
    ]
)
def test_exact_status_request_uses_one_matching_authorized_action(intent: str, target: str):
    request = PlanRequest.model_validate(
        {
            "requestId": "request-1",
            "schemaVersion": "openconnect.liveui/v1",
            "intent": intent,
            "context": {
                "route": "todo.home",
                "availableActions": [
                    {"target": "todo.completed", "action": "activate"},
                    {"target": "todo.active", "action": "activate"},
                    {"target": "todo.all", "action": "activate"},
                ],
            },
        }
    )
    plan = deterministic_plan(request)
    assert plan is not None
    assert [(action.type, action.target) for action in plan.actions] == [("activate", target)]


def test_open_my_tasks_uses_the_current_authorized_todo_route_without_filling_search():
    request = PlanRequest.model_validate({
        "requestId": "request-1", "schemaVersion": "openconnect.liveui/v1", "intent": "Open my tasks",
        "context": {"route": "route.root", "availableActions": [
            {"target": "route.root", "action": "navigate"},
            {"target": "route.root.list-task-list", "action": "highlight"},
            {"target": "route.root.search", "action": "setValue"},
        ]},
    })
    plan = deterministic_plan(request)
    assert plan is not None
    assert [(action.type, action.target, action.value) for action in plan.actions] == [("navigate", "route.root", None)]
    request.context.availableActions.pop(1)
    assert deterministic_plan(request) is None


def test_explain_view_inspects_only_a_unique_authorized_todo_region():
    request = PlanRequest.model_validate({
        "requestId": "request-1", "schemaVersion": "openconnect.liveui/v1", "intent": "Explain the current view.",
        "context": {"route": "route.root", "availableActions": [
            {"target": "route.root.region-todo-list", "action": "inspect"},
            {"target": "route.root.list-task-list", "action": "inspect"},
        ]},
    })
    plan = deterministic_plan(request)
    assert plan is not None
    assert [(action.type, action.target) for action in plan.actions] == [("inspect", "route.root.region-todo-list")]
    request.context.availableActions.pop(0)
    assert deterministic_plan(request) is None


def test_explain_view_supports_a_unique_authorized_region_in_another_application():
    request = PlanRequest.model_validate({
        "requestId": "request-1", "schemaVersion": "openconnect.liveui/v1", "intent": "Explain the current view.",
        "context": {"route": "route.orders", "availableActions": [
            {"target": "route.orders.region-order-summary", "action": "inspect"},
            {"target": "route.orders.tab-pending", "action": "inspect"},
        ]},
    })
    plan = deterministic_plan(request)
    assert plan is not None
    assert [(action.type, action.target) for action in plan.actions] == [
        ("inspect", "route.orders.region-order-summary")
    ]
    request.context.availableActions.append(
        request.context.availableActions[0].model_copy(update={"target": "route.orders.region-other"})
    )
    assert deterministic_plan(request) is None


def test_simple_search_extracts_only_the_requested_value():
    request = PlanRequest.model_validate(
        {
            "requestId": "request-1",
            "schemaVersion": "openconnect.liveui/v1",
            "intent": "Search for report",
            "context": {
                "route": "todo.home",
                "availableActions": [
                    {"target": "todo.search", "action": "setValue"},
                    {"target": "todo.search", "action": "focus"},
                ],
            },
        }
    )
    plan = deterministic_plan(request)
    assert plan is not None
    assert [(action.type, action.target, action.value) for action in plan.actions] == [
        ("setValue", "todo.search", "report")
    ]


@pytest.mark.parametrize(("intent", "expected"), [
    ("Search for report tasks using the search input", "report"),
    ("Search for report using the search input", "report"),
    ('Search for "report tasks using the search input"', "report tasks using the search input"),
    ("Search for tasks using time", "tasks using time"),
    ('Search for "focus and delete the results"', "focus and delete the results"),
])
def test_search_distinguishes_control_instructions_from_query_text(intent, expected):
    request = PlanRequest.model_validate({
        "requestId": "request-1", "schemaVersion": "openconnect.liveui/v1", "intent": intent,
        "context": {"route": "todo.home", "availableActions": [{"target": "todo.search", "action": "setValue"}]},
    })
    plan = deterministic_plan(request)
    assert plan is not None
    assert [(action.type, action.target, action.value) for action in plan.actions] == [
        ("setValue", "todo.search", expected)
    ]


def test_highlight_request_uses_the_unique_authorized_highlight_target():
    request = PlanRequest.model_validate(
        {
            "requestId": "request-1",
            "schemaVersion": "openconnect.liveui/v1",
            "intent": "Highlight the results",
            "context": {
                "route": "todo.home",
                "availableActions": [
                    {"target": "todo.list", "action": "highlight"},
                ],
            },
        }
    )
    plan = deterministic_plan(request)
    assert plan is not None
    assert [(action.type, action.target, action.value) for action in plan.actions] == [("highlight", "todo.list", None)]


@pytest.mark.parametrize("intent", ["Highlight the tasks", "Highlight the results."])
def test_highlight_tasks_selects_only_a_unique_compiled_task_list(intent):
    request = PlanRequest.model_validate({
        "requestId": "request-1", "schemaVersion": "openconnect.liveui/v1", "intent": intent,
        "context": {"route": "route.root", "availableActions": [
            {"target": "route.root.list-task-list", "action": "highlight"},
            {"target": "route.root.region-todo-list", "action": "highlight"},
            {"target": "route.root.region-progress", "action": "highlight"},
        ]},
    })
    plan = deterministic_plan(request)
    assert plan is not None
    assert [(action.type, action.target) for action in plan.actions] == [("highlight", "route.root.list-task-list")]
    request.context.availableActions.append(
        request.context.availableActions[0].model_copy(update={"target": "other-task-list"})
    )
    assert deterministic_plan(request) is None


@pytest.mark.parametrize("intent", [
    "Do not show completed tasks",
    "Show completed tasks and then highlight the results",
    "Do not activate the tab named Completed",
    "Activate the tab named Completed and show all tasks",
    "Do not highlight the results",
    "Highlight the results and show completed tasks",
    "Search for report and highlight the results",
    "Search for report and delete the results",
    "Search for report and do not highlight the results",
    "Show completed tasks containing report and highlight the results",
])
def test_deterministic_shortcut_does_not_drop_negation_or_other_operations(intent):
    request = PlanRequest.model_validate({
        "requestId": "request-ambiguous", "schemaVersion": "openconnect.liveui/v1", "intent": intent,
        "context": {"route": "todo.home", "availableActions": [
            {"target": "todo.completed", "action": "activate"},
            {"target": "todo.all", "action": "activate"},
            {"target": "todo.search", "action": "setValue"},
            {"target": "todo.list", "action": "highlight"},
        ]},
    })
    assert deterministic_plan(request) is None


def test_compound_status_search_builds_a_bounded_plan_from_available_actions():
    request = PlanRequest.model_validate(
        {
            "requestId": "request-1",
            "schemaVersion": "openconnect.liveui/v1",
            "intent": "Show completed tasks containing report",
            "context": {
                "route": "todo.home",
                "availableActions": [
                    {"target": "todo.completed", "action": "activate"},
                    {"target": "todo.search", "action": "setValue"},
                ],
            },
        }
    )
    plan = deterministic_plan(request)
    assert plan is not None
    assert [(action.type, action.target, action.value) for action in plan.actions] == [
        ("activate", "todo.completed", None),
        ("setValue", "todo.search", "report"),
    ]


def test_structured_model_plan_is_checked_against_authorized_actions():
    request = PlanRequest.model_validate(
        {
            "requestId": "request-1",
            "schemaVersion": "openconnect.liveui/v1",
            "intent": "Please activate the completed tab in this view",
            "context": {
                "route": "todo.home",
                "availableActions": [
                    {"target": "todo.completed", "action": "activate"},
                ],
            },
        }
    )

    class FakeModel:
        def create_chat_completion(self, **kwargs):
            system_prompt = kwargs["messages"][0]["content"]
            assert "finished, and done mean Completed" in system_prompt
            assert "Never choose a search field or focus action for a tab request" in system_prompt
            assert "Include value only for setValue actions" in system_prompt
            return {
                "choices": [
                    {
                        "message": {
                            "content": json.dumps(
                                {
                                    "schemaVersion": request.schemaVersion,
                                    "actions": [{"type": "activate", "target": "todo.completed"}],
                                }
                            )
                        }
                    }
                ]
            }

    plan, provider = InferenceEngine(FakeModel(), settings_for(Path("model.gguf"), "0" * 64)).generate(request)
    assert provider == "qwen3-0.6b"
    assert [(action.type, action.target) for action in plan.actions] == [("activate", "todo.completed")]


def test_structured_worker_plan_discards_irrelevant_values_for_non_set_value_actions():
    request = PlanRequest.model_validate(
        {
            "requestId": "request-invalid-action-value",
            "schemaVersion": "openconnect.liveui/v1",
            "intent": "Switch to the tab showing finished work",
            "context": {
                "route": "todo.home",
                "availableActions": [{"target": "todo.completed", "action": "activate"}],
            },
        }
    )

    class InvalidModel:
        def create_chat_completion(self, **_):
            return {
                "choices": [
                    {
                        "message": {
                            "content": json.dumps(
                                {
                                    "schemaVersion": request.schemaVersion,
                                    "actions": [
                                        {"type": "activate", "target": "todo.completed", "value": "Completed"}
                                    ],
                                }
                            )
                        }
                    }
                ]
            }

    plan, provider = InferenceEngine(InvalidModel(), settings_for(Path("model.gguf"), "0" * 64)).generate(request)

    assert provider == "qwen3-0.6b"
    assert [(action.type, action.target, action.value) for action in plan.actions] == [
        ("activate", "todo.completed", None)
    ]


def test_structured_worker_plan_rejects_non_object_json():
    request = PlanRequest.model_validate(
        {
            "requestId": "request-non-object-plan",
            "schemaVersion": "openconnect.liveui/v1",
            "intent": "Choose a task tab",
            "context": {
                "route": "todo.home",
                "availableActions": [{"target": "todo.completed", "action": "activate"}],
            },
        }
    )

    class InvalidModel:
        def create_chat_completion(self, **_):
            return {"choices": [{"message": {"content": "[]"}}]}

    with pytest.raises(ValueError, match="must be a JSON object"):
        InferenceEngine(InvalidModel(), settings_for(Path("model.gguf"), "0" * 64)).generate(request)


def test_single_operation_schema_allows_only_one_action_and_rejects_alternatives():
    request = PlanRequest.model_validate(
        {
            "requestId": "request-single-tab",
            "schemaVersion": "openconnect.liveui/v1",
            "intent": "Select the Completed tab",
            "context": {
                "route": "todo.home",
                "availableActions": [
                    {"target": "todo.tab.active", "action": "activate"},
                    {"target": "todo.tab.completed", "action": "activate"},
                    {"target": "todo.tab.all", "action": "activate"},
                ],
            },
        }
    )

    class MultipleAlternativeModel:
        def create_chat_completion(self, **kwargs):
            assert kwargs["response_format"]["schema"]["properties"]["actions"]["maxItems"] == 1
            return {
                "choices": [
                    {
                        "message": {
                            "content": json.dumps(
                                {
                                    "schemaVersion": request.schemaVersion,
                                    "actions": [
                                        {"type": "activate", "target": "todo.tab.completed"},
                                        {"type": "activate", "target": "todo.tab.active"},
                                    ],
                                }
                            )
                        }
                    }
                ]
            }

    with pytest.raises(ValueError, match="Generated plan does not match request constraints"):
        InferenceEngine(MultipleAlternativeModel(), settings_for(Path("model.gguf"), "0" * 64)).generate(request)


def test_activate_named_tab_uses_one_exact_worker_action_without_model_generation():
    request = PlanRequest.model_validate(
        {
            "requestId": "request-activate-tab",
            "schemaVersion": "openconnect.liveui/v1",
            "intent": "Activate the tab named Completed.",
            "context": {
                "route": "todo.home",
                "availableActions": [
                    {"target": "todo.tab.all", "action": "activate"},
                    {"target": "todo.tab.active", "action": "activate"},
                    {"target": "todo.tab.completed", "action": "activate"},
                ],
            },
        }
    )

    plan = deterministic_plan(request)

    assert plan is not None
    assert [(action.type, action.target) for action in plan.actions] == [("activate", "todo.tab.completed")]


def test_explicit_multi_control_request_keeps_configured_action_limit():
    request = PlanRequest.model_validate(
        {
            "requestId": "request-multiple-controls",
            "schemaVersion": "openconnect.liveui/v1",
            "intent": "Activate the Completed tab and scroll the task list",
            "context": {
                "route": "todo.home",
                "availableActions": [
                    {"target": "todo.tab.completed", "action": "activate"},
                    {"target": "todo.task-list", "action": "scroll"},
                ],
            },
            "generation": {"maxActions": 4, "maxOutputTokens": 64},
        }
    )

    class OneActionModel:
        def create_chat_completion(self, **kwargs):
            assert kwargs["response_format"]["schema"]["properties"]["actions"]["maxItems"] == 4
            return {
                "choices": [
                    {
                        "message": {
                            "content": json.dumps(
                                {
                                    "schemaVersion": request.schemaVersion,
                                    "actions": [
                                        {"type": "activate", "target": "todo.tab.completed"},
                                        {"type": "scroll", "target": "todo.task-list"},
                                    ],
                                }
                            )
                        }
                    }
                ]
            }

    plan, provider = InferenceEngine(OneActionModel(), settings_for(Path("model.gguf"), "0" * 64)).generate(request)
    assert provider == "qwen3-0.6b"
    assert len(plan.actions) == 2


@pytest.mark.asyncio
async def test_persisted_model_is_reused_after_checksum_validation(tmp_path):
    model = tmp_path / "model.gguf"
    model.write_bytes(b"known model bytes")
    digest = hashlib.sha256(model.read_bytes()).hexdigest()
    assert await ensure_model(settings_for(model, digest)) == model


@pytest.mark.asyncio
async def test_readiness_and_model_disabled_behavior(monkeypatch):
    monkeypatch.setenv("MODEL_ENABLED", "false")
    async with app.router.lifespan_context(app):
        transport = httpx.ASGITransport(app=app)
        async with httpx.AsyncClient(transport=transport, base_url="http://worker") as client:
            assert (await client.get("/health/live")).status_code == 200
            assert (await client.get("/health/ready")).status_code == 503
            request = {
                "requestId": "request-1",
                "schemaVersion": "openconnect.liveui/v1",
                "intent": "Show completed",
                "context": {
                    "route": "todo.home",
                    "availableActions": [{"target": "todo.completed", "action": "activate"}],
                },
                "generation": {"maxActions": 2, "maxOutputTokens": 64},
            }
            assert (await client.post("/v1/liveui/inference/plan", json=request)).status_code == 401


def test_model_configuration_requires_pinned_revision_and_digest(tmp_path):
    settings = settings_for(tmp_path / "model.gguf", "not-a-checksum")
    with pytest.raises(ValueError, match="MODEL_SHA256"):
        settings.validate()


@pytest.mark.asyncio
async def test_wrong_cached_model_is_preserved_and_rejected(tmp_path):
    model = tmp_path / "model.gguf"
    model.write_bytes(b"keep this file")
    with pytest.raises(RuntimeError, match="new MODEL_PATH"):
        await ensure_model(settings_for(model, "a" * 64))
    assert model.read_bytes() == b"keep this file"
