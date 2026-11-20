"""Malware-screened document processing orchestration."""

from __future__ import annotations

import math
import time
from dataclasses import dataclass

from openconnect_document_intelligence.malware import ClamAvScanner
from openconnect_document_intelligence.ocr import MAX_INPUT_BYTES, OcrPage, TesseractOcr
from openconnect_document_intelligence.pdf import render_pdf_page

MAX_DOCUMENT_PIXELS = 80_000_000


@dataclass(frozen=True)
class DocumentResult:
    pages: tuple[OcrPage, ...]


class DocumentAnalyzer:
    """Scan original bytes before parsing or OCR; caller provides an OS sandbox."""

    def __init__(self, scanner: ClamAvScanner, ocr: TesseractOcr, *, timeout_seconds: float = 120):
        if not math.isfinite(timeout_seconds) or not 0 < timeout_seconds <= 120:
            raise ValueError("invalid_timeout")
        self._scanner = scanner
        self._ocr = ocr
        self._timeout = timeout_seconds

    def analyze(self, content: bytes, *, timeout_seconds: float | None = None) -> DocumentResult:
        if not content or len(content) > MAX_INPUT_BYTES:
            raise ValueError("invalid_document")
        if timeout_seconds is not None and (not math.isfinite(timeout_seconds) or timeout_seconds <= 0):
            raise ValueError("invalid_timeout")
        deadline = time.monotonic() + min(self._timeout, timeout_seconds or self._timeout)
        self._scanner.scan(content, timeout_seconds=self._remaining(deadline))
        self._remaining(deadline)
        if not content.startswith(b"%PDF-"):
            return DocumentResult((self._ocr.analyze(content, timeout_seconds=self._remaining(deadline)),))
        pages: list[OcrPage] = []
        pixels = 0
        page_count = 1
        while len(pages) < page_count:
            page = render_pdf_page(content, len(pages), timeout_seconds=self._remaining(deadline))
            if pages and page.page_count != page_count:
                raise RuntimeError("document_invalid")
            page_count = page.page_count
            pixels += page.width * page.height
            if pixels > MAX_DOCUMENT_PIXELS:
                raise ValueError("document_limit_exceeded")
            pages.append(self._ocr.analyze(page.png, timeout_seconds=self._remaining(deadline)))
        return DocumentResult(tuple(pages))

    @staticmethod
    def _remaining(deadline: float) -> float:
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise RuntimeError("document_timeout")
        return min(remaining, 120)
