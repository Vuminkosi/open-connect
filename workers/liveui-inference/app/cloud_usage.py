"""Strict model usage parsing for Cloud quota reconciliation."""

from dataclasses import dataclass
from typing import Any


@dataclass(frozen=True)
class TokenUsage:
    input_tokens: int
    output_tokens: int


def model_usage(result: Any, context_limit: int, output_limit: int) -> TokenUsage:
    """Accept only bounded native usage; missing or ambiguous usage is not zero usage."""
    if not isinstance(result, dict) or not isinstance(result.get("usage"), dict):
        raise ValueError("Model token usage is unavailable")
    usage = result["usage"]
    prompt, completion, total = (usage.get(name) for name in ("prompt_tokens", "completion_tokens", "total_tokens"))
    if (
        type(prompt) is not int
        or type(completion) is not int
        or type(total) is not int
        or type(context_limit) is not int
        or type(output_limit) is not int
        or not 1 <= prompt <= context_limit
        or not 0 <= completion <= output_limit
        or total != prompt + completion
        or total > context_limit
    ):
        raise ValueError("Model token usage is invalid")
    return TokenUsage(prompt, completion)
