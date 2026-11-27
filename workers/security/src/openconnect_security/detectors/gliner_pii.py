"""CPU-first GLiNER semantic PII detector adapter with strict lazy imports."""

from __future__ import annotations

import math
from collections.abc import Callable, Sequence
from pathlib import Path
from typing import Any

from openconnect_security.detectors.base import DetectorLifecycleState, DetectorStatusSnapshot
from openconnect_security.runtime_cache import (
    AssetFetcher,
    RuntimeModelCacheManager,
    RuntimeModelManifest,
)
from openconnect_security.schemas import EntityCategory, TextSpanFinding

DEFAULT_GLINER_LABELS: tuple[str, ...] = (
    "person",
    "email",
    "phone number",
    "address",
    "date of birth",
    "government id",
    "passport number",
    "bank account",
    "credit card",
    "organization",
)

_LABEL_TO_CATEGORY: dict[str, EntityCategory] = {
    "person": "PERSON",
    "name": "PERSON",
    "full name": "PERSON",
    "email": "EMAIL",
    "email address": "EMAIL",
    "phone": "PHONE",
    "phone number": "PHONE",
    "address": "ADDRESS",
    "street address": "ADDRESS",
    "location": "ADDRESS",
    "date of birth": "DATE_OF_BIRTH",
    "dob": "DATE_OF_BIRTH",
    "government id": "GOVERNMENT_ID",
    "national id": "GOVERNMENT_ID",
    "id number": "GOVERNMENT_ID",
    "passport": "GOVERNMENT_ID",
    "passport number": "GOVERNMENT_ID",
    "tax id": "GOVERNMENT_ID",
    "ssn": "GOVERNMENT_ID",
    "bank account": "ACCOUNT_NUMBER",
    "account number": "ACCOUNT_NUMBER",
    "iban": "ACCOUNT_NUMBER",
    "credit card": "PAYMENT_CARD",
    "payment card": "PAYMENT_CARD",
    "ip address": "IP_ADDRESS",
    "secret": "SECRET",
    "api key": "SECRET",
    "organization": "ORG_SENSITIVE",
}

GlinerBackend = Callable[[str, Sequence[str], float], list[dict[str, Any]]]


def map_gliner_label(raw_label: str) -> EntityCategory:
    return _LABEL_TO_CATEGORY.get(raw_label.strip().lower(), "CUSTOM")


class GlinerPiiDetector:
    """Lazy-loading GLiNER semantic PII detector backed by verified local cache."""

    detector_id = "gliner-pii"
    modalities = ("text",)

    def __init__(
        self,
        *,
        manifest: RuntimeModelManifest | None = None,
        threshold: float = 0.5,
        labels: tuple[str, ...] = DEFAULT_GLINER_LABELS,
        backend: GlinerBackend | None = None,
    ) -> None:
        self._manifest = manifest
        self._threshold = threshold
        self._labels = labels
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
            from gliner import GLiNER  # type: ignore[import-not-found]

            model = GLiNER.from_pretrained(str(assets_dir), local_files_only=True)
            self._backend = lambda text, labels, thr: model.predict_entities(
                text, list(labels), threshold=thr
            )
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

    def detect(self, text: str) -> list[TextSpanFinding]:
        if not self.is_ready() or self._backend is None:
            raise RuntimeError("detector_unavailable:gliner-pii")
        raw_entities = self._backend(text, self._labels, self._threshold)
        text_len = len(text)
        findings: list[TextSpanFinding] = []
        for entity in raw_entities:
            start = entity.get("start")
            end = entity.get("end")
            score = entity.get("score")
            label = str(entity.get("label", ""))
            if not isinstance(start, int) or not isinstance(end, int):
                raise TypeError("invalid_gliner_offsets")
            if start < 0 or end > text_len or start >= end:
                raise ValueError("out_of_bounds_gliner_offsets")
            if not isinstance(score, (int, float)) or not math.isfinite(float(score)):
                raise ValueError("non_finite_gliner_score")
            conf = float(score)
            if not (0.0 <= conf <= 1.0):
                raise ValueError("out_of_range_gliner_score")
            if conf < self._threshold:
                continue
            category = map_gliner_label(label)
            findings.append(
                TextSpanFinding(
                    detectorId="gliner-pii",
                    category=category,
                    start=start,
                    end=end,
                    confidence=conf,
                    recommendedAction="redact" if category == "SECRET" else "mask",
                )
            )
        return sorted(findings, key=lambda f: (f.start, f.end, f.category))
