"""Offline Whisper execution using an explicitly verified model revision."""

from __future__ import annotations

import io
import math
import struct
import threading
from pathlib import Path

from openconnect_transcription.engine import TranscriptResult
from openconnect_transcription.engine.audio import ValidatedAudio
from openconnect_transcription.runtime_cache import RuntimeModelCacheManager, RuntimeModelManifest

_lock = threading.Lock()
_loaded_key = None
_loaded_model = None


def load_model(cfg, device: str):
    loaded = load_verified_model(cfg, device)
    return loaded[0] if loaded is not None else None


def load_verified_model(cfg, device: str):
    """Load a verified local revision; never resolve a model ID over the network."""
    global _loaded_key, _loaded_model
    if cfg.backend != "local-model" or cfg.fake_engine or not cfg.model_id or not cfg.model_revision:
        return None
    try:
        manifest_path = Path(cfg.model_dir) / "model-manifest.json"
        if manifest_path.stat().st_size > 1_048_576:
            return None
        manifest = RuntimeModelManifest.model_validate_json(manifest_path.read_bytes())
        if (manifest.workerKind != "transcription" or manifest.modelId != cfg.model_id or
                manifest.immutableRevision != cfg.model_revision):
            return None
        assets = {asset.relativePath for asset in manifest.assets}
        if not {"model.bin", "config.json", "tokenizer.json"}.issubset(assets):
            return None
        verified = RuntimeModelCacheManager(cfg.model_dir, "transcription").verify_for_request(manifest)
        if not verified.ready or verified.revision_dir is None:
            return None
        model_path = verified.revision_dir / "assets"
        if any(path.is_symlink() or (path.is_file() and path.relative_to(model_path).as_posix() not in assets)
               for path in model_path.rglob("*")):
            return None
        key = (str(model_path.resolve()), verified.fingerprint, device)
        with _lock:
            if key != _loaded_key:
                from faster_whisper import WhisperModel

                model = WhisperModel(str(model_path), device=device, local_files_only=True)
                _loaded_model, _loaded_key = model, key
            return _loaded_model, verified.fingerprint
    except Exception:
        return None


def transcribe(model, audio: ValidatedAudio, locale: str) -> TranscriptResult:
    import av
    import numpy as np

    data = audio.raw_pcm
    align = audio.channels * (audio.bits_per_sample // 8)
    header = b"RIFF" + struct.pack("<I", 36 + len(data)) + b"WAVEfmt "
    header += struct.pack("<IHHIIHH", 16, 1, audio.channels, audio.sample_rate_hz,
                          audio.sample_rate_hz * align, align, audio.bits_per_sample)
    header += b"data" + struct.pack("<I", len(data))
    resampler = av.audio.resampler.AudioResampler(format="s16", layout="mono", rate=16000)
    chunks = []
    with av.open(io.BytesIO(header + data), mode="r") as container:
        for frame in container.decode(audio=0):
            frame.pts = None
            chunks.extend(item.to_ndarray().reshape(-1) for item in resampler.resample(frame))
        chunks.extend(item.to_ndarray().reshape(-1) for item in resampler.resample(None))
    if not chunks:
        raise ValueError("empty audio")
    waveform = np.concatenate(chunks).astype(np.float32) / 32768.0
    segments, _ = model.transcribe(waveform, language=locale.split("-")[0], vad_filter=False)
    output = []
    for segment in segments:
        text = segment.text.strip()
        if not text:
            continue
        if len(output) >= 256 or len(text) > 8192 or not math.isfinite(segment.start) or not math.isfinite(segment.end):
            raise ValueError("invalid transcription output")
        start = max(0, int(segment.start * 1000))
        end = min(audio.duration_ms, max(start + 1, int(segment.end * 1000)))
        if end <= start:
            raise ValueError("invalid transcription timing")
        output.append((start, end, text))
    return TranscriptResult(" ".join(item[2] for item in output), 1, True, locale, audio.duration_ms, output)
