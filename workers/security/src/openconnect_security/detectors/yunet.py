"""CPU-first YuNet face-region detector adapter with strict lazy imports."""

from __future__ import annotations

import math
from collections.abc import Callable
from dataclasses import dataclass
from pathlib import Path

from openconnect_security.detectors.base import DetectorLifecycleState, DetectorStatusSnapshot
from openconnect_security.media import ValidatedImage
from openconnect_security.runtime_cache import (
    AssetFetcher,
    RuntimeModelCacheManager,
    RuntimeModelManifest,
)
from openconnect_security.schemas import ImageRegionFinding


@dataclass(frozen=True)
class FaceBox:
    x: int
    y: int
    width: int
    height: int
    confidence: float


YuNetBackend = Callable[[ValidatedImage], list[FaceBox]]


class YuNetFaceDetector:
    """Lazy-loading OpenCV YuNet face-region detector (privacy masking only)."""

    detector_id = "yunet"
    modalities = ("image",)

    def __init__(
        self,
        *,
        manifest: RuntimeModelManifest | None = None,
        score_threshold: float = 0.6,
        backend: YuNetBackend | None = None,
    ) -> None:
        self._manifest = manifest
        self._score_threshold = score_threshold
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
        if not result.ready or result.revision_dir is None or self._manifest is None:
            self._state = "unavailable"
            self._reason_code = result.reason or "model_unavailable"
            self._version = None
            return self.status()

        onnx_asset = next(
            (a for a in self._manifest.assets if a.relativePath.lower().endswith(".onnx")),
            self._manifest.assets[0],
        )
        onnx_path = result.revision_dir / "assets" / onnx_asset.relativePath
        try:
            # Strict lazy import: never imported unless enabled and cache is verified.
            import cv2  # type: ignore[import-not-found]
            import numpy as np  # type: ignore[import-not-found]

            detector = cv2.FaceDetectorYN.create(
                str(onnx_path),
                "",
                (320, 320),
                score_threshold=self._score_threshold,
                nms_threshold=0.3,
                top_k=100,
            )

            def _run(validated: ValidatedImage) -> list[FaceBox]:
                rgb = np.array(validated.image.convert("RGB"))
                bgr = cv2.cvtColor(rgb, cv2.COLOR_RGB2BGR)
                detector.setInputSize((validated.width, validated.height))
                _, faces = detector.detect(bgr)
                if faces is None:
                    return []
                boxes: list[FaceBox] = []
                for row in faces:
                    x0 = max(0, math.floor(float(row[0])))
                    y0 = max(0, math.floor(float(row[1])))
                    w = math.ceil(float(row[2]))
                    h = math.ceil(float(row[3]))
                    x1 = min(validated.width, x0 + w)
                    y1 = min(validated.height, y0 + h)
                    score = float(row[-1])
                    if x1 > x0 and y1 > y0:
                        boxes.append(
                            FaceBox(
                                x=x0,
                                y=y0,
                                width=x1 - x0,
                                height=y1 - y0,
                                confidence=min(1.0, max(0.0, score)),
                            )
                        )
                return boxes

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

    def detect(self, validated: ValidatedImage) -> list[ImageRegionFinding]:
        if not self.is_ready() or self._backend is None:
            raise RuntimeError("detector_unavailable:yunet")

        boxes = self._backend(validated)
        findings: list[ImageRegionFinding] = []
        for box in boxes:
            if not math.isfinite(box.confidence) or not (0.0 <= box.confidence <= 1.0):
                raise ValueError("invalid_yunet_confidence")
            if box.confidence < self._score_threshold:
                continue
            if (
                box.x < 0
                or box.y < 0
                or box.width < 1
                or box.height < 1
                or box.x + box.width > validated.width
                or box.y + box.height > validated.height
            ):
                raise ValueError("yunet_region_out_of_bounds")
            findings.append(
                ImageRegionFinding(
                    detectorId="yunet",
                    category="FACE",
                    x=box.x,
                    y=box.y,
                    width=box.width,
                    height=box.height,
                    confidence=box.confidence,
                    recommendedAction="redact",
                )
            )
        return findings
