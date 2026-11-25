"""Bounded media validation, magic-byte MIME sniffing, and EXIF orientation normalization."""

from __future__ import annotations

import hashlib
import io
from dataclasses import dataclass

from PIL import Image, ImageOps, UnidentifiedImageError

MAX_FILE_BYTES = 20 * 1024 * 1024  # 20 MiB
MAX_IMAGE_BYTES = 10 * 1024 * 1024  # 10 MiB
MAX_IMAGE_PIXELS = 20_000_000  # 20 megapixels

Image.MAX_IMAGE_PIXELS = MAX_IMAGE_PIXELS

_PNG_MAGIC = b"\x89PNG\r\n\x1a\n"
_JPEG_MAGIC = b"\xff\xd8\xff"
_PDF_MAGIC = b"%PDF-"
_ZIP_MAGIC = b"PK\x03\x04"


class MediaLimitExceededError(ValueError):
    """Uploaded bytes or decoded pixel count exceeds configured hard limits."""


class UnsupportedMediaError(ValueError):
    """Uploaded bytes do not match an allowed/supported magic-byte signature."""


def sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def sniff_mime(data: bytes) -> str:
    """Identify MIME type strictly from leading magic bytes, never filename extension."""
    if not data:
        raise UnsupportedMediaError("empty_payload")
    if data.startswith(_PNG_MAGIC):
        return "image/png"
    if data.startswith(_JPEG_MAGIC):
        return "image/jpeg"
    if data.startswith(_PDF_MAGIC):
        return "application/pdf"
    if data.startswith(_ZIP_MAGIC):
        return "application/zip"
    # Check if valid UTF-8 text without NUL bytes
    if b"\x00" not in data[:4096]:
        try:
            data[:4096].decode("utf-8")
            return "text/plain"
        except UnicodeDecodeError:
            pass
    return "application/octet-stream"


@dataclass(frozen=True)
class ValidatedImage:
    width: int
    height: int
    content_type: str
    sha256: str
    orientation_normalized: bool
    image: Image.Image


def validate_and_normalize_image(data: bytes) -> ValidatedImage:
    """Validate JPEG/PNG magic bytes, byte & pixel limits, and normalize EXIF orientation."""
    if not data:
        raise UnsupportedMediaError("empty_image")
    if len(data) > MAX_IMAGE_BYTES:
        raise MediaLimitExceededError("image_bytes_limit_exceeded")
    mime = sniff_mime(data)
    if mime not in ("image/jpeg", "image/png"):
        raise UnsupportedMediaError("unsupported_image_format")

    try:
        with Image.open(io.BytesIO(data)) as img:
            if img.format not in ("JPEG", "PNG"):
                raise UnsupportedMediaError("unsupported_image_format")
            width, height = img.size
            if width <= 0 or height <= 0 or (width * height) > MAX_IMAGE_PIXELS:
                raise MediaLimitExceededError("image_pixels_limit_exceeded")
            img.load()
            normalized = ImageOps.exif_transpose(img)
            norm_w, norm_h = normalized.size
            if norm_w <= 0 or norm_h <= 0 or (norm_w * norm_h) > MAX_IMAGE_PIXELS:
                raise MediaLimitExceededError("image_pixels_limit_exceeded")
            # Copy pixel buffer so closing `img` never invalidates `normalized`
            copied = normalized.copy()
    except Image.DecompressionBombError:
        raise MediaLimitExceededError("image_pixels_limit_exceeded") from None
    except (UnidentifiedImageError, OSError, SyntaxError):
        raise UnsupportedMediaError("malformed_image") from None

    return ValidatedImage(
        width=copied.size[0],
        height=copied.size[1],
        content_type=mime,
        sha256=sha256_bytes(data),
        orientation_normalized=True,
        image=copied,
    )
