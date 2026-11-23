"""Redacted logging tests: no PII or secrets may reach log output."""

import io
import logging
from unittest.mock import patch

from openconnect_worker_common.security.redacted_logs import (
    RedactingFilter,
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


class _MemoryHandler(logging.StreamHandler):
    def handleError(self, record: logging.LogRecord) -> None:
        raise AssertionError("The test handler could not format a record.") from None


def _assert_patterns_absent(output: str, patterns: tuple[str, ...]) -> None:
    if any(pattern in output for pattern in patterns):
        raise AssertionError("An inert fixture marker was not sanitized.")


def test_named_logger_sanitizes_rendered_exception() -> None:
    stream = io.StringIO()
    logger = get_redacted_logger("test-openconnect-exception-text")
    logger.handlers = [_MemoryHandler(stream)]
    logger.propagate = False
    markers = (
        "fixture-person@invalid.example",
        "fixture-secret-marker",
        "fixture-bearer-marker",
        "+999 012-034-0567",
        "0000 0000 0000 0000",
    )

    try:
        email, secret, bearer, phone, card = markers
        raise RuntimeError(
            f"sample {email} token={secret} Bearer {bearer} phone {phone} card {card}"
        )
    except RuntimeError:
        logger.exception("The sample operation failed.")

    output = stream.getvalue()
    _assert_patterns_absent(output, markers)
    expected_fragments = (
        "The sample operation failed.",
        "Traceback",
        "RuntimeError",
        "[REDACTED]",
    )
    if not all(fragment in output for fragment in expected_fragments):
        raise AssertionError("Expected diagnostic context was not retained.")


def test_application_handler_sanitizes_child_and_cached_exception_text() -> None:
    stream = io.StringIO()
    handler = _MemoryHandler(stream)
    handler.addFilter(RedactingFilter())
    parent = logging.Logger("fixture-parent")
    parent.addHandler(handler)
    child = logging.Logger("fixture-parent.child")
    child.parent = parent
    message_marker = "child-marker@invalid.example"
    cached_marker = "cached-marker@invalid.example"
    raw_marker = "unused-marker@invalid.example"
    record = child.makeRecord(
        child.name,
        logging.ERROR,
        __file__,
        1,
        "Child message about %s",
        (message_marker,),
        (RuntimeError, RuntimeError(raw_marker), None),
    )
    record.exc_text = f"Cached exception text {cached_marker}"

    child.handle(record)

    output = stream.getvalue()
    _assert_patterns_absent(output, (message_marker, cached_marker, raw_marker))
    if output.count("[REDACTED_EMAIL]") != 2 or "Cached exception text" not in output:
        raise AssertionError("The handler did not sanitize the propagated message and cached text.")
    if record.exc_info is not None:
        raise AssertionError("The original exception remained available to the formatter.")


def test_formatter_failures_emit_only_fixed_placeholders() -> None:
    stream = io.StringIO()
    handler = _MemoryHandler(stream)
    handler.addFilter(RedactingFilter())
    logger = logging.Logger("fixture-format-failure")
    logger.addHandler(handler)
    marker = "format-failure@invalid.example"

    with patch.object(logging.Formatter, "formatException", side_effect=RuntimeError(marker)):
        logger.error("%d", marker, exc_info=ValueError(marker))

    output = stream.getvalue()
    _assert_patterns_absent(output, (marker,))
    if output.strip() != "[LOG_MESSAGE_UNAVAILABLE]\n[EXCEPTION_TEXT_UNAVAILABLE]":
        raise AssertionError("Formatting failures did not use the fixed placeholders.")
