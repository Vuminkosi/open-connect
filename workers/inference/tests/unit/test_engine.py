"""Unit tests for the inference fake engine (no network, no downloads)."""

from openconnect_inference.engine import FakeInferenceEngine, advertised_capabilities
from openconnect_inference.schemas import ChatMessage

engine = FakeInferenceEngine()


def _msgs(*contents: str) -> list[ChatMessage]:
    return [ChatMessage(role="user", content=c) for c in contents]


def test_engine_is_side_effect_free() -> None:
    assert engine.performs_downloads is False
    assert engine.performs_network_calls is False
    assert engine.is_second_orchestrator is False


def test_complete_is_bounded_and_deterministic() -> None:
    first, _ = engine.complete(_msgs("hello world"), 16)
    second, _ = engine.complete(_msgs("hello world"), 16)
    assert first == second
    assert "hello world" in first
    assert len(first) <= 16 * 4


def test_stream_chunks_ordered_terminal_once() -> None:
    chunks = engine.stream(_msgs("stream me"), 64)
    assert len(chunks) >= 2
    assert [c.seq for c in chunks] == list(range(len(chunks)))
    dones = [c for c in chunks if c.done]
    assert len(dones) == 1
    assert dones[0].seq == chunks[-1].seq
    assert "".join(c.delta for c in chunks) == engine.complete(_msgs("stream me"), 64)[0]


def test_capability_discovery_truthful() -> None:
    base = advertised_capabilities(
        model_id=None,
        enable_tools=True,
        enable_structured_output=True,
        enable_embeddings=True,
    )
    # Optional capabilities require a configured model identity.
    assert base == ["chat", "stream"]
    full = advertised_capabilities(
        model_id="org/model",
        enable_tools=True,
        enable_structured_output=False,
        enable_embeddings=True,
    )
    assert full == ["chat", "stream", "tools", "embeddings"]
