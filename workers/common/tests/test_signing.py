"""Signing tests: roundtrip, tamper rejection, key hygiene."""

import pytest

from openconnect_worker_common.security.signing import (
    generate_key,
    key_from_environment,
    sign_bytes,
    verify_bytes,
)


def test_sign_verify_roundtrip() -> None:
    key = generate_key()
    signature = sign_bytes(b"task-payload", key)
    assert verify_bytes(b"task-payload", signature, key) is True


def test_tampered_payload_fails() -> None:
    key = generate_key()
    signature = sign_bytes(b"original", key)
    assert verify_bytes(b"modified", signature, key) is False
    assert verify_bytes(b"original", "0" * 64, key) is False


def test_empty_key_rejected() -> None:
    with pytest.raises(ValueError):
        sign_bytes(b"x", b"")
    assert verify_bytes(b"x", "0" * 64, b"") is False


def test_short_key_rejected() -> None:
    with pytest.raises(ValueError):
        generate_key(8)


def test_key_from_environment(monkeypatch) -> None:
    monkeypatch.delenv("OPENCONNECT_WORKER_SIGNING_KEY", raising=False)
    assert key_from_environment() is None
    monkeypatch.setenv("OPENCONNECT_WORKER_SIGNING_KEY", "not-hex!!")
    assert key_from_environment() is None
    monkeypatch.setenv("OPENCONNECT_WORKER_SIGNING_KEY", generate_key().hex())
    assert isinstance(key_from_environment(), bytes)
