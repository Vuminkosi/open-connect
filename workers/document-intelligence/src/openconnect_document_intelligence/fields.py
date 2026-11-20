"""Extract literal, source-cited label/value candidates from OCR lines."""

from __future__ import annotations

from dataclasses import dataclass

from openconnect_document_intelligence.ocr import OcrPage

MAX_FIELDS = 1000
MAX_LABEL_CHARS = 128
MAX_VALUE_CHARS = 1024


@dataclass(frozen=True)
class FieldCandidate:
    page_index: int
    label: str
    value: str
    label_start: int
    label_end: int
    value_start: int
    value_end: int
    label_span_indexes: tuple[int, ...]
    value_span_indexes: tuple[int, ...]
    minimum_ocr_confidence: float


@dataclass(frozen=True)
class FieldCandidates:
    fields: tuple[FieldCandidate, ...]
    warnings: tuple[str, ...]


def extract_field_candidates(pages: tuple[OcrPage, ...]) -> FieldCandidates:
    """Return literal colon-delimited candidates; no semantic accuracy is implied.

    Offsets use Python Unicode code points in the returned page text. The caller
    must treat both OCR and label matching as untrusted, reviewable suggestions.
    """
    fields: list[FieldCandidate] = []
    truncated = False
    skipped = False
    for page_index, page in enumerate(pages):
        spans = page.spans
        span_index = 0
        offset = 0
        for line in page.text.splitlines(keepends=True):
            line_end = offset + len(line.rstrip("\r\n"))
            while span_index < len(spans) and spans[span_index].end <= offset:
                span_index += 1
            separator = page.text.find(":", offset, line_end)
            if separator < 0:
                offset += len(line)
                continue
            label_start, label_end = offset, separator
            value_start, value_end = separator + 1, line_end
            while label_start < label_end and page.text[label_start].isspace():
                label_start += 1
            while label_end > label_start and page.text[label_end - 1].isspace():
                label_end -= 1
            while value_start < value_end and page.text[value_start].isspace():
                value_start += 1
            while value_end > value_start and page.text[value_end - 1].isspace():
                value_end -= 1
            if not label_start < label_end or not value_start < value_end:
                offset += len(line)
                continue
            if label_end - label_start > MAX_LABEL_CHARS or value_end - value_start > MAX_VALUE_CHARS:
                skipped = True
                offset += len(line)
                continue
            label_spans: list[int] = []
            value_spans: list[int] = []
            current = span_index
            while current < len(spans) and spans[current].start < line_end:
                span = spans[current]
                if span.end > label_start and span.start < label_end:
                    label_spans.append(current)
                if span.end > value_start and span.start < value_end:
                    value_spans.append(current)
                current += 1
            if label_spans and value_spans:
                if len(fields) == MAX_FIELDS:
                    truncated = True
                    break
                fields.append(FieldCandidate(
                    page_index=page_index, label=page.text[label_start:label_end],
                    value=page.text[value_start:value_end], label_start=label_start,
                    label_end=label_end, value_start=value_start, value_end=value_end,
                    label_span_indexes=tuple(label_spans), value_span_indexes=tuple(value_spans),
                    minimum_ocr_confidence=min(spans[i].confidence for i in label_spans + value_spans),
                ))
            else:
                skipped = True
            offset += len(line)
        if truncated:
            break
    warnings = tuple(code for enabled, code in (
        (skipped, "field_candidates_skipped"), (truncated, "field_limit_reached")) if enabled)
    return FieldCandidates(tuple(fields), warnings)
