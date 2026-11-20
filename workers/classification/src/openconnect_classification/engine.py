"""Classification engines.

Two adapters share one interface:

- FakeEngine: deterministic keyword scorer for CI and contract tests.
  Explicitly NON-PRODUCTION; every result is tagged engine="fake" with a
  warning. No model files required.
- OnnxEngine: offline inference with a verified, operator-supplied bundle.
- RealEngine: legacy checkpoint fallback that remains unavailable.
"""

from __future__ import annotations

import os
from dataclasses import dataclass

from openconnect_classification.label_sets import get_label_set
from openconnect_classification.manifest import ModelManifest
from openconnect_classification.onnx_classifier import OnnxClassifier, TokenLimitError

FAKE_MODEL_REVISION = "fake-0.1.0"
FAKE_WARNING = "fake-engine-non-production: deterministic stub for CI only"

_KEYWORDS: dict[str, tuple[str, ...]] = {
    "spam": ("buy now", "free money", "click here", "winner", "lottery"),
    "toxic": ("hate", "idiot", "stupid", "kill"),
    "sensitive": ("password", "api_key", "secret", "token"),
    "pii": ("@", "+1-", "ssn"),
    "injection": ("ignore previous", "system prompt", "jailbreak"),
}


@dataclass(frozen=True)
class ClassifyOutcome:
    labels: list[str]
    scores: dict[str, float]
    abstained: bool
    model_revision: str
    engine: str


def _score(text: str, labels: tuple[str, ...]) -> dict[str, float]:
    lowered = text.lower()
    scores: dict[str, float] = {}
    for label in labels:
        if label == "safe":
            continue
        hits = sum(1 for kw in _KEYWORDS.get(label, ()) if kw in lowered)
        scores[label] = min(0.99, 0.55 + 0.15 * hits) if hits else 0.05
    safe_mass = max(0.0, 1.0 - sum(v for v in scores.values() if v > 0.1))
    scores["safe"] = round(
        max(0.5 if not any(v > 0.1 for v in scores.values()) else safe_mass, 0.01), 3
    )
    return {label: round(scores.get(label, 0.01), 3) for label in labels}


class FakeEngine:
    engine_name = "fake"

    def classify(
        self,
        text: str,
        *,
        label_set_version: str,
        threshold: float,
        max_labels: int,
        locale_hint: str | None = None,  # noqa: ARG002 - part of stable interface
    ) -> ClassifyOutcome:
        labels = get_label_set(label_set_version)
        scores = _score(text, labels)
        picked = sorted(
            (label for label in labels if scores[label] >= threshold),
            key=lambda label: scores[label],
            reverse=True,
        )[:max_labels]
        if "safe" in labels and not picked:
            picked = ["safe"]
        return ClassifyOutcome(
            labels=picked,
            scores={label: scores[label] for label in picked},
            abstained=not picked
            or (len(picked) == 1 and picked[0] == "safe" and scores["safe"] < threshold),
            model_revision=FAKE_MODEL_REVISION,
            engine=self.engine_name,
        )


class ModelUnavailableError(RuntimeError):
    pass


class LicenseDeniedError(RuntimeError):
    pass


class InputTokenLimitError(RuntimeError):
    pass


class OnnxEngine:
    """Classifier backed by a verified local ONNX bundle."""

    def __init__(self, classifier: OnnxClassifier) -> None:
        self._classifier = classifier

    def classify(self, text: str, *, label_set_version: str, threshold: float, max_labels: int,
                 locale_hint: str | None = None) -> ClassifyOutcome:  # noqa: ARG002
        if label_set_version != self._classifier.contract.labelSetVersion:
            raise ModelUnavailableError("requested label set is unavailable")
        try:
            scores = self._classifier.scores(text)
        except TokenLimitError as exc:
            raise InputTokenLimitError("input exceeds model token limit") from exc
        except Exception as exc:
            raise ModelUnavailableError("model inference unavailable") from exc
        selected = sorted((label for label in self._classifier.contract.labels if scores[label] >= threshold),
                          key=lambda label: scores[label], reverse=True)[:max_labels]
        return ClassifyOutcome(labels=selected, scores={label: scores[label] for label in selected},
                               abstained=not selected, model_revision=self._classifier.contract.revision, engine="real")


def _offline_enforced() -> bool:
    return os.environ.get("HF_HUB_OFFLINE", "") == "1"


class RealEngine:
    """Local-checkpoint adapter: manifest-gated, offline-only, no downloads."""

    engine_name = "real"

    def __init__(self, manifest: ModelManifest | None, *, ready: bool, reason: str | None) -> None:
        self._manifest = manifest
        self._ready = ready
        self._reason = reason

    def classify(
        self,
        text: str,
        *,
        label_set_version: str,
        threshold: float,
        max_labels: int,
        locale_hint: str | None = None,  # noqa: ARG002 - part of stable interface
    ) -> ClassifyOutcome:
        if not self._ready:
            if self._reason == "license_denied":
                raise LicenseDeniedError("model license not accepted")
            raise ModelUnavailableError("model unavailable")
        if not _offline_enforced():
            raise ModelUnavailableError("HF_HUB_OFFLINE=1 required; refusing network fetch")
        if self._manifest is None:  # pragma: no cover - guarded by readiness
            raise ModelUnavailableError("model unavailable")
        raise ModelUnavailableError("classification inference runtime is unavailable")
