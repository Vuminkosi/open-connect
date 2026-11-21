"""Offline CPU cross-encoder scoring of candidates authorized by the host."""

from __future__ import annotations

import math
import threading
from dataclasses import dataclass

import numpy as np
import onnx
import onnxruntime as ort
from tokenizers import Tokenizer

from openconnect_knowledge.embeddings import _reject_external_tensors
from openconnect_worker_common.models.runtime_cache import RuntimeModelCacheManager, RuntimeModelManifest

MAX_CANDIDATES = 32
MAX_TOTAL_CHARS = 65536


@dataclass(frozen=True)
class Candidate:
    id: str
    text: str


@dataclass(frozen=True)
class RankedCandidate:
    id: str
    score: float


@dataclass(frozen=True)
class Ranking:
    candidates: tuple[RankedCandidate, ...]
    model_id: str
    fingerprint: str
    score_type: str = "raw_logit"


def _valid_identifier(value: object) -> bool:
    if not isinstance(value, str) or not value.strip() or len(value) > 256:
        return False
    try:
        value.encode("utf-8")
    except UnicodeEncodeError:
        return False
    return all(ord(character) >= 32 and ord(character) != 127 for character in value)


class OnnxReranker:
    """Scores bounded query/passage pairs; host must filter ACLs beforehand."""

    def __init__(self, cache: RuntimeModelCacheManager, manifest: RuntimeModelManifest, *,
                 model_asset: str, tokenizer_asset: str, max_tokens: int = 256,
                 pad_token: str = "[PAD]", license_accepted: bool = False,
                 commercial_use: bool = False):
        if (manifest.workerKind != "knowledge" or not _valid_identifier(manifest.modelId)
                or isinstance(max_tokens, bool) or not isinstance(max_tokens, int) or not 2 <= max_tokens <= 512):
            raise ValueError("invalid_profile")
        assets = {asset.relativePath for asset in manifest.assets}
        if model_asset not in assets or tokenizer_asset not in assets:
            raise ValueError("profile_asset_missing")
        self._cache = cache
        self._manifest = manifest.model_copy(deep=True)
        self._license_accepted = license_accepted
        self._commercial_use = commercial_use
        self._max_tokens = max_tokens
        verified = self._verify()
        assert verified.revision_dir is not None and verified.fingerprint is not None
        self._fingerprint = verified.fingerprint
        root = verified.revision_dir / "assets"
        self._tokenizer = Tokenizer.from_file(str(root / tokenizer_asset))
        self._pad_token = pad_token
        self._pad_id = self._tokenizer.token_to_id(pad_token)
        if self._pad_id is None:
            raise ValueError("padding_token_missing")
        model_path = root / model_asset
        _reject_external_tensors(onnx.load_model(str(model_path), load_external_data=False))
        options = ort.SessionOptions()
        options.intra_op_num_threads = options.inter_op_num_threads = 1
        self._session = ort.InferenceSession(str(model_path), sess_options=options, providers=["CPUExecutionProvider"])
        self._inputs = {item.name for item in self._session.get_inputs()}
        if not {"input_ids", "attention_mask"}.issubset(self._inputs) or self._inputs - {
                "input_ids", "attention_mask", "token_type_ids"}:
            raise ValueError("unsupported_model_inputs")
        self._lock = threading.Lock()

    def _verify(self):
        result = self._cache.verify_for_request(self._manifest, license_accepted=self._license_accepted,
                                                commercial_use=self._commercial_use)
        if not result.ready or result.revision_dir is None:
            raise RuntimeError("model_unavailable")
        return result

    def rank(self, query: str, candidates: list[Candidate]) -> Ranking:
        if (not isinstance(query, str) or not query.strip() or len(query) > 8192
                or not isinstance(candidates, list) or not 1 <= len(candidates) <= MAX_CANDIDATES
                or any(not isinstance(item, Candidate) or not _valid_identifier(item.id)
                       or not isinstance(item.text, str)
                       or not item.text.strip() or len(item.text) > 8192 for item in candidates)
                or sum(len(item.text) for item in candidates) + len(query) > MAX_TOTAL_CHARS
                or len({item.id for item in candidates}) != len(candidates)):
            raise ValueError("invalid_input")
        self._verify()
        with self._lock:
            self._tokenizer.no_padding()
            self._tokenizer.no_truncation()
            pairs = [(query, item.text) for item in candidates]
            encoded = self._tokenizer.encode_batch(pairs)
            if any(len(item.ids) > self._max_tokens for item in encoded):
                raise ValueError("token_limit_exceeded")
            self._tokenizer.enable_padding(pad_id=self._pad_id, pad_token=self._pad_token)
            encoded = self._tokenizer.encode_batch(pairs)
            feeds = {"input_ids": np.asarray([item.ids for item in encoded], dtype=np.int64),
                     "attention_mask": np.asarray([item.attention_mask for item in encoded], dtype=np.int64)}
            if "token_type_ids" in self._inputs:
                feeds["token_type_ids"] = np.asarray([item.type_ids for item in encoded], dtype=np.int64)
            output = self._session.run(None, feeds)
        values = np.asarray(output[0])
        if values.shape not in {(len(candidates),), (len(candidates), 1)} or not np.isfinite(values).all():
            raise RuntimeError("model_output_invalid")
        scores = [float(value) for value in values.reshape(-1)]
        if not all(math.isfinite(score) for score in scores):
            raise RuntimeError("model_output_invalid")
        order = sorted(range(len(candidates)), key=lambda i: (-scores[i], i))
        return Ranking(tuple(RankedCandidate(candidates[i].id, scores[i]) for i in order),
                       self._manifest.modelId, self._fingerprint)
