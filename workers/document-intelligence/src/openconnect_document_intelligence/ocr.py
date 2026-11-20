"""Bounded image admission and offline Tesseract OCR adapter.

Run the consuming service in an isolated, resource-limited sandbox. This
adapter does not itself provide an OS security sandbox or malware scanning.
"""

from __future__ import annotations

import csv
import io
import math
import os
import subprocess
import threading
import warnings
from dataclasses import dataclass
from pathlib import Path

from PIL import Image, ImageOps, UnidentifiedImageError

from openconnect_worker_common.models.runtime_cache import RuntimeModelCacheManager, RuntimeModelManifest

MAX_INPUT_BYTES = 10 * 1024 * 1024
MAX_IMAGE_PIXELS = 16_000_000
MAX_OUTPUT_BYTES = 8 * 1024 * 1024
MAX_WORDS = 50_000


def _run_tesseract(command: list[str], content: bytes, timeout: float) -> bytes:
    """Stream bounded output without retaining document content on disk."""
    try:
        process = subprocess.Popen(
            command, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
            env={"OMP_THREAD_LIMIT": "1", "PATH": os.defpath},
        )
    except OSError as exc:
        raise RuntimeError("ocr_unavailable") from exc
    output = bytearray()
    overflow = threading.Event()
    failed = threading.Event()

    def send() -> None:
        try:
            assert process.stdin is not None
            process.stdin.write(content)
            process.stdin.close()
        except (OSError, ValueError):
            failed.set()

    def receive() -> None:
        try:
            assert process.stdout is not None
            while chunk := process.stdout.read(65536):
                if len(output) + len(chunk) > MAX_OUTPUT_BYTES:
                    overflow.set()
                    process.kill()
                    return
                output.extend(chunk)
        except (OSError, ValueError):
            failed.set()

    writer = threading.Thread(target=send, daemon=True)
    reader = threading.Thread(target=receive, daemon=True)
    writer.start()
    reader.start()
    try:
        process.wait(timeout=timeout)
    except subprocess.TimeoutExpired as exc:
        process.kill()
        process.wait()
        raise RuntimeError("ocr_unavailable") from exc
    finally:
        writer.join(timeout=1)
        reader.join(timeout=1)
        if process.stdin is not None and not writer.is_alive():
            process.stdin.close()
        if process.stdout is not None and not reader.is_alive():
            process.stdout.close()
    if overflow.is_set():
        raise RuntimeError("ocr_output_limit")
    if failed.is_set() or process.returncode != 0 or writer.is_alive() or reader.is_alive():
        raise RuntimeError("ocr_unavailable")
    return bytes(output)


@dataclass(frozen=True)
class WordSpan:
    text: str
    start: int
    end: int
    box: tuple[float, float, float, float]
    confidence: float


@dataclass(frozen=True)
class OcrPage:
    text: str
    width: int
    height: int
    spans: tuple[WordSpan, ...]
    model_id: str
    model_fingerprint: str
    language: str = "eng"


def _prepare_image(content: bytes) -> tuple[bytes, int, int]:
    if not content or len(content) > MAX_INPUT_BYTES:
        raise ValueError("invalid_image")
    if not (content.startswith(b"\x89PNG\r\n\x1a\n") or content.startswith(b"\xff\xd8\xff")):
        raise ValueError("unsupported_format")
    try:
        with warnings.catch_warnings():
            warnings.simplefilter("error", Image.DecompressionBombWarning)
            with Image.open(io.BytesIO(content)) as image:
                if image.format not in {"PNG", "JPEG"} or image.width * image.height > MAX_IMAGE_PIXELS:
                    raise ValueError("image_limit_exceeded")
                if getattr(image, "n_frames", 1) != 1:
                    raise ValueError("multipage_image_unsupported")
                image.load()
                normalized = ImageOps.exif_transpose(image).convert("RGB")
                out = io.BytesIO()
                normalized.save(out, format="PNG")
                return out.getvalue(), normalized.width, normalized.height
    except (UnidentifiedImageError, OSError, Image.DecompressionBombError,
            Image.DecompressionBombWarning) as exc:
        raise ValueError("invalid_image") from exc


def _parse_tsv(data: bytes, width: int, height: int) -> tuple[str, tuple[WordSpan, ...]]:
    if len(data) > MAX_OUTPUT_BYTES:
        raise RuntimeError("ocr_output_limit")
    try:
        reader = csv.DictReader(io.StringIO(data.decode("utf-8")), delimiter="\t")
        required = {"level", "left", "top", "width", "height", "conf", "text"}
        if not reader.fieldnames or not required.issubset(reader.fieldnames):
            raise ValueError("invalid_tsv")
        parts: list[str] = []
        spans: list[WordSpan] = []
        position = 0
        for row in reader:
            if row["level"] != "5" or not row["text"] or not row["text"].strip():
                continue
            text = row["text"].strip()
            left, top, box_width, box_height = (int(row[key]) for key in ("left", "top", "width", "height"))
            confidence = float(row["conf"])
            if (not math.isfinite(confidence) or not 0 <= confidence <= 100 or left < 0 or top < 0
                    or box_width <= 0 or box_height <= 0 or left + box_width > width
                    or top + box_height > height):
                raise ValueError("invalid_tsv")
            if len(spans) >= MAX_WORDS:
                raise ValueError("invalid_tsv")
            if parts:
                position += 1
            start = position
            position += len(text)
            parts.append(text)
            spans.append(WordSpan(text, start, position,
                                  (left / width, top / height, (left + box_width) / width,
                                   (top + box_height) / height), confidence / 100))
        return " ".join(parts), tuple(spans)
    except (UnicodeError, ValueError, TypeError, KeyError, csv.Error) as exc:
        raise RuntimeError("ocr_output_invalid") from exc


class TesseractOcr:
    """Single-page OCR with a locally verified English traineddata asset."""

    def __init__(self, cache: RuntimeModelCacheManager, manifest: RuntimeModelManifest,
                 executable: Path, *, timeout_seconds: float = 30,
                 license_accepted: bool = False, commercial_use: bool = False) -> None:
        if (not executable.is_absolute() or not executable.is_file()
                or not math.isfinite(timeout_seconds) or not 0 < timeout_seconds <= 120):
            raise ValueError("invalid_ocr_configuration")
        if "eng.traineddata" not in {asset.relativePath for asset in manifest.assets}:
            raise ValueError("model_asset_missing")
        self._cache = cache
        self._manifest = manifest.model_copy(deep=True)
        self._executable = executable
        self._timeout = timeout_seconds
        self._license_accepted = license_accepted
        self._commercial_use = commercial_use
        self._verify()

    def _verify(self):
        result = self._cache.verify_for_request(self._manifest, license_accepted=self._license_accepted,
                                                commercial_use=self._commercial_use)
        if not result.ready or result.revision_dir is None or result.fingerprint is None:
            raise RuntimeError("model_unavailable")
        return result

    def analyze(self, content: bytes) -> OcrPage:
        prepared, width, height = _prepare_image(content)
        result = self._verify()
        output = _run_tesseract(
            [str(self._executable), "stdin", "stdout", "--tessdata-dir",
             str(result.revision_dir / "assets"), "-l", "eng", "--psm", "3",
             "-c", "tessedit_create_tsv=1"], prepared, self._timeout,
        )
        text, spans = _parse_tsv(output, width, height)
        return OcrPage(text, width, height, spans, self._manifest.modelId, result.fingerprint)
