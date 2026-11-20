"""Bounded offline image classification over a verified model cache."""

from __future__ import annotations

import io
import math
import threading
import warnings
from dataclasses import dataclass

import numpy as np
import onnx
import onnxruntime as ort
from google.protobuf.message import Message
from PIL import Image, ImageOps, UnidentifiedImageError

from openconnect_worker_common.models.runtime_cache import RuntimeModelCacheManager, RuntimeModelManifest

MAX_INPUT_BYTES = 10 * 1024 * 1024
MAX_PIXELS = 16_000_000
MAX_ASPECT_RATIO = 12


def _reject_external_tensors(message: Message) -> None:
    if isinstance(message, onnx.TensorProto) and (
        message.data_location == onnx.TensorProto.EXTERNAL or message.external_data
    ):
        raise ValueError("external_tensor_unsupported")
    for field, value in message.ListFields():
        if field.message_type is not None:
            if field.is_repeated:
                for child in value:
                    _reject_external_tensors(child)
            else:
                _reject_external_tensors(value)


@dataclass(frozen=True)
class Classification:
    label: str
    class_index: int
    score: float


@dataclass(frozen=True)
class ImageClassification:
    model_id: str
    model_fingerprint: str
    taxonomy_version: str
    preprocessing_version: str
    width: int
    height: int
    classifications: tuple[Classification, ...]


def _prepare_image(content: bytes) -> tuple[np.ndarray, int, int]:
    """Decode bounded single-frame PNG/JPEG and apply ImageNet center-crop preprocessing."""
    if not content or len(content) > MAX_INPUT_BYTES:
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
                width, height = image.size
                ratio = 256 / min(width, height)
                image = image.resize((round(width * ratio), round(height * ratio)), Image.Resampling.BILINEAR)
                left, top = image.width // 2 - 112, image.height // 2 - 112
                image = image.crop((left, top, left + 224, top + 224))
                pixels = np.asarray(image, dtype=np.float32) / np.float32(255)
                pixels = (pixels - np.array([.485, .456, .406], dtype=np.float32)) / np.array(
                    [.229, .224, .225], dtype=np.float32)
                return np.transpose(pixels, (2, 0, 1))[None, :, :, :].copy(), width, height
    except (UnidentifiedImageError, OSError, Image.DecompressionBombError,
            Image.DecompressionBombWarning) as exc:
        raise ValueError("invalid_image") from exc


class OnnxImageClassifier:
    """Single-image CPU classifier; scanner, host authorization and sandbox are required externally."""

    def __init__(self, cache: RuntimeModelCacheManager, manifest: RuntimeModelManifest, *,
                 model_asset: str, labels_asset: str, taxonomy_version: str,
                 taxonomy_approved: bool = False, license_accepted: bool = False,
                 commercial_use: bool = False) -> None:
        assets = {asset.relativePath for asset in manifest.assets}
        if (manifest.workerKind != "vision" or model_asset not in assets or labels_asset not in assets
                or not taxonomy_version or len(taxonomy_version) > 128 or taxonomy_approved is not True):
            raise ValueError("invalid_profile")
        self._cache = cache
        self._manifest = manifest.model_copy(deep=True)
        self._license_accepted = license_accepted
        self._commercial_use = commercial_use
        self._taxonomy = taxonomy_version
        result = self._verify()
        assert result.revision_dir is not None and result.fingerprint is not None
        self._fingerprint = result.fingerprint
        root = result.revision_dir / "assets"
        raw_labels = (root / labels_asset).read_bytes()
        if len(raw_labels) > 256 * 1024:
            raise ValueError("invalid_taxonomy")
        self._labels = raw_labels.decode("utf-8").splitlines()
        if len(self._labels) != 1000 or any(not label or len(label) > 256 for label in self._labels):
            raise ValueError("invalid_taxonomy")
        path = root / model_asset
        _reject_external_tensors(onnx.load_model(str(path), load_external_data=False))
        options = ort.SessionOptions()
        options.intra_op_num_threads = 1
        options.inter_op_num_threads = 1
        self._session = ort.InferenceSession(str(path), sess_options=options, providers=["CPUExecutionProvider"])
        inputs = self._session.get_inputs()
        if (len(inputs) != 1 or inputs[0].type != "tensor(float)" or len(inputs[0].shape) != 4
                or inputs[0].shape[1:] != [3, 224, 224]
                or isinstance(inputs[0].shape[0], int) and inputs[0].shape[0] != 1):
            raise ValueError("unsupported_model_input")
        self._input_name = inputs[0].name
        self._lock = threading.Lock()

    def _verify(self):
        result = self._cache.verify_for_request(self._manifest, license_accepted=self._license_accepted,
                                                commercial_use=self._commercial_use)
        if not result.ready or result.revision_dir is None or result.fingerprint is None:
            raise RuntimeError("model_unavailable")
        return result

    def classify(self, content: bytes, *, top_k: int = 5) -> ImageClassification:
        if isinstance(top_k, bool) or not isinstance(top_k, int) or not 1 <= top_k <= 20:
            raise ValueError("invalid_top_k")
        self._verify()
        pixels, width, height = _prepare_image(content)
        with self._lock:
            outputs = self._session.run(None, {self._input_name: pixels})
        scores = np.asarray(outputs[0])
        if scores.shape != (1, 1000) or not np.isfinite(scores).all():
            raise RuntimeError("model_output_invalid")
        logits = scores[0].astype(np.float64)
        weights = np.exp(logits - np.max(logits))
        weights /= np.sum(weights)
        if not np.isfinite(weights).all():
            raise RuntimeError("model_output_invalid")
        indexes = sorted(range(1000), key=lambda i: (-weights[i], i))[:top_k]
        results = tuple(Classification(self._labels[i], i, float(weights[i])) for i in indexes)
        if any(not math.isfinite(item.score) for item in results):
            raise RuntimeError("model_output_invalid")
        return ImageClassification(self._manifest.modelId, self._fingerprint, self._taxonomy,
                                   "imagenet-center224-v1", width, height, results)
