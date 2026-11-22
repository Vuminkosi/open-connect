"""Derive source-cited OCR line regions without semantic layout claims."""

from __future__ import annotations

import math
from dataclasses import dataclass

from openconnect_document_intelligence.ocr import OcrPage

MAX_LINES = 50000


@dataclass(frozen=True)
class LineRegion:
    page_index: int
    start: int
    end: int
    span_indexes: tuple[int, ...]
    box: tuple[float, float, float, float]
    minimum_ocr_confidence: float


@dataclass(frozen=True)
class LineRegions:
    lines: tuple[LineRegion, ...]
    warnings: tuple[str, ...]


def extract_line_regions(pages: tuple[OcrPage, ...]) -> LineRegions:
    """Group contiguous OCR spans by line; boxes and offsets refer to source pages.

    These are OCR line regions, not table cells or a semantic reading-order model.
    """
    lines: list[LineRegion] = []
    truncated = False
    if not 1 <= len(pages) <= 30:
        raise ValueError("invalid_ocr_spans")
    for page_index, page in enumerate(pages):
        if len(page.spans) > 50000:
            raise ValueError("invalid_ocr_spans")
        previous_end = 0
        for index, span in enumerate(page.spans):
            if (isinstance(span.start, bool) or isinstance(span.end, bool)
                    or not isinstance(span.start, int) or not isinstance(span.end, int)
                    or not 0 <= span.start < span.end <= len(page.text)
                    or page.text[span.start:span.end] != span.text
                    or (span.start != 0 if index == 0 else
                        span.start != previous_end + 1 or page.text[previous_end:span.start] not in {" ", "\n"})
                    or len(span.box) != 4 or any(not math.isfinite(value) or not 0 <= value <= 1 for value in span.box)
                    or span.box[0] >= span.box[2] or span.box[1] >= span.box[3]
                    or not math.isfinite(span.confidence) or not 0 <= span.confidence <= 1):
                raise ValueError("invalid_ocr_spans")
            previous_end = span.end
        if previous_end != len(page.text):
            raise ValueError("invalid_ocr_spans")
        start = 0
        while start < len(page.spans):
            if len(lines) == MAX_LINES:
                truncated = True
                break
            end = start + 1
            while end < len(page.spans):
                separator = page.text[page.spans[end - 1].end:page.spans[end].start]
                if separator == "\n":
                    break
                if separator != " ":
                    raise ValueError("invalid_ocr_spans")
                end += 1
            spans = page.spans[start:end]
            indexes = tuple(range(start, end))
            lines.append(LineRegion(page_index, spans[0].start, spans[-1].end, indexes,
                (min(span.box[0] for span in spans), min(span.box[1] for span in spans),
                 max(span.box[2] for span in spans), max(span.box[3] for span in spans)),
                min(span.confidence for span in spans)))
            start = end
        if truncated:
            break
    return LineRegions(tuple(lines), ("line_limit_reached",) if truncated else ())
