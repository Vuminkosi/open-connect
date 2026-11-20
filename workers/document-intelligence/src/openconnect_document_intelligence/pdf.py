"""Bounded PDF page rendering via a separate child process."""

from __future__ import annotations

import base64
import binascii
import json
import math
import sys
from dataclasses import dataclass
from pathlib import Path

from openconnect_document_intelligence.ocr import MAX_INPUT_BYTES, _prepare_image, _run_tesseract


@dataclass(frozen=True)
class RenderedPage:
    page_count: int
    page_index: int
    png: bytes
    width: int
    height: int


def render_pdf_page(content: bytes, page_index: int, *, timeout_seconds: float = 30) -> RenderedPage:
    """Render one PDF page; caller must supply a network-isolated OS sandbox.

    This child-process boundary and timeout are not an OS security sandbox.
    PDF and image data remain application data and must not be logged.
    """
    if not content or len(content) > MAX_INPUT_BYTES or not content.startswith(b"%PDF-"):
        raise ValueError("invalid_document")
    if isinstance(page_index, bool) or not isinstance(page_index, int) or not 0 <= page_index < 30:
        raise ValueError("invalid_page")
    if not math.isfinite(timeout_seconds) or not 0 < timeout_seconds <= 120:
        raise ValueError("invalid_timeout")
    try:
        raw = _run_tesseract([sys.executable, str(Path(__file__).with_name("_pdf_child.py")),
                              str(page_index)], content, timeout_seconds)
    except RuntimeError as exc:
        raise RuntimeError("document_unavailable") from exc
    try:
        result = json.loads(raw)
        if "error" in result:
            error = result["error"]
            if error in {"invalid_document", "encrypted_document", "page_limit_exceeded", "invalid_page"}:
                raise ValueError(error)
            raise ValueError("invalid_document")
        if (set(result) != {"pageCount", "pageIndex", "png"} or result["pageIndex"] != page_index
                or not isinstance(result["pageCount"], int) or not 1 <= result["pageCount"] <= 30):
            raise ValueError("invalid_document")
        png = base64.b64decode(result["png"], validate=True)
        normalized, width, height = _prepare_image(png)
        return RenderedPage(result["pageCount"], page_index, normalized, width, height)
    except (KeyError, TypeError, json.JSONDecodeError, binascii.Error) as exc:
        raise ValueError("invalid_document") from exc
