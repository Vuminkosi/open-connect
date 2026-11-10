"""Reusable security primitives for workers: identity, signing, redacted logging."""

from openconnect_worker_common.security.identity import Identity, extract_bearer, verify_bearer
from openconnect_worker_common.security.redacted_logs import (
    RedactingFilter,
    get_redacted_logger,
    redact_text,
)
from openconnect_worker_common.security.signing import (
    generate_key,
    key_from_environment,
    sign_bytes,
    verify_bytes,
)

__all__ = [
    "Identity",
    "RedactingFilter",
    "extract_bearer",
    "generate_key",
    "get_redacted_logger",
    "key_from_environment",
    "redact_text",
    "sign_bytes",
    "verify_bearer",
    "verify_bytes",
]
