"""Malware-screened document processing orchestration."""

from __future__ import annotations

import math
import time
from dataclasses import dataclass

from openconnect_document_intelligence.malware import ClamAvScanner
from openconnect_document_intelligence.ocr import MAX_INPUT_BYTES, OcrPage, TesseractOcr
from openconnect_document_intelligence.pdf import render_pdf_page
from openconnect_document_intelligence.tiff import render_tiff_page

MAX_DOCUMENT_PIXELS = 80_000_000


@dataclass(frozen=True)
class DocumentResult:
    pages: tuple[OcrPage, ...]


@dataclass(frozen=True)
class DocumentPageResult:
    page_index: int
    status: str
    page: OcrPage | None
    reason: str | None = None


@dataclass(frozen=True)
class PartialDocumentResult:
    pages: tuple[DocumentPageResult, ...]
    status: str


class DocumentAnalyzer:
    """Scan original bytes before parsing or OCR; caller provides an OS sandbox."""

    def __init__(self, scanner: ClamAvScanner, ocr: TesseractOcr, *, timeout_seconds: float = 120):
        if not math.isfinite(timeout_seconds) or not 0 < timeout_seconds <= 120:
            raise ValueError("invalid_timeout")
        self._scanner = scanner
        self._ocr = ocr
        self._timeout = timeout_seconds

    def analyze(self, content: bytes, *, timeout_seconds: float | None = None) -> DocumentResult:
        if type(content) is not bytes or not content or len(content) > MAX_INPUT_BYTES:
            raise ValueError("invalid_document")
        if timeout_seconds is not None and (not math.isfinite(timeout_seconds) or timeout_seconds <= 0):
            raise ValueError("invalid_timeout")
        deadline = time.monotonic() + min(self._timeout, timeout_seconds or self._timeout)
        self._scanner.scan(content, timeout_seconds=self._remaining(deadline))
        self._remaining(deadline)
        renderer = (render_pdf_page if content.startswith(b"%PDF-") else
                    render_tiff_page if content.startswith((b"II*\x00", b"MM\x00*")) else None)
        if renderer is None:
            return DocumentResult((self._ocr.analyze(content, timeout_seconds=self._remaining(deadline)),))
        pages: list[OcrPage] = []
        pixels = 0
        page_count = 1
        while len(pages) < page_count:
            page = renderer(content, len(pages), timeout_seconds=self._remaining(deadline))
            if pages and page.page_count != page_count:
                raise RuntimeError("document_invalid")
            page_count = page.page_count
            pixels += page.width * page.height
            if pixels > MAX_DOCUMENT_PIXELS:
                raise ValueError("document_limit_exceeded")
            pages.append(self._ocr.analyze(page.png, timeout_seconds=self._remaining(deadline)))
        return DocumentResult(tuple(pages))

    def analyze_partial(self, content: bytes, *, timeout_seconds: float | None = None) -> PartialDocumentResult:
        """Scan once, retaining explicit page failures without exposing private diagnostics."""
        if type(content) is not bytes or not content or len(content) > MAX_INPUT_BYTES:
            raise ValueError("invalid_document")
        if timeout_seconds is not None and (not math.isfinite(timeout_seconds) or timeout_seconds <= 0):
            raise ValueError("invalid_timeout")
        deadline = time.monotonic() + min(self._timeout, timeout_seconds or self._timeout)
        self._scanner.scan(content, timeout_seconds=self._remaining(deadline))
        self._remaining(deadline)
        renderer = (render_pdf_page if content.startswith(b"%PDF-") else
                    render_tiff_page if content.startswith((b"II*\x00", b"MM\x00*")) else None)
        if renderer is None:
            try:
                page = self._ocr.analyze(content, timeout_seconds=self._remaining(deadline))
                return PartialDocumentResult((DocumentPageResult(0, "completed", page),), "completed")
            except (ValueError, RuntimeError, OSError):
                return PartialDocumentResult((DocumentPageResult(0, "failed", None, "ocr_unavailable"),), "partial")
        # A first-page failure cannot safely establish the actual page count.
        first = renderer(content, 0, timeout_seconds=self._remaining(deadline))
        page_count = first.page_count
        if isinstance(page_count, bool) or not isinstance(page_count, int) or not 1 <= page_count <= 30 or first.page_index != 0:
            raise ValueError("invalid_document")
        pages: list[DocumentPageResult] = []
        pixels = 0
        for index in range(page_count):
            try:
                remaining = self._remaining(deadline)
            except RuntimeError:
                pages.extend(DocumentPageResult(item, "failed", None, "deadline_exceeded") for item in range(index, page_count))
                break
            if index == 0:
                rendered = first
            else:
                try:
                    rendered = renderer(content, index, timeout_seconds=remaining)
                except (ValueError, RuntimeError, OSError):
                    pages.append(DocumentPageResult(index, "failed", None, "page_unavailable"))
                    continue
            if rendered.page_count != page_count or rendered.page_index != index:
                raise ValueError("invalid_document")
            pixels += rendered.width * rendered.height
            if pixels > MAX_DOCUMENT_PIXELS:
                raise ValueError("document_limit_exceeded")
            try:
                page = self._ocr.analyze(rendered.png, timeout_seconds=self._remaining(deadline))
                pages.append(DocumentPageResult(index, "completed", page))
            except (ValueError, RuntimeError, OSError):
                pages.append(DocumentPageResult(index, "failed", None, "ocr_unavailable"))
        return PartialDocumentResult(tuple(pages), "completed" if all(page.status == "completed" for page in pages) else "partial")

    @staticmethod
    def _remaining(deadline: float) -> float:
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise RuntimeError("document_timeout")
        return min(remaining, 120)
