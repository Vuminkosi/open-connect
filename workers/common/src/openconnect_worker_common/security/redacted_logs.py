"""Pattern-based redaction for opted-in Python loggers and handlers."""

import logging
import re

_EMAIL_RE = re.compile(r"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")
_BEARER_RE = re.compile(r"(Bearer\s+)[^\s\"']+", re.IGNORECASE)
_SECRET_ASSIGNMENT_RE = re.compile(
    r"(?i)(token|secret|password|api[_-]?key|authorization)\s*[:=]\s*['\"]?([^'\"\s,}]+)['\"]?"
)
_PHONE_RE = re.compile(r"\+?\d[\d\s().-]{7,}\d")
_CARD_RE = re.compile(r"\b\d{4}[- ]?\d{4}[- ]?\d{4}[- ]?\d{4}\b")

_REDACTED_EMAIL = "[REDACTED_EMAIL]"
_REDACTED_SECRET = "[REDACTED]"
_REDACTED_PHONE = "[REDACTED_PHONE]"
_EXCEPTION_FORMATTER = logging.Formatter()


def redact_text(text: str) -> str:
    """Scrub emails, bearer tokens, secrets, phone numbers, and card numbers."""
    redacted = _BEARER_RE.sub(lambda m: m.group(1) + _REDACTED_SECRET, text)
    redacted = _SECRET_ASSIGNMENT_RE.sub(lambda m: f"{m.group(1)}={_REDACTED_SECRET}", redacted)
    redacted = _EMAIL_RE.sub(_REDACTED_EMAIL, redacted)
    redacted = _CARD_RE.sub(_REDACTED_SECRET, redacted)
    redacted = _PHONE_RE.sub(_REDACTED_PHONE, redacted)
    return redacted


class RedactingFilter(logging.Filter):
    """Redact recognized patterns in a record's formatted message and exception text."""

    def filter(self, record: logging.LogRecord) -> bool:
        try:
            record.msg = redact_text(record.getMessage())
        except Exception:
            record.msg = "[LOG_MESSAGE_UNAVAILABLE]"
        record.args = ()

        if record.exc_info or record.exc_text is not None:
            try:
                text = record.exc_text
                if text is None and record.exc_info:
                    text = _EXCEPTION_FORMATTER.formatException(record.exc_info)
                record.exc_text = redact_text(text) if text is not None else None
            except Exception:
                record.exc_text = "[EXCEPTION_TEXT_UNAVAILABLE]"
            finally:
                # Keep only the sanitized traceback so formatters cannot render the original again.
                record.exc_info = None
        return True


def get_redacted_logger(name: str, level: int = logging.INFO) -> logging.Logger:
    """Return a named logger with pattern-based message and exception redaction."""
    logger = logging.getLogger(name)
    logger.setLevel(level)
    if not any(isinstance(f, RedactingFilter) for f in logger.filters):
        logger.addFilter(RedactingFilter())
    return logger
