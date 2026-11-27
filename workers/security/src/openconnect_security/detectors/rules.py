"""Deterministic rules-only PII and secret detection."""

from __future__ import annotations

import datetime
import re

from openconnect_security.schemas import ScanSpan

_EMAIL_RE = re.compile(r"(?<![\w.+-])[\w.+-]+@[\w-]+(?:\.[\w-]+)+(?![\w.-])")
_PHONE_RE = re.compile(r"(?<!\w)(?:\+?\d[\d().\s-]{6,}\d)(?!\w)")
_PAYMENT_CARD_RE = re.compile(r"(?<!\d)(?:\d[ -]?){13,19}(?<!\s)(?<!-)(?!\d)")
_SA_ID_RE = re.compile(r"(?<!\w)(\d{13})(?!\w)")
_PASSPORT_CONTEXT_RE = re.compile(
    r"(?i)\bpassport(?:\s*(?:no\.?|number|id|#))?\s*[:=#-]?\s*([A-Z0-9]{6,10})\b"
)
_ACCOUNT_CONTEXT_RE = re.compile(
    r"(?i)\b(?:bank\s+account|account(?:\s*(?:no\.?|number|#))?|acct(?:\s*(?:no\.?|#))?|iban)"
    r"\s*[:=#-]\s*([A-Z0-9][A-Z0-9-]{5,24})\b"
)
_TAX_CONTEXT_RE = re.compile(
    r"(?i)\b(?:tax(?:\s*(?:id|no\.?|number|ref(?:erence)?))?|tin|ssn|vat(?:\s*(?:no\.?|number))?"
    r"|sars(?:\s*(?:ref(?:erence)?|tax\s*(?:no\.?|number)))?)"
    r"\s*[:=#-]\s*([A-Z0-9][A-Z0-9-]{6,16})\b"
)
_SECRET_PATTERNS: tuple[re.Pattern[str], ...] = (
    re.compile(r"(?i)\b(?:api[_-]?key|secret|password|token)\s*[:=]\s*[^\s,;]+"),
    re.compile(r"(?<![A-Za-z0-9_-])sk-(?:proj-)?[A-Za-z0-9_-]{20,}(?![A-Za-z0-9_-])"),
    re.compile(r"(?<![A-Za-z0-9_])(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,})(?![A-Za-z0-9_])"),
    re.compile(r"(?<![A-Z0-9])AKIA[0-9A-Z]{16}(?![A-Z0-9])"),
    re.compile(r"(?<![A-Za-z0-9_-])eyJ[A-Za-z0-9_-]{10,}\.eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}(?![A-Za-z0-9_-])"),
    re.compile(r"-----BEGIN (?:RSA |EC |OPENSSH |DSA )?PRIVATE KEY-----"),
)


def luhn_valid(digits: str) -> bool:
    """Return True iff ``digits`` is an all-digit string passing the Luhn checksum."""
    if not digits or not digits.isdigit():
        return False
    if len(set(digits)) == 1:
        return False
    total = 0
    parity = len(digits) % 2
    for idx, ch in enumerate(digits):
        d = int(ch)
        if idx % 2 == parity:
            d *= 2
            if d > 9:
                d -= 9
        total += d
    return total % 10 == 0


def is_valid_sa_id(candidate: str) -> bool:
    """Validate a 13-digit South African ID (YYMMDD date plausibility, citizenship, Luhn)."""
    if len(candidate) != 13 or not candidate.isdigit():
        return False
    yy = int(candidate[0:2])
    mm = int(candidate[2:4])
    dd = int(candidate[4:6])
    citizenship = candidate[10]
    if citizenship not in ("0", "1"):
        return False
    date_plausible = False
    for century in (1900, 2000):
        try:
            datetime.date(century + yy, mm, dd)
            date_plausible = True
            break
        except ValueError:
            continue
    if not date_plausible:
        return False
    return luhn_valid(candidate)


def is_valid_payment_card(candidate: str) -> bool:
    """Validate a 13-19 digit payment card number using Luhn checksum."""
    digits = re.sub(r"[ -]", "", candidate)
    if not (13 <= len(digits) <= 19) or not digits.isdigit():
        return False
    return luhn_valid(digits)


def _digit_count(value: str) -> int:
    return sum(1 for ch in value if ch.isdigit())


def _covered_by(start: int, end: int, ranges: list[tuple[int, int]]) -> bool:
    return any(r_start <= start and end <= r_end for r_start, r_end in ranges)


def scan(text: str) -> list[ScanSpan]:
    """Return sorted, deterministic detections as half-open Unicode scalar offsets."""
    spans: list[ScanSpan] = []
    structured_ranges: list[tuple[int, int]] = []

    for match in _EMAIL_RE.finditer(text):
        spans.append(
            ScanSpan(
                entityType="EMAIL",
                start=match.start(),
                end=match.end(),
                confidence=0.99,
                decision="mask",
            )
        )

    for match in _SA_ID_RE.finditer(text):
        candidate = match.group(1)
        if is_valid_sa_id(candidate):
            spans.append(
                ScanSpan(
                    entityType="GOVERNMENT_ID",
                    start=match.start(1),
                    end=match.end(1),
                    confidence=0.98,
                    decision="mask",
                )
            )
            structured_ranges.append((match.start(1), match.end(1)))

    for match in _PAYMENT_CARD_RE.finditer(text):
        raw = match.group(0).strip()
        if not raw:
            continue
        start = match.start() + match.group(0).find(raw)
        end = start + len(raw)
        if _covered_by(start, end, structured_ranges):
            continue
        if is_valid_payment_card(raw):
            spans.append(
                ScanSpan(
                    entityType="PAYMENT_CARD",
                    start=start,
                    end=end,
                    confidence=0.97,
                    decision="mask",
                )
            )
            structured_ranges.append((start, end))

    for match in _PASSPORT_CONTEXT_RE.finditer(text):
        value = match.group(1)
        if _digit_count(value) >= 1:
            spans.append(
                ScanSpan(
                    entityType="GOVERNMENT_ID",
                    start=match.start(1),
                    end=match.end(1),
                    confidence=0.92,
                    decision="mask",
                )
            )
            structured_ranges.append((match.start(1), match.end(1)))

    for match in _ACCOUNT_CONTEXT_RE.finditer(text):
        value = match.group(1)
        if _digit_count(value) >= 4:
            spans.append(
                ScanSpan(
                    entityType="ACCOUNT_NUMBER",
                    start=match.start(1),
                    end=match.end(1),
                    confidence=0.92,
                    decision="mask",
                )
            )
            structured_ranges.append((match.start(1), match.end(1)))

    for match in _TAX_CONTEXT_RE.finditer(text):
        value = match.group(1)
        if _digit_count(value) >= 5:
            spans.append(
                ScanSpan(
                    entityType="GOVERNMENT_ID",
                    start=match.start(1),
                    end=match.end(1),
                    confidence=0.93,
                    decision="mask",
                )
            )
            structured_ranges.append((match.start(1), match.end(1)))

    seen_secret_ranges: list[tuple[int, int]] = []
    for pattern in _SECRET_PATTERNS:
        for match in pattern.finditer(text):
            r = (match.start(), match.end())
            if _covered_by(r[0], r[1], seen_secret_ranges):
                continue
            seen_secret_ranges.append(r)
            spans.append(
                ScanSpan(
                    entityType="SECRET",
                    start=r[0],
                    end=r[1],
                    confidence=0.95,
                    decision="mask",
                )
            )
            structured_ranges.append(r)

    for match in _PHONE_RE.finditer(text):
        if _covered_by(match.start(), match.end(), structured_ranges):
            continue
        digits_only = re.sub(r"\D", "", match.group(0))
        if len(digits_only) < 7 or len(digits_only) > 15:
            continue
        spans.append(
            ScanSpan(
                entityType="PHONE",
                start=match.start(),
                end=match.end(),
                confidence=0.9,
                decision="mask",
            )
        )

    return sorted(spans, key=lambda span: (span.start, span.end, span.entityType))
