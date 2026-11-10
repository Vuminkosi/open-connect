"""Bounded in-memory audio validation for WAV/PCM payloads.

Runs entirely in memory (no disk writes), enforces MIME/codec/sample-rate/
channel/duration bounds, and verifies WAV container headers against the
declared PCM parameters. Raises :class:`AudioValidationError` with a stable
machine-readable ``reason`` on any mismatch.
"""

from __future__ import annotations

import base64
import binascii
import struct
from dataclasses import dataclass

from openconnect_transcription.schemas import (
    ALLOWED_SAMPLE_RATES,
    PCM_MIMES,
    WAV_MIMES,
)


class AudioValidationError(Exception):
    """Raised when audio metadata or container bytes fail validation."""

    def __init__(self, reason: str) -> None:
        super().__init__(reason)
        self.reason = reason


@dataclass(frozen=True)
class ValidatedAudio:
    raw_pcm: bytes
    mime_type: str
    sample_rate_hz: int
    channels: int
    bits_per_sample: int
    duration_ms: int


def _decode_b64(payload: str, max_bytes: int) -> bytes:
    if len(payload) > 7_000_000:
        raise AudioValidationError("audio_too_large")
    try:
        raw = base64.b64decode(payload, validate=True)
    except (binascii.Error, ValueError):
        raise AudioValidationError("invalid_audio_encoding") from None
    if len(raw) > max_bytes:
        raise AudioValidationError("audio_too_large")
    if not raw:
        raise AudioValidationError("invalid_audio_empty")
    return raw


def _parse_wav_header(raw: bytes) -> tuple[int, int, int, bytes]:
    """Return (channels, sample_rate_hz, bits_per_sample, pcm_data)."""
    if len(raw) < 44:
        raise AudioValidationError("invalid_wav_header")
    if raw[0:4] != b"RIFF" or raw[8:12] != b"WAVE":
        raise AudioValidationError("invalid_wav_header")
    try:
        (audio_format,) = struct.unpack_from("<H", raw, 20)
        (channels,) = struct.unpack_from("<H", raw, 22)
        (sample_rate,) = struct.unpack_from("<I", raw, 24)
        (bits_per_sample,) = struct.unpack_from("<H", raw, 34)
    except struct.error:
        raise AudioValidationError("invalid_wav_header") from None
    if audio_format != 1:
        raise AudioValidationError("unsupported_wav_codec")
    # Locate the data chunk (fmt chunk may carry extra bytes).
    offset = 36
    pcm: bytes | None = None
    while offset + 8 <= len(raw):
        chunk_id = raw[offset : offset + 4]
        (chunk_size,) = struct.unpack_from("<I", raw, offset + 4)
        if chunk_id == b"data":
            start = offset + 8
            pcm = raw[start : start + chunk_size]
            break
        offset += 8 + chunk_size
        if offset > 1_048_576:  # bound header scan (sandbox)
            break
    if pcm is None:
        raise AudioValidationError("invalid_wav_header")
    return channels, sample_rate, bits_per_sample, pcm


def validate_audio(
    *,
    audio_base64: str,
    mime_type: str,
    sample_rate_hz: int,
    channels: int,
    bits_per_sample: int,
    duration_ms: int | None,
    max_audio_bytes: int,
    max_duration_ms: int,
) -> ValidatedAudio:
    """Validate and normalize an inline audio payload to raw PCM bytes."""
    if sample_rate_hz not in ALLOWED_SAMPLE_RATES:
        raise AudioValidationError("unsupported_sample_rate")
    if channels not in (1, 2):
        raise AudioValidationError("unsupported_channels")

    raw = _decode_b64(audio_base64, max_audio_bytes)

    if mime_type in WAV_MIMES:
        wav_channels, wav_rate, wav_bits, pcm = _parse_wav_header(raw)
        if wav_channels != channels or wav_rate != sample_rate_hz or wav_bits != bits_per_sample:
            raise AudioValidationError("wav_parameter_mismatch")
    elif mime_type in PCM_MIMES:
        pcm = raw
    else:  # pragma: no cover - pydantic Literal guards this
        raise AudioValidationError("unsupported_mime_type")

    bytes_per_sample = bits_per_sample // 8
    frame_size = channels * bytes_per_sample
    if frame_size <= 0 or len(pcm) % frame_size != 0:
        raise AudioValidationError("invalid_pcm_length")
    frames = len(pcm) // frame_size
    computed_ms = int(frames * 1_000 / sample_rate_hz)
    if computed_ms <= 0:
        raise AudioValidationError("invalid_audio_empty")
    if computed_ms > max_duration_ms:
        raise AudioValidationError("audio_too_long")
    if duration_ms is not None and abs(duration_ms - computed_ms) > 100:
        raise AudioValidationError("duration_mismatch")

    return ValidatedAudio(
        raw_pcm=pcm,
        mime_type=mime_type,
        sample_rate_hz=sample_rate_hz,
        channels=channels,
        bits_per_sample=bits_per_sample,
        duration_ms=computed_ms,
    )
