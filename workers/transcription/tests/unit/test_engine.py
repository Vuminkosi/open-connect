"""Unit tests for WAV/PCM validation and the fake STT engine."""

import base64
import struct

import pytest

from openconnect_transcription.engine import FakeTranscriptionEngine, advertised_capabilities
from openconnect_transcription.engine.audio import AudioValidationError, validate_audio

engine = FakeTranscriptionEngine()


def make_wav(
    *,
    sample_rate: int = 16_000,
    channels: int = 1,
    bits: int = 16,
    frames: int = 1600,
    codec: int = 1,
) -> bytes:
    # Build minimal PCM payload sized exactly frames * frame_bytes.
    frame_bytes = channels * (bits // 8)
    pcm = (b"\x01\x02" * (frames * frame_bytes // 2 + 1))[: frames * frame_bytes]
    header = struct.pack(
        "<4sI4s4sIHHIIHH4sI",
        b"RIFF",
        36 + len(pcm),
        b"WAVE",
        b"fmt ",
        16,
        codec,
        channels,
        sample_rate,
        sample_rate * frame_bytes,
        frame_bytes,
        bits,
        b"data",
        len(pcm),
    )
    return header + pcm


def b64(raw: bytes) -> str:
    return base64.b64encode(raw).decode("ascii")


def base_kwargs(**overrides):
    params = {
        "audio_base64": b64(make_wav()),
        "mime_type": "audio/wav",
        "sample_rate_hz": 16_000,
        "channels": 1,
        "bits_per_sample": 16,
        "duration_ms": 100,
        "max_audio_bytes": 5_242_880,
        "max_duration_ms": 60_000,
    }
    params.update(overrides)
    return params


def test_valid_wav_computes_duration() -> None:
    audio = validate_audio(**base_kwargs())
    assert audio.duration_ms == 100
    assert len(audio.raw_pcm) == 3_200


def test_wav_magic_rejected() -> None:
    with pytest.raises(AudioValidationError):
        validate_audio(**base_kwargs(audio_base64=b64(b"NOTAWAVE" + b"\x00" * 64)))


def test_wav_parameter_mismatch_rejected() -> None:
    with pytest.raises(AudioValidationError):
        validate_audio(**base_kwargs(sample_rate_hz=8_000))


def test_non_pcm_codec_rejected() -> None:
    with pytest.raises(AudioValidationError):
        validate_audio(**base_kwargs(audio_base64=b64(make_wav(codec=3))))


def test_duration_mismatch_rejected() -> None:
    with pytest.raises(AudioValidationError):
        validate_audio(**base_kwargs(duration_ms=5_000))


def test_unsupported_sample_rate_rejected() -> None:
    with pytest.raises(AudioValidationError):
        validate_audio(
            **base_kwargs(
                audio_base64=b64(make_wav(sample_rate=11_025)),
                sample_rate_hz=11_025,
            )
        )


def test_bad_base64_rejected() -> None:
    with pytest.raises(AudioValidationError):
        validate_audio(**base_kwargs(audio_base64="!!!not-base64!!!"))


def test_oversized_audio_rejected() -> None:
    big = base64.b64encode(b"\x00" * 64).decode("ascii")
    with pytest.raises(AudioValidationError):
        validate_audio(**base_kwargs(audio_base64=big, max_audio_bytes=16))


def test_too_long_audio_rejected() -> None:
    wav = make_wav(frames=16_000 * 61)  # 61s at 16kHz mono 16-bit
    with pytest.raises(AudioValidationError):
        validate_audio(**base_kwargs(audio_base64=b64(wav), duration_ms=None))


def test_raw_pcm_mime_accepted() -> None:
    pcm = b"\x01\x02" * 1_600  # 100ms @16kHz mono 16-bit
    audio = validate_audio(**base_kwargs(audio_base64=b64(pcm), mime_type="audio/L16"))
    assert audio.duration_ms == 100


def test_engine_final_transcript_with_revision() -> None:
    audio = validate_audio(**base_kwargs())
    first = engine.transcribe(audio, "en")
    second = engine.transcribe(audio, "en")
    assert first == second
    assert first.revision == 1
    assert first.is_final is True
    assert first.duration_ms == 100
    assert first.segments == [(0, 100, first.transcript)]
    assert engine.supports_partials is False


def test_capability_discovery() -> None:
    assert advertised_capabilities(backend="local-fake") == [
        "transcribe-final",
        "transcribe-recorded",
    ]
    assert "transcribe-partials" not in advertised_capabilities(backend="local-model")
