"""Redacted logging: PII and secrets are scrubbed before they reach log output."""

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


def redact_text(text: str) -> str:
    """Scrub emails, bearer tokens, secrets, phone numbers, and card numbers."""
    redacted = _BEARER_RE.sub(lambda m: m.group(1) + _REDACTED_SECRET, text)
    redacted = _SECRET_ASSIGNMENT_RE.sub(lambda m: f"{m.group(1)}={_REDACTED_SECRET}", redacted)
    redacted = _EMAIL_RE.sub(_REDACTED_EMAIL, redacted)
    redacted = _CARD_RE.sub(_REDACTED_SECRET, redacted)
    redacted = _PHONE_RE.sub(_REDACTED_PHONE, redacted)
    return redacted


class RedactingFilter(logging.Filter):
    """Logging filter that redacts PII/secrets from the formatted message and args."""

    def filter(self, record: logging.LogRecord) -> bool:
        try:
            message = record.getMessage()
        except Exception:
            return True
        redacted = redact_text(message)
        record.msg = redacted
        record.args = ()
        return True


def get_redacted_logger(name: str, level: int = logging.INFO) -> logging.Logger:
    """Return a logger with redaction applied. Safe to use with untrusted text."""
    logger = logging.getLogger(name)
    logger.setLevel(level)
    if not any(isinstance(f, RedactingFilter) for f in logger.filters):
        logger.addFilter(RedactingFilter())
    return logger
