"""Bounded fake inference engine.

Deterministic and side-effect free: no model downloads, no network calls,
and no second (MAF) agent orchestrator. It produces a single bounded
completion from the supplied messages so hosts can exercise the full
claim/execute/complete path without model weights.
"""

from __future__ import annotations

from openconnect_inference.schemas import ChatMessage, StreamChunk

BASE_CAPABILITIES: tuple[str, ...] = ("chat", "stream")
# Reserved configuration values; no current adapter implements these operations.
OPTIONAL_CAPABILITIES: tuple[str, ...] = ("tools", "structured-output", "embeddings")


def advertised_capabilities(
    *,
    model_id: str | None,
    enable_tools: bool,
    enable_structured_output: bool,
    enable_embeddings: bool,
) -> list[str]:
    """Return exactly the capabilities this engine instance can serve."""
    # Keep the configuration signature compatible while rejecting unsupported
    # operations until an adapter actually implements their request and result contracts.
    return list(BASE_CAPABILITIES)


def _render(messages: list[ChatMessage]) -> str:
    last_user = next(
        (m.content for m in reversed(messages) if m.role == "user"),
        messages[-1].content,
    )
    return f"fake-inference response to: {last_user.strip()}"


class FakeInferenceEngine:
    """Deterministic bounded fake: single completion, no orchestration."""

    name = "fake-bounded"
    performs_downloads = False
    performs_network_calls = False
    is_second_orchestrator = False

    def complete(self, messages: list[ChatMessage], max_tokens: int) -> tuple[str, int]:
        """Return (output, input_chars); output bounded by ~4 chars/token."""
        input_chars = sum(len(m.content) for m in messages)
        output = _render(messages)[: max(1, max_tokens) * 4]
        return output, input_chars

    def stream(self, messages: list[ChatMessage], max_tokens: int) -> list[StreamChunk]:
        """Return ordered chunks with exactly one terminal chunk, last.

        Chunk ``seq`` values are contiguous from 0 and ``done=True`` appears
        exactly once, on the final chunk, so consumers can rely on
        terminal-once semantics.
        """
        output, _ = self.complete(messages, max_tokens)
        # Split into at most three content deltas, then one terminal chunk.
        size = max(1, (len(output) + 2) // 3)
        deltas = [output[i : i + size] for i in range(0, len(output), size)][:3]
        chunks = [StreamChunk(seq=i, delta=d, done=False) for i, d in enumerate(deltas)]
        chunks.append(StreamChunk(seq=len(chunks), delta="", done=True))
        return chunks
