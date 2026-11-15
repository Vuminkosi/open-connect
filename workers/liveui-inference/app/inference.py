import asyncio
import copy
import hashlib
import json
import os
import re
import tempfile
from pathlib import Path
from typing import Any

import httpx

from .schemas import ActionPlan, PlannedAction, PlanRequest
from .settings import Settings

PLAN_SCHEMA = {
    "type": "object",
    "properties": {
        "schemaVersion": {"type": "string"},
        "actions": {
            "type": "array",
            "items": {
                "type": "object",
                "properties": {
                    "type": {
                        "type": "string",
                        "enum": ["activate", "navigate", "setValue", "select", "highlight", "focus", "scroll"],
                    },
                    "target": {"type": "string"},
                    "value": {"type": "string"},
                },
                "required": ["type", "target"],
                "additionalProperties": False,
            },
        },
    },
    "required": ["schemaVersion", "actions"],
    "additionalProperties": False,
}

UI_OPERATION = re.compile(
    r"\b(?:show|display|open|filter|activate|select|switch|search|find|set|enter|highlight|focus|scroll|navigate)\b",
    re.IGNORECASE,
)


def max_actions_for_intent(request: PlanRequest) -> int:
    """Keep one-operation requests to one action; allow bounded explicit UI sequences."""
    operation_count = len(UI_OPERATION.findall(request.intent))
    return request.generation.maxActions if operation_count > 1 else 1


def plan_schema(max_actions: int) -> dict[str, Any]:
    schema = copy.deepcopy(PLAN_SCHEMA)
    schema["properties"]["actions"]["minItems"] = 1
    schema["properties"]["actions"]["maxItems"] = max_actions
    return schema


async def ensure_model(settings: Settings) -> Path:
    path = settings.model_path
    path.parent.mkdir(parents=True, exist_ok=True)
    if path.exists():
        if await asyncio.to_thread(sha256_file, path) == settings.model_sha256:
            return path
        raise RuntimeError(
            "Existing model file checksum does not match configured MODEL_SHA256; "
            "preserve it and configure a new MODEL_PATH"
        )

    url = (
        f"https://huggingface.co/{settings.model_id}/resolve/"
        f"{settings.model_revision}/{settings.model_filename}?download=true"
    )
    descriptor, temporary_name = tempfile.mkstemp(prefix="liveui-model-", dir=path.parent)
    os.close(descriptor)
    temporary = Path(temporary_name)
    digest = hashlib.sha256()
    try:
        async with httpx.AsyncClient(timeout=None, follow_redirects=True) as client:
            async with client.stream("GET", url) as response:
                response.raise_for_status()
                with temporary.open("wb") as stream:
                    async for chunk in response.aiter_bytes(1024 * 1024):
                        digest.update(chunk)
                        stream.write(chunk)
        if digest.hexdigest() != settings.model_sha256:
            raise RuntimeError("Downloaded model checksum does not match configured MODEL_SHA256")
        os.replace(temporary, path)
        return path
    finally:
        temporary.unlink(missing_ok=True)


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


class InferenceEngine:
    def __init__(self, model: Any, settings: Settings):
        self._model = model
        self._settings = settings

    def generate(self, request: PlanRequest) -> tuple[ActionPlan, str]:
        deterministic = deterministic_plan(request)
        if deterministic is not None:
            return deterministic, "deterministic"

        maximum_actions = max_actions_for_intent(request)
        capability_list = [{"target": item.target, "action": item.action} for item in request.context.availableActions]
        prompt = json.dumps(
            {
                "schemaVersion": request.schemaVersion,
                "intent": request.intent,
                "route": request.context.route,
                "availableActions": capability_list,
                "maxActions": maximum_actions,
                "constraints": (
                    "Return only actions explicitly available. Use no IDs outside availableActions. "
                    "Return exactly maxActions actions or fewer, never more. For a single UI operation, "
                    "return exactly one action. Never return alternative targets or activate multiple "
                    "tabs for one request. Match the target "
                    "to the requested state. For a search request, set the value to only the search "
                    "term, not the instruction, and do not add focus unless the user asks for focus. "
                    "Copy schemaVersion exactly. If no action fits, return an empty actions array."
                ),
            },
            separators=(",", ":"),
        )
        result: dict[str, Any] = self._model.create_chat_completion(
            messages=[
                {
                    "role": "system",
                    "content": (
                        "You map UI intent to a minimal action plan. Return one allowed action when the "
                    "intent requests one UI operation, even when several alternatives are available. "
                    "Never return a list of alternative tabs or controls. Copy the target and action "
                        "exactly. Do not reason about unrelated tasks. /no_think"
                    ),
                },
                {"role": "user", "content": prompt},
            ],
            response_format={"type": "json_object", "schema": plan_schema(maximum_actions)},
            max_tokens=request.generation.maxOutputTokens,
            temperature=0,
            top_p=1,
        )
        content = result["choices"][0]["message"]["content"]
        plan = ActionPlan.model_validate_json(content)
        if (
            plan.schemaVersion != request.schemaVersion
            or not plan.actions
            or len(plan.actions) > maximum_actions
        ):
            raise ValueError("Generated plan does not match request constraints")
        allowed = {(item.target, item.action) for item in request.context.availableActions}
        if any((action.target, action.type) not in allowed for action in plan.actions):
            raise ValueError("Generated plan contains an unavailable capability")
        return plan, "qwen3-0.6b"


def load_model(settings: Settings, path: Path) -> InferenceEngine:
    from llama_cpp import Llama

    return InferenceEngine(
        Llama(
            model_path=str(path),
            n_ctx=settings.model_context_size,
            n_threads=settings.model_threads,
            n_batch=256,
            chat_format="chatml",
            verbose=False,
        ),
        settings,
    )


def deterministic_plan(request: PlanRequest) -> ActionPlan | None:
    intent = request.intent.casefold()
    if re.search(r"\b(focus|scroll|navigate)\b", intent):
        return None
    tab_request = re.search(r"\bactivate\s+(?:the\s+)?tab\s+(?:named\s+)?(all|active|completed)\b", intent)
    if tab_request is not None:
        tab_action = find_status_action(request, tab_request.group(1))
        if tab_action is not None:
            return ActionPlan(schemaVersion=request.schemaVersion, actions=[tab_action])
    if re.search(r"\bhighlight\b", intent):
        candidates = [capability for capability in request.context.availableActions if capability.action == "highlight"]
        if len(candidates) == 1:
            return ActionPlan(
                schemaVersion=request.schemaVersion,
                actions=[PlannedAction(type="highlight", target=candidates[0].target)],
            )
    match = re.search(
        r"\b(show|display|open|filter)\s+(?:my\s+)?(completed|done|finished|active|incomplete|pending)\s+(?:todo\s+)?(?:tasks|items|todos)\b",
        intent,
    )
    compound_search = re.search(r"\b(?:containing|contains|matching|with)\s+(.+?)\s*$", request.intent, re.IGNORECASE)
    simple_search = re.search(
        r"\b(?:search|find)\s+(?:for\s+)?[\"']?(.+?)[\"']?\s*[?.!]*$", request.intent, re.IGNORECASE
    )
    if match is not None and compound_search is not None:
        status = match.group(2)
        search_value = compound_search.group(1).strip().strip(" \"'?.!")
        status_action = find_status_action(request, status)
        search_action = find_search_action(request, search_value)
        if status_action is None or search_action is None or request.generation.maxActions < 2:
            return None
        return ActionPlan(schemaVersion=request.schemaVersion, actions=[status_action, search_action])
    if match is not None and re.search(r"\b(search|find)\b", intent):
        return None
    if match is None and simple_search is not None:
        search_value = simple_search.group(1).strip().strip(" \"'?.!")
        search_action = find_search_action(request, search_value)
        if search_action is not None:
            return ActionPlan(schemaVersion=request.schemaVersion, actions=[search_action])
    if match is None:
        return None
    status_action = find_status_action(request, match.group(2))
    return (
        ActionPlan(schemaVersion=request.schemaVersion, actions=[status_action]) if status_action is not None else None
    )


def find_status_action(request: PlanRequest, status: str) -> PlannedAction | None:
    equivalent = {
        "all": {"all"},
        "completed": {"completed", "done", "finished"},
        "done": {"completed", "done", "finished"},
        "finished": {"completed", "done", "finished"},
        "active": {"active", "incomplete", "pending"},
        "incomplete": {"active", "incomplete", "pending"},
        "pending": {"active", "incomplete", "pending"},
    }[status]
    candidates = [
        capability
        for capability in request.context.availableActions
        if capability.action in {"activate", "select"}
        and any(re.search(rf"\b{re.escape(term)}\b", capability.target.casefold()) for term in equivalent)
    ]
    if len(candidates) != 1:
        return None
    selected = candidates[0]
    action_type = "select" if selected.action == "select" else "activate"
    return PlannedAction(type=action_type, target=selected.target)


def find_search_action(request: PlanRequest, search_value: str) -> PlannedAction | None:
    if not search_value or len(search_value) > 256:
        return None
    candidates = [
        capability
        for capability in request.context.availableActions
        if capability.action == "setValue" and "search" in capability.target.casefold()
    ]
    if len(candidates) != 1:
        return None
    return PlannedAction(type="setValue", target=candidates[0].target, value=search_value)
