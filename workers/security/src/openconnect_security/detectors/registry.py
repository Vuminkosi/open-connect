"""Per-detector lifecycle registry and separated bounded inference/malware queues."""

from __future__ import annotations

from openconnect_security.config import Settings
from openconnect_security.detectors.base import BoundedExecutionQueue, DetectorStatusSnapshot
from openconnect_security.detectors.deberta_injection import DebertaInjectionDetector
from openconnect_security.detectors.gliner_pii import GlinerPiiDetector
from openconnect_security.detectors.manifests import load_detector_manifests
from openconnect_security.detectors.paddle_ocr import PaddleOcrDetector
from openconnect_security.detectors.yunet import YuNetFaceDetector
from openconnect_security.media import ValidatedImage
from openconnect_security.runtime_cache import RuntimeModelCacheManager
from openconnect_security.schemas import ImageRegionFinding, InjectionFinding, TextSpanFinding


class DetectorRegistry:
    """Owns model-backed detector adapters, lifecycle states, and bounded queues."""

    def __init__(
        self,
        *,
        gliner: GlinerPiiDetector | None = None,
        deberta: DebertaInjectionDetector | None = None,
        paddle_ocr: PaddleOcrDetector | None = None,
        yunet: YuNetFaceDetector | None = None,
        max_inference_concurrency: int = 2,
        inference_queue_timeout_seconds: float = 10.0,
        max_malware_concurrency: int = 4,
        malware_queue_timeout_seconds: float = 15.0,
    ) -> None:
        manifests = load_detector_manifests()
        self.gliner = gliner or GlinerPiiDetector(manifest=manifests.get("gliner-pii"))
        self.deberta = deberta or DebertaInjectionDetector(
            manifest=manifests.get("deberta-injection")
        )
        self.paddle_ocr = paddle_ocr or PaddleOcrDetector(manifest=manifests.get("paddle-ocr"))
        self.yunet = yunet or YuNetFaceDetector(manifest=manifests.get("yunet"))
        self.inference_queue = BoundedExecutionQueue(
            name="cpu_inference",
            max_concurrent=max_inference_concurrency,
            wait_timeout_seconds=inference_queue_timeout_seconds,
        )
        self.malware_queue = BoundedExecutionQueue(
            name="malware_scan",
            max_concurrent=max_malware_concurrency,
            wait_timeout_seconds=malware_queue_timeout_seconds,
        )

    def initialize_from_settings(self, settings: Settings) -> dict[str, DetectorStatusSnapshot]:
        """Provision/verify ONLY enabled model detectors; disabled ones do zero work."""
        cache = RuntimeModelCacheManager(settings.model_dir, "security")
        return {
            "gliner-pii": self.gliner.initialize(
                cache,
                enabled=settings.gliner_enabled,
                provisioning=settings.provisioning,
                offline=settings.model_offline,
                license_accepted=settings.license_accepted,
            ),
            "deberta-injection": self.deberta.initialize(
                cache,
                enabled=settings.deberta_enabled,
                provisioning=settings.provisioning,
                offline=settings.model_offline,
                license_accepted=settings.license_accepted,
            ),
            "paddle-ocr": self.paddle_ocr.initialize(
                cache,
                enabled=settings.paddleocr_enabled,
                provisioning=settings.provisioning,
                offline=settings.model_offline,
                license_accepted=settings.license_accepted,
            ),
            "yunet": self.yunet.initialize(
                cache,
                enabled=settings.yunet_enabled,
                provisioning=settings.provisioning,
                offline=settings.model_offline,
                license_accepted=settings.license_accepted,
            ),
        }

    def version_if_ready(self, detector_id: str) -> str | None:
        adapter_map = {
            "gliner-pii": self.gliner,
            "deberta-injection": self.deberta,
            "paddle-ocr": self.paddle_ocr,
            "yunet": self.yunet,
        }
        adapter = adapter_map.get(detector_id)
        if adapter is None or not adapter.is_ready():
            return None
        return adapter.status().version

    def run_gliner(self, text: str) -> list[TextSpanFinding]:
        with self.inference_queue.acquire():
            return self.gliner.detect(text)

    def run_deberta(self, prompt: str) -> list[InjectionFinding]:
        with self.inference_queue.acquire():
            return self.deberta.detect(prompt)

    def run_paddle_ocr(self, validated: ValidatedImage) -> list[ImageRegionFinding]:
        semantic_fn = self.gliner.detect if self.gliner.is_ready() else None
        with self.inference_queue.acquire():
            return self.paddle_ocr.detect(validated, semantic_scanner=semantic_fn)

    def run_yunet(self, validated: ValidatedImage) -> list[ImageRegionFinding]:
        with self.inference_queue.acquire():
            return self.yunet.detect(validated)
