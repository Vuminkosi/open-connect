"""Offline paired CLIP projection adapter; host authorization and scanning are external."""

from __future__ import annotations

import hashlib
import io
import json
import threading
import warnings
from dataclasses import dataclass

import numpy as np
import onnx
import onnxruntime as ort
from PIL import Image, ImageOps, UnidentifiedImageError
from tokenizers import Tokenizer

from openconnect_vision.classifier import MAX_ASPECT_RATIO, MAX_INPUT_BYTES, MAX_PIXELS, _reject_external_tensors
from openconnect_vision.profiles import CLIP_MODEL_ID, CLIP_REVISION, clip_initializer
from openconnect_worker_common.models.resource_admission import ResourceBudget, ResourceRequest
from openconnect_worker_common.models.runtime_cache import RuntimeModelCacheManager, RuntimeModelManifest

DIMENSION = 512
MAX_TOKENS = 77
MAX_TEXT_CHARS = 8192


@dataclass(frozen=True)
class ClipEmbedding:
    vector: tuple[float, ...]
    model_id: str
    fingerprint: str
    space_id: str
    dimension: int = DIMENSION


def _clip_image(content: bytes) -> np.ndarray:
    if not isinstance(content, bytes) or not content or len(content) > MAX_INPUT_BYTES:
        raise ValueError("invalid_image")
    if not (content.startswith(b"\x89PNG\r\n\x1a\n") or content.startswith(b"\xff\xd8\xff")):
        raise ValueError("unsupported_format")
    try:
        with warnings.catch_warnings():
            warnings.simplefilter("error", Image.DecompressionBombWarning)
            with Image.open(io.BytesIO(content)) as original:
                width, height = original.size
                if (original.format not in {"PNG", "JPEG"} or width <= 0 or height <= 0
                        or width * height > MAX_PIXELS or max(width, height) > MAX_ASPECT_RATIO * min(width, height)
                        or getattr(original, "n_frames", 1) != 1):
                    raise ValueError("image_limit_exceeded")
                original.load()
                image = ImageOps.exif_transpose(original).convert("RGB")
                ratio = 224 / min(image.size)
                image = image.resize((max(224, int(image.width * ratio)), max(224, int(image.height * ratio))),
                                     Image.Resampling.BICUBIC)
                left, top = (image.width - 224) // 2, (image.height - 224) // 2
                image = image.crop((left, top, left + 224, top + 224))
                pixels = np.asarray(image, dtype=np.float32) / np.float32(255)
                pixels = (pixels - np.array([0.48145466, 0.4578275, 0.40821073], dtype=np.float32)) / np.array(
                    [0.26862954, 0.26130258, 0.27577711], dtype=np.float32)
                return np.transpose(pixels, (2, 0, 1))[None, :, :, :].copy()
    except (UnidentifiedImageError, OSError, Image.DecompressionBombError,
            Image.DecompressionBombWarning) as exc:
        raise ValueError("invalid_image") from exc


class PairedClipEncoder:
    """Load only the reviewed pinned pair; never fetch model files at request time."""

    def __init__(self, cache: RuntimeModelCacheManager, manifest: RuntimeModelManifest, *,
                 artifact_license_reviewed: bool = False, commercial_use: bool = False):
        expected = clip_initializer(ResourceRequest(memoryMb=1, cpuMillicores=1),
            ResourceBudget(memoryMbAvailable=1, vramMbAvailable=0, cpuMillicoresAvailable=1),
            allowed_hosts=["huggingface.co"], timeout_seconds=1, disk_reserve_bytes=0,
            artifact_license_reviewed=artifact_license_reviewed).manifest
        actual_assets = {(a.relativePath, a.sizeBytes, a.sha256.lower(), a.kind) for a in manifest.assets}
        expected_assets = {(a.relativePath, a.sizeBytes, a.sha256.lower(), a.kind) for a in expected.assets}
        if (manifest.workerKind != "vision" or manifest.modelId != CLIP_MODEL_ID
                or manifest.immutableRevision != CLIP_REVISION or manifest.license != expected.license
                or len(manifest.assets) != len(expected.assets) or actual_assets != expected_assets):
            raise ValueError("invalid_profile")
        self._cache = cache
        self._manifest = manifest.model_copy(deep=True)
        self._commercial_use = commercial_use
        result = self._verify()
        assert result.revision_dir is not None and result.fingerprint is not None
        self._fingerprint = result.fingerprint
        root = result.revision_dir / "assets"
        self._tokenizer = Tokenizer.from_file(str(root / "tokenizer.json"))
        self._tokenizer.no_truncation()
        self._tokenizer.no_padding()
        options = ort.SessionOptions()
        options.intra_op_num_threads = 1
        options.inter_op_num_threads = 1
        sessions = []
        for name in ("vision_model_quantized.onnx", "text_model_quantized.onnx"):
            path = root / "onnx" / name
            _reject_external_tensors(onnx.load_model(str(path), load_external_data=False))
            sessions.append(ort.InferenceSession(str(path), sess_options=options, providers=["CPUExecutionProvider"]))
        self._vision, self._text = sessions
        if ({item.name for item in self._vision.get_inputs()} != {"pixel_values"}
                or "image_embeds" not in {item.name for item in self._vision.get_outputs()}):
            raise ValueError("unsupported_model_inputs")
        self._text_inputs = {item.name for item in self._text.get_inputs()}
        if ("input_ids" not in self._text_inputs or self._text_inputs - {"input_ids", "attention_mask"}
                or "text_embeds" not in {item.name for item in self._text.get_outputs()}):
            raise ValueError("unsupported_model_inputs")
        self._lock = threading.Lock()
        self._space = hashlib.sha256(json.dumps({"fingerprint": self._fingerprint, "dimension": DIMENSION,
            "imagePreprocessing": "clip-bicubic224-rgb-exif-v1", "textTokens": MAX_TOKENS,
            "normalization": "l2-v1"}, sort_keys=True, separators=(",", ":")).encode()).hexdigest()

    def _verify(self):
        result = self._cache.verify_for_request(self._manifest, license_accepted=True,
                                                commercial_use=self._commercial_use)
        if (not result.ready or result.revision_dir is None or result.fingerprint is None
                or (hasattr(self, "_fingerprint") and result.fingerprint != self._fingerprint)):
            raise RuntimeError("model_unavailable")
        return result

    def _result(self, values) -> ClipEmbedding:
        vector = np.asarray(values)
        if vector.shape != (1, DIMENSION) or not np.isfinite(vector).all():
            raise RuntimeError("model_output_invalid")
        vector = vector[0].astype(np.float64)
        norm = float(np.linalg.norm(vector))
        if not np.isfinite(norm) or norm <= 1e-12:
            raise RuntimeError("model_output_invalid")
        return ClipEmbedding(tuple(float(value) for value in vector / norm), CLIP_MODEL_ID,
                             self._fingerprint, self._space)

    def embed_image(self, content: bytes) -> ClipEmbedding:
        self._verify()
        pixels = _clip_image(content)
        with self._lock:
            values = self._vision.run(["image_embeds"], {"pixel_values": pixels})[0]
        return self._result(values)

    def embed_text(self, text: str) -> ClipEmbedding:
        if not isinstance(text, str) or not text.strip() or len(text) > MAX_TEXT_CHARS:
            raise ValueError("invalid_text")
        self._verify()
        with self._lock:
            encoded = self._tokenizer.encode(text)
            if not 1 <= len(encoded.ids) <= MAX_TOKENS:
                raise ValueError("text_token_limit_exceeded")
            feeds = {"input_ids": np.asarray([encoded.ids], dtype=np.int64)}
            if "attention_mask" in self._text_inputs:
                feeds["attention_mask"] = np.asarray([encoded.attention_mask], dtype=np.int64)
            values = self._text.run(["text_embeds"], feeds)[0]
        return self._result(values)
