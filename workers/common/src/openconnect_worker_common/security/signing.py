"""HMAC-SHA256 signing helpers for worker payloads. Keys never leave the host."""

import hashlib
import hmac
import os
import secrets


def generate_key(num_bytes: int = 32) -> bytes:
    """Generate a random signing key. Callers must persist it in the secret store."""
    if num_bytes < 16:
        raise ValueError("signing key must be at least 16 bytes")
    return secrets.token_bytes(num_bytes)


def key_from_environment(variable: str = "OPENCONNECT_WORKER_SIGNING_KEY") -> bytes | None:
    """Load a hex-encoded signing key from the environment, or None if unset."""
    raw = os.environ.get(variable)
    if not raw:
        return None
    try:
        return bytes.fromhex(raw)
    except ValueError:
        return None


def sign_bytes(payload: bytes, key: bytes) -> str:
    """Return the hex HMAC-SHA256 signature of payload under key."""
    if not key:
        raise ValueError("signing key must not be empty")
    return hmac.new(key, payload, hashlib.sha256).hexdigest()


def verify_bytes(payload: bytes, signature: str, key: bytes) -> bool:
    """Constant-time signature verification. Returns False on any mismatch."""
    if not key or not signature:
        return False
    try:
        expected = sign_bytes(payload, key)
    except ValueError:
        return False
    return hmac.compare_digest(expected, signature)
