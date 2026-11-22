"""Bounded, source-cited table candidates inferred from OCR line alignment."""

from __future__ import annotations

from dataclasses import dataclass

from openconnect_document_intelligence.layout import LineRegion, extract_line_regions
from openconnect_document_intelligence.ocr import OcrPage, WordSpan

MAX_TABLES = 100
MAX_CELLS = 10000
_COLUMN_GAP = 0.04
_ALIGNMENT_TOLERANCE = 0.025
_MAX_ROW_GAP = 0.08


@dataclass(frozen=True)
class TableCell:
    row: int
    column: int
    start: int
    end: int
    span_indexes: tuple[int, ...]
    box: tuple[float, float, float, float]
    minimum_ocr_confidence: float


@dataclass(frozen=True)
class TableCandidate:
    page_index: int
    rows: int
    columns: int
    cells: tuple[TableCell, ...]


@dataclass(frozen=True)
class TableCandidates:
    tables: tuple[TableCandidate, ...]
    warnings: tuple[str, ...]


def _row(page: OcrPage, line: LineRegion) -> tuple[tuple[int, ...], ...]:
    columns: list[list[int]] = []
    previous: WordSpan | None = None
    for index in line.span_indexes:
        span = page.spans[index]
        if previous is not None and span.box[0] < previous.box[0]:
            return ()
        if previous is None or span.box[0] - previous.box[2] >= _COLUMN_GAP:
            columns.append([])
        columns[-1].append(index)
        previous = span
    return tuple(tuple(column) for column in columns) if 2 <= len(columns) <= 20 else ()


def extract_table_candidates(pages: tuple[OcrPage, ...]) -> TableCandidates:
    """Return aligned rows as candidates, never as verified semantic tables.

    Cells cite original OCR spans and character offsets. Layout truncation or
    table caps are reported; empty, merged and irregular cells are not inferred.
    """
    layout = extract_line_regions(pages)
    candidates: list[TableCandidate] = []
    cells_used = 0
    limited = False
    lines = layout.lines
    cursor = 0
    while cursor < len(lines):
        first = lines[cursor]
        page = pages[first.page_index]
        columns = _row(page, first)
        if not columns:
            cursor += 1
            continue
        starts = [page.spans[column[0]].box[0] for column in columns]
        rows = [columns]
        previous = first
        next_line = cursor + 1
        while next_line < len(lines):
            line = lines[next_line]
            if (line.page_index != first.page_index or line.box[1] < previous.box[1]
                    or line.box[1] - previous.box[3] > _MAX_ROW_GAP):
                break
            row = _row(page, line)
            if len(row) != len(columns) or any(
                    abs(page.spans[column[0]].box[0] - starts[index]) > _ALIGNMENT_TOLERANCE
                    for index, column in enumerate(row)):
                break
            rows.append(row)
            previous = line
            next_line += 1
        if len(rows) < 2:
            cursor += 1
            continue
        if len(candidates) >= MAX_TABLES or cells_used + len(rows) * len(columns) > MAX_CELLS:
            limited = True
            break
        cells = []
        for row_index, row in enumerate(rows):
            for column_index, indexes in enumerate(row):
                spans = [page.spans[index] for index in indexes]
                cells.append(TableCell(row_index, column_index, spans[0].start, spans[-1].end, indexes,
                    (min(span.box[0] for span in spans), min(span.box[1] for span in spans),
                     max(span.box[2] for span in spans), max(span.box[3] for span in spans)),
                    min(span.confidence for span in spans)))
        candidates.append(TableCandidate(first.page_index, len(rows), len(columns), tuple(cells)))
        cells_used += len(cells)
        cursor = next_line
    warnings = list(layout.warnings)
    if candidates:
        warnings.append("heuristic_tables_require_review")
    if limited:
        warnings.append("table_limit_reached")
    return TableCandidates(tuple(candidates), tuple(warnings))
