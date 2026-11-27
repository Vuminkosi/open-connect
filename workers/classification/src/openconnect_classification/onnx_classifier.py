"""Offline ONNX classification for explicitly pinned, operator-supplied bundles."""

from __future__ import annotations

import hashlib
import json
import re
from dataclasses import dataclass
from functools import lru_cache
from pathlib import Path
from typing import TYPE_CHECKING, Literal

import numpy as np
from pydantic import BaseModel, ConfigDict, Field

from openconnect_classification.cloud_consumption import _unique_object
from openconnect_classification.label_sets import get_label_set
from openconnect_classification.manifest import PRE_APPROVED_LICENSES

if TYPE_CHECKING:
    import onnxruntime as ort
    from tokenizers import Tokenizer

_DIGEST = re.compile(r"^[0-9a-f]{64}$")


class BundleContract(BaseModel):
    model_config = ConfigDict(extra="forbid", strict=True)

    schemaVersion: Literal["openconnect.classification.onnx/v1"]
    modelId: str = Field(min_length=1, max_length=256)
    revision: str = Field(min_length=1, max_length=128)
    license: str = Field(min_length=1, max_length=128)
    labelSetVersion: Literal["v1", "v2"]
    labels: list[str] = Field(min_length=1, max_length=6)
    activation: Literal["sigmoid", "softmax"]
    maxTokens: int = Field(ge=1, le=4096)
    outputName: str = Field(min_length=1, max_length=128)
    modelSha256: str = Field(pattern=r"^[0-9a-f]{64}$")
    tokenizerSha256: str = Field(pattern=r"^[0-9a-f]{64}$")


class BundleUnavailableError(Exception):
    pass


class TokenLimitError(Exception):
    pass


def _read_file(root: Path, name: str, maximum: int) -> bytes:
    path = root / name
    if path.is_symlink() or not path.is_file():
        raise BundleUnavailableError("bundle file unavailable")
    with path.open("rb") as source:
        data = source.read(maximum + 1)
    if not data or len(data) > maximum:
        raise BundleUnavailableError("bundle file size invalid")
    return data


@dataclass(frozen=True)
class OnnxClassifier:
    contract: BundleContract
    bundle_digest: str
    tokenizer: Tokenizer
    session: ort.InferenceSession
    inputs: tuple[str, ...]

    def scores(self, text: str) -> dict[str, float]:
        encoding = self.tokenizer.encode(text, add_special_tokens=True)
        if not encoding.ids or len(encoding.ids) > self.contract.maxTokens:
            raise TokenLimitError("input exceeds model token limit")
        values = {"input_ids": encoding.ids, "attention_mask": encoding.attention_mask, "token_type_ids": encoding.type_ids}
        feeds = {name: np.asarray([values[name]], dtype=np.int64) for name in self.inputs}
        logits = np.asarray(self.session.run([self.contract.outputName], feeds)[0])
        if logits.shape != (1, len(self.contract.labels)) or not np.issubdtype(logits.dtype, np.floating) or not np.isfinite(logits).all():
            raise BundleUnavailableError("model output is invalid")
        values_array = logits[0].astype(np.float64)
        if self.contract.activation == "softmax":
            exp = np.exp(values_array - np.max(values_array))
            scores = exp / np.sum(exp)
        else:
            scores = 1.0 / (1.0 + np.exp(-np.clip(values_array, -80, 80)))
        return {label: float(score) for label, score in zip(self.contract.labels, scores, strict=True)}


@lru_cache(maxsize=1)
def load_bundle(root: str, manifest_sha256: str, license_accepted: bool) -> OnnxClassifier:
    """Load verified bytes only; this method never provisions or downloads assets."""
    if not _DIGEST.fullmatch(manifest_sha256):
        raise BundleUnavailableError("bundle digest is not configured")
    try:
        directory = Path(root)
        if not directory.is_absolute() or not directory.is_dir() or directory.is_symlink():
            raise BundleUnavailableError("bundle directory unavailable")
        contract_bytes = _read_file(directory, "classification.onnx.json", 65536)
        if hashlib.sha256(contract_bytes).hexdigest() != manifest_sha256:
            raise BundleUnavailableError("bundle digest mismatch")
        contract = BundleContract.model_validate(json.loads(contract_bytes, object_pairs_hook=_unique_object))
        if contract.license.lower() not in PRE_APPROVED_LICENSES and not license_accepted:
            raise BundleUnavailableError("model license not accepted")
        expected = get_label_set(contract.labelSetVersion)
        if len(contract.labels) != len(expected) or set(contract.labels) != set(expected):
            raise BundleUnavailableError("label mapping is incomplete")
        model = _read_file(directory, "model.onnx", 536870912)
        tokenizer = _read_file(directory, "tokenizer.json", 8388608)
        if hashlib.sha256(model).hexdigest() != contract.modelSha256 or hashlib.sha256(tokenizer).hexdigest() != contract.tokenizerSha256:
            raise BundleUnavailableError("bundle asset digest mismatch")
        # The offline fake engine does not need native model runtime dependencies.
        import onnxruntime as ort
        from tokenizers import Tokenizer

        tokenizer_instance = Tokenizer.from_str(tokenizer.decode("utf-8"))
        tokenizer_instance.no_truncation()
        options = ort.SessionOptions()
        options.intra_op_num_threads = 1
        options.inter_op_num_threads = 1
        session = ort.InferenceSession(model, sess_options=options, providers=["CPUExecutionProvider"])
        inputs = session.get_inputs()
        input_names = tuple(item.name for item in inputs)
        if ("input_ids" not in input_names or len(input_names) > 3 or
                any(item.name not in {"input_ids", "attention_mask", "token_type_ids"} or item.type != "tensor(int64)" or len(item.shape) != 2 for item in inputs) or
                contract.outputName not in {item.name for item in session.get_outputs()}):
            raise BundleUnavailableError("model tensor contract is incompatible")
        classifier = OnnxClassifier(contract, "sha256:" + manifest_sha256, tokenizer_instance, session, input_names)
        classifier.scores("readiness probe")
        return classifier
    except BundleUnavailableError:
        raise
    except Exception as exc:
        raise BundleUnavailableError("classification bundle unavailable") from exc
