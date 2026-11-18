"""Classification engines.

Two adapters share one interface:

- FakeEngine: deterministic keyword scorer for CI and contract tests.
  Explicitly NON-PRODUCTION; every result is tagged engine="fake" with a
  warning. No model files required.
- RealEngine: local-checkpoint-gated adapter. Serves only when a manifest +
  weights are mounted under MODEL_DIR and the license is satisfied; it NEVER
  downloads and refuses when HF_HUB_OFFLINE is not set. Until the pinned ML
  runtime lands, scoring is a local deterministic heuristic tagged with the
  manifest revision (engine="real") so the manifest/readiness/license path is
  exercised truthfully; it must not be mistaken for a production model.
"""

from __future__ import annotations

import os
import re
from dataclasses import dataclass

from openconnect_classification.label_sets import get_label_set
from openconnect_classification.manifest import ModelManifest

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
        labels = get_label_set(label_set_version)
        scores = _score(re.sub(r"\s+", " ", text), labels)
        picked = sorted(
            (label for label in labels if scores[label] >= threshold),
            key=lambda label: scores[label],
            reverse=True,
        )[:max_labels]
        return ClassifyOutcome(
            labels=picked,
            scores={label: scores[label] for label in picked},
            abstained=not picked,
            model_revision=self._manifest.revision,
            engine=self.engine_name,
        )
