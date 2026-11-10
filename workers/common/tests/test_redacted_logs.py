"""Redacted logging tests: no PII or secrets may reach log output."""

import io
import logging

from openconnect_worker_common.security.redacted_logs import (
    get_redacted_logger,
    redact_text,
)


def test_redact_text_scrubs_pii_and_secrets() -> None:
    text = (
        "user alice@example.com called with Authorization: Bearer s3cret-token "
        "phone +1 555-123-4567 card 4111 1111 1111 1111 token=abc123"
    )
    redacted = redact_text(text)
    assert "alice@example.com" not in redacted
    assert "s3cret-token" not in redacted
    assert "4111 1111 1111 1111" not in redacted
    assert "abc123" not in redacted
    assert "[REDACTED_EMAIL]" in redacted
    assert "[REDACTED]" in redacted


def test_logger_output_contains_no_pii() -> None:
    stream = io.StringIO()
    handler = logging.StreamHandler(stream)
    logger = get_redacted_logger("test-openconnect-redacted")
    logger.handlers = [handler]
    logger.propagate = False
    logger.warning("scan for bob@example.org with Bearer hunter2 failed")
    output = stream.getvalue()
    assert "bob@example.org" not in output
    assert "hunter2" not in output
    assert "[REDACTED_EMAIL]" in output
