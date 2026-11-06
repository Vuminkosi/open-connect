"""Deterministic rules-only PII and secret detection."""

import re

from openconnect_security.schemas import ScanSpan

_PATTERNS: tuple[tuple[str, re.Pattern[str]], ...] = (
    ("EMAIL", re.compile(r"(?<![\w.+-])[\w.+-]+@[\w-]+(?:\.[\w-]+)+(?![\w.-])")),
    ("PHONE", re.compile(r"(?<!\w)(?:\+?\d[\d().\s-]{6,}\d)(?!\w)")),
    ("SECRET", re.compile(r"(?i)\b(?:api[_-]?key|secret|password|token)\s*[:=]\s*[^\s,;]+")),
)


def scan(text: str) -> list[ScanSpan]:
    """Return sorted, non-overlapping-within-type detections as half-open offsets."""
    spans: list[ScanSpan] = []
    for entity_type, pattern in _PATTERNS:
        for match in pattern.finditer(text):
            spans.append(
                ScanSpan(
                    entityType=entity_type,
                    start=match.start(),
                    end=match.end(),
                    confidence=0.99 if entity_type == "EMAIL" else 0.9,
                    decision="mask",
                )
            )
    return sorted(spans, key=lambda span: (span.start, span.end, span.entityType))
