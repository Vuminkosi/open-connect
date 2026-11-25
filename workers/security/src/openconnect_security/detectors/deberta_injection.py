"""CPU-first DeBERTa prompt-injection detector adapter with strict lazy imports."""

from __future__ import annotations

import math
from collections.abc import Callable
from pathlib import Path
from typing import Literal

from openconnect_security.detectors.base import DetectorLifecycleState, DetectorStatusSnapshot
from openconnect_security.runtime_cache import (
    AssetFetcher,
    RuntimeModelCacheManager,
    RuntimeModelManifest,
)
from openconnect_security.schemas import InjectionFinding

# Backend returns (raw_label, injection_probability_in_0_1).
DebertaBackend = Callable[[str], tuple[str, float]]


class DebertaInjectionDetector:
    """Lazy-loading DeBERTa prompt-injection classifier backed by verified local cache."""

    detector_id = "deberta-injection"
    modalities = ("prompt",)

    def __init__(
        self,
        *,
        manifest: RuntimeModelManifest | None = None,
        threshold: float = 0.85,
        uncertain_floor: float = 0.55,
        backend: DebertaBackend | None = None,
    ) -> None:
        if not (0.0 < uncertain_floor <= threshold <= 1.0):
            raise ValueError("invalid_deberta_thresholds")
        self._manifest = manifest
        self._threshold = threshold
        self._uncertain_floor = uncertain_floor
        self._backend = backend
        self._state: DetectorLifecycleState = "disabled"
        self._reason_code: str | None = "detector_disabled"
        self._version: str | None = None
        self._revision_dir: Path | None = None

    def status(self) -> DetectorStatusSnapshot:
        return DetectorStatusSnapshot(
            detector_id=self.detector_id,
            state=self._state,
            version=self._version,
            model_id=self._manifest.modelId if self._manifest else None,
            revision=self._manifest.immutableRevision if self._manifest else None,
            reason_code=self._reason_code,
        )

    def is_ready(self) -> bool:
        return self._state == "ready" and self._backend is not None

    def initialize(
        self,
        cache_manager: RuntimeModelCacheManager,
        *,
        enabled: bool,
        provisioning: str = "first-start-if-missing",
        offline: bool = False,
        license_accepted: bool = False,
        asset_fetcher: AssetFetcher | None = None,
    ) -> DetectorStatusSnapshot:
        if not enabled:
            self._state = "disabled"
            self._reason_code = "detector_disabled"
            self._version = None
            return self.status()

        if self._backend is not None:
            self._state = "ready"
            self._reason_code = None
            self._version = self._manifest.immutableRevision if self._manifest else "injected"
            return self.status()

        self._state = "provisioning"
        result = cache_manager.provision_or_verify(
            self._manifest,
            enabled=True,
            provisioning=provisioning,
            offline=offline,
            license_accepted=license_accepted,
            asset_fetcher=asset_fetcher,
        )
        if not result.ready or result.revision_dir is None:
            self._state = "unavailable"
            self._reason_code = result.reason or "model_unavailable"
            self._version = None
            return self.status()

        assets_dir = result.revision_dir / "assets"
        try:
            # Strict lazy import: never imported unless enabled and cache is verified.
            from transformers import (  # type: ignore[import-not-found]
                AutoModelForSequenceClassification,
                AutoTokenizer,
                pipeline,
            )

            tokenizer = AutoTokenizer.from_pretrained(
                str(assets_dir), local_files_only=True, trust_remote_code=False
            )
            model = AutoModelForSequenceClassification.from_pretrained(
                str(assets_dir), local_files_only=True, trust_remote_code=False
            )
            clf = pipeline(
                "text-classification",
                model=model,
                tokenizer=tokenizer,
                device=-1,
                truncation=True,
                max_length=512,
            )

            def _run(prompt: str) -> tuple[str, float]:
                out = clf(prompt)
                first = out[0] if isinstance(out, list) and out else out
                raw_label = str(first.get("label", "")).upper()
                raw_score = float(first.get("score", 0.0))
                if "INJECTION" in raw_label or raw_label == "LABEL_1":
                    return ("injection", raw_score)
                return ("benign", 1.0 - raw_score)

            self._backend = _run
            self._revision_dir = result.revision_dir
            self._version = result.revision or "verified"
            self._state = "ready"
            self._reason_code = None
        except ImportError:
            self._state = "unavailable"
            self._reason_code = "runtime_dependency_missing"
            self._version = None
        except Exception:  # noqa: BLE001 - Fail closed on any model load failure.
            self._state = "unavailable"
            self._reason_code = "model_load_failed"
            self._version = None
        return self.status()

    def detect(self, prompt: str) -> list[InjectionFinding]:
        if not self.is_ready() or self._backend is None:
            raise RuntimeError("detector_unavailable:deberta-injection")
        _raw_label, score = self._backend(prompt)
        if not isinstance(score, (int, float)) or not math.isfinite(float(score)):
            raise ValueError("non_finite_injection_score")
        prob = float(score)
        if not (0.0 <= prob <= 1.0):
            raise ValueError("out_of_range_injection_score")

        label: Literal["benign", "injection", "uncertain"]
        if prob >= self._threshold:
            label = "injection"
        elif prob >= self._uncertain_floor:
            label = "uncertain"
        else:
            label = "benign"

        return [
            InjectionFinding(
                detectorId="deberta-injection",
                label=label,
                score=prob,
                threshold=self._threshold,
                modelRevision=self._version or "unknown",
            )
        ]
