"""Identity (bearer auth) tests: fail closed, constant-time compare."""

from openconnect_worker_common.security.identity import (
    Identity,
    extract_bearer,
    verify_bearer,
)


def test_extract_bearer() -> None:
    assert extract_bearer("Bearer abc123") == "abc123"
    assert extract_bearer("Basic abc123") is None
    assert extract_bearer(None) is None
    assert extract_bearer("Bearer ") is None


def test_verify_fails_closed_without_configured_token() -> None:
    assert verify_bearer("Bearer anything", None) is False
    assert verify_bearer("Bearer anything", "") is False


def test_verify_accepts_exact_token_only() -> None:
    assert verify_bearer("Bearer s3cret", "s3cret") is True
    assert verify_bearer("Bearer wrong", "s3cret") is False
    assert verify_bearer(None, "s3cret") is False


def test_identity_from_environment(monkeypatch) -> None:
    monkeypatch.delenv("OPENCONNECT_WORKER_TOKEN", raising=False)
    assert Identity.from_environment().is_configured is False
    monkeypatch.setenv("OPENCONNECT_WORKER_TOKEN", "tok")
    identity = Identity.from_environment()
    assert identity.is_configured is True
    assert identity.verify("Bearer tok") is True
    assert identity.verify("Bearer nope") is False
