"""Runtime configuration, sourced only from environment variables."""

from __future__ import annotations

from dataclasses import dataclass
import os
from pathlib import Path

from openconnect_security.engine_mode import (
    fake_requested,
    is_production,
    parse_device,
    parse_offline,
    parse_provisioning,
    resolve_cache_dir,
    resolve_environment,
    validate_startup,
)

DETECTOR_FLAG_MAP: tuple[tuple[str, str, bool], ...] = (
    ("rules", "OC_SECURITY_RULES_ENABLED", True),
    ("gliner-pii", "OC_SECURITY_GLINER_ENABLED", False),
    ("deberta-injection", "OC_SECURITY_DEBERTA_ENABLED", False),
    ("qwen3guard", "OC_SECURITY_QWEN3GUARD_ENABLED", False),
    ("paddle-ocr", "OC_SECURITY_PADDLEOCR_ENABLED", False),
    ("yunet", "OC_SECURITY_YUNET_ENABLED", False),
)

MODEL_BACKED_DETECTORS: dict[str, str] = {
    "gliner-pii": "urchade/gliner_small-v2.1",
    "deberta-injection": "protectai/deberta-v3-base-prompt-injection-v2",
    "qwen3guard": "Qwen/Qwen3Guard-0.6B",
    "paddle-ocr": "PaddlePaddle/PP-OCRv5_mobile",
    "yunet": "opencv/face_detection_yunet",
}


def _as_bool(raw: str | None, default: bool) -> bool:
    if raw is None or raw.strip() == "":
        return default
    return raw.strip().lower() in {"1", "true", "yes", "on"}


@dataclass(frozen=True)
class Settings:
    """Worker configuration; auth must be configured before serving scans."""

    scan_token: str | None
    model_dir: Path = Path("/models")
    fake_engine: bool = False
    is_production: bool = False
    environment: str = "Production"
    model_device: str = "auto"
    provisioning: str = "first-start-if-missing"
    model_offline: bool = False
    license_accepted: bool = False
    rules_enabled: bool = True
    gliner_enabled: bool = False
    deberta_enabled: bool = False
    qwen3guard_enabled: bool = False
    paddleocr_enabled: bool = False
    yunet_enabled: bool = False

    @classmethod
    def from_environment(cls) -> "Settings":
        token = os.environ.get("OPENCONNECT_SCAN_TOKEN") or os.environ.get(
            "OPENCONNECT_WORKER_TOKEN"
        )
        fake = fake_requested()
        validate_startup(fake=fake)
        cache_dir = Path(
            resolve_cache_dir(os.environ.get("OC_MODEL_CACHE_DIR"), os.environ.get("MODEL_DIR"))
        )
        return cls(
            scan_token=token if token else None,
            model_dir=cache_dir,
            fake_engine=fake,
            is_production=is_production(),
            environment=resolve_environment(),
            model_device=parse_device(os.environ.get("OC_MODEL_DEVICE")),
            provisioning=parse_provisioning(os.environ.get("OC_MODEL_PROVISIONING")),
            model_offline=parse_offline(os.environ.get("OC_MODEL_OFFLINE")),
            license_accepted=_as_bool(os.environ.get("LICENSE_ACCEPTED"), False),
            rules_enabled=_as_bool(os.environ.get("OC_SECURITY_RULES_ENABLED"), True),
            gliner_enabled=_as_bool(os.environ.get("OC_SECURITY_GLINER_ENABLED"), False),
            deberta_enabled=_as_bool(os.environ.get("OC_SECURITY_DEBERTA_ENABLED"), False),
            qwen3guard_enabled=_as_bool(os.environ.get("OC_SECURITY_QWEN3GUARD_ENABLED"), False),
            paddleocr_enabled=_as_bool(os.environ.get("OC_SECURITY_PADDLEOCR_ENABLED"), False),
            yunet_enabled=_as_bool(os.environ.get("OC_SECURITY_YUNET_ENABLED"), False),
        )

    def enabled_detectors(self) -> list[str]:
        enabled: list[str] = []
        if self.rules_enabled:
            enabled.append("rules")
        if self.gliner_enabled:
            enabled.append("gliner-pii")
        if self.deberta_enabled:
            enabled.append("deberta-injection")
        if self.qwen3guard_enabled:
            enabled.append("qwen3guard")
        if self.paddleocr_enabled:
            enabled.append("paddle-ocr")
        if self.yunet_enabled:
            enabled.append("yunet")
        return enabled

    def enabled_model_detectors(self) -> list[str]:
        return [d for d in self.enabled_detectors() if d != "rules"]
