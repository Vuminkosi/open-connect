"""Offline ONNX sentence embedding adapter over a verified model cache."""

from __future__ import annotations

import hashlib
import json
import threading
from dataclasses import dataclass
from itertools import islice
from typing import Literal

import numpy as np
import onnx
import onnxruntime as ort
from google.protobuf.message import Message
from tokenizers import Tokenizer

from openconnect_knowledge.chunking import _identifier
from openconnect_worker_common.models.runtime_cache import (
    RuntimeModelCacheManager,
    RuntimeModelManifest,
)

MAX_BATCH = 32
MAX_TEXT_CHARS = 8192
MAX_TOTAL_CHARS = 65536


def _reject_external_tensors(message: Message) -> None:
    if isinstance(message, onnx.TensorProto) and (
        message.data_location == onnx.TensorProto.EXTERNAL or message.external_data
    ):
        raise ValueError("external_tensor_unsupported")
    for field, value in message.ListFields():
        if field.message_type is None:
            continue
        if field.is_repeated:
            for child in value:
                _reject_external_tensors(child)
        else:
            _reject_external_tensors(value)


@dataclass(frozen=True)
class EmbeddingProfile:
    """Reviewed adapter metadata; changing preprocessing changes the space ID."""

    model_asset: str
    tokenizer_asset: str
    dimension: int
    max_tokens: int
    prefix_style: Literal["none", "e5"] = "none"
    pad_token: str = "[PAD]"


@dataclass(frozen=True, repr=False)
class Embedding:
    vector: tuple[float, ...]
    truncated: bool
    norm: float


@dataclass(frozen=True, repr=False)
class EmbeddingBatch:
    items: tuple[Embedding, ...]
    model_id: str
    fingerprint: str
    dimension: int
    space_id: str


class OnnxEmbeddingEngine:
    """One CPU session, with bounded batches and verified local model assets."""

    def __init__(self, cache: RuntimeModelCacheManager, manifest: RuntimeModelManifest,
                 profile: EmbeddingProfile, *, license_accepted: bool = False,
                 commercial_use: bool = False) -> None:
        if (not isinstance(profile, EmbeddingProfile) or manifest.workerKind != "knowledge"
                or type(profile.dimension) is not int or not 1 <= profile.dimension <= 8192
                or type(profile.max_tokens) is not int or not 2 <= profile.max_tokens <= 512
                or not isinstance(profile.model_asset, str) or not isinstance(profile.tokenizer_asset, str)
                or not isinstance(profile.pad_token, str) or not profile.pad_token
                or not isinstance(profile.prefix_style, str)
                or profile.prefix_style not in {"none", "e5"}):
            raise ValueError("invalid_profile")
        try:
            _identifier(manifest.modelId)
        except ValueError as exc:
            raise ValueError("invalid_profile") from exc
        asset_paths = {asset.relativePath for asset in manifest.assets}
        if profile.model_asset not in asset_paths or profile.tokenizer_asset not in asset_paths:
            raise ValueError("profile_asset_missing")
        self._manifest = manifest.model_copy(deep=True)
        self._cache = cache
        self._license_accepted = license_accepted
        self._commercial_use = commercial_use
        self._profile = profile
        result = self._verify()
        assert result.revision_dir is not None and result.fingerprint is not None
        self._fingerprint = result.fingerprint
        root = result.revision_dir / "assets"
        self._tokenizer = Tokenizer.from_file(str(root / profile.tokenizer_asset))
        self._pad_id = self._tokenizer.token_to_id(profile.pad_token)
        if self._pad_id is None:
            raise ValueError("padding_token_missing")
        model_path = root / profile.model_asset
        _reject_external_tensors(onnx.load_model(str(model_path), load_external_data=False))
        options = ort.SessionOptions()
        options.intra_op_num_threads = 1
        options.inter_op_num_threads = 1
        self._session = ort.InferenceSession(str(model_path), sess_options=options,
                                             providers=["CPUExecutionProvider"])
        inputs = {item.name for item in self._session.get_inputs()}
        if not {"input_ids", "attention_mask"}.issubset(inputs) or inputs - {
            "input_ids", "attention_mask", "token_type_ids"
        }:
            raise ValueError("unsupported_model_inputs")
        self._inputs = inputs
        self._lock = threading.Lock()
        metadata = {"fingerprint": self._fingerprint, "dimension": profile.dimension,
                    "maxTokens": profile.max_tokens, "prefix": profile.prefix_style,
                    "modelAsset": profile.model_asset, "tokenizerAsset": profile.tokenizer_asset,
                    "padToken": profile.pad_token,
                    "pooling": "attention-mean-l2-v1"}
        self._space = hashlib.sha256(json.dumps(metadata, sort_keys=True).encode()).hexdigest()

    def _verify(self):
        result = self._cache.verify_for_request(self._manifest,
                                                license_accepted=self._license_accepted,
                                                commercial_use=self._commercial_use)
        if not result.ready or result.revision_dir is None:
            raise RuntimeError("model_unavailable")
        return result

    def embed(self, texts: list[str], *, input_kind: Literal["query", "passage"]) -> EmbeddingBatch:
        if input_kind not in {"query", "passage"}:
            raise ValueError("invalid_input_kind")
        if not isinstance(texts, (list, tuple)):
            raise ValueError("invalid_input")
        snapshot = tuple(islice(texts, MAX_BATCH + 1))
        if (not snapshot or len(snapshot) > MAX_BATCH or any(not isinstance(text, str) or not text
                or len(text) > MAX_TEXT_CHARS for text in snapshot)
                or sum(len(text) for text in snapshot) > MAX_TOTAL_CHARS):
            raise ValueError("invalid_input")
        self._verify()
        prefix = f"{input_kind}: " if self._profile.prefix_style == "e5" else ""
        inputs = [prefix + text for text in snapshot]
        with self._lock:
            self._tokenizer.no_truncation()
            self._tokenizer.no_padding()
            original_lengths = [len(item.ids) for item in self._tokenizer.encode_batch(inputs)]
            self._tokenizer.enable_truncation(max_length=self._profile.max_tokens)
            self._tokenizer.enable_padding(pad_id=self._pad_id, pad_token=self._profile.pad_token)
            encoded = self._tokenizer.encode_batch(inputs)
            ids = np.asarray([item.ids for item in encoded], dtype=np.int64)
            mask = np.asarray([item.attention_mask for item in encoded], dtype=np.int64)
            feeds = {"input_ids": ids, "attention_mask": mask}
            if "token_type_ids" in self._inputs:
                feeds["token_type_ids"] = np.asarray([item.type_ids for item in encoded],
                                                     dtype=np.int64)
            outputs = self._session.run(None, feeds)
        hidden = np.asarray(outputs[0])
        expected_shape = (len(snapshot), ids.shape[1], self._profile.dimension)
        if hidden.shape != expected_shape or not np.isfinite(hidden).all():
            raise RuntimeError("model_output_invalid")
        mask_float = mask.astype(np.float32)
        denominator = mask_float.sum(axis=1, keepdims=True)
        if np.any(denominator == 0):
            raise RuntimeError("model_output_invalid")
        pooled = (hidden * mask_float[:, :, None]).sum(axis=1) / denominator
        norms = np.linalg.norm(pooled, axis=1, keepdims=True)
        if not np.isfinite(norms).all() or np.any(norms <= 1e-12):
            raise RuntimeError("model_output_invalid")
        vectors = pooled / norms
        items = tuple(Embedding(tuple(float(v) for v in row), length > self._profile.max_tokens,
                                float(np.linalg.norm(row)))
                      for row, length in zip(vectors, original_lengths, strict=True))
        return EmbeddingBatch(items, self._manifest.modelId, self._fingerprint,
                              self._profile.dimension, self._space)
