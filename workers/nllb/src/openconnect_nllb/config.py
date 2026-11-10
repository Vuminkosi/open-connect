"""Runtime configuration, sourced only from environment variables."""

from dataclasses import dataclass
from pathlib import Path
import os

from openconnect_nllb.engine_mode import (
    fake_requested,
    is_production,
    parse_device,
    parse_provisioning,
    resolve_cache_dir,
    validate_startup,
)


def _as_bool(value: str | None) -> bool:
    return value is not None and value.strip().lower() in {"1", "true", "yes", "on"}


@dataclass(frozen=True)
class Settings:
    """Worker configuration; auth must be configured before serving requests."""

    worker_token: str | None
    model_dir: Path
    fake_engine: bool
    is_production: bool
    model_device: str
    max_text_chars: int
    license_accepted: bool
    provisioning: str = "first-start-if-missing"

    @classmethod
    def from_environment(cls) -> "Settings":
        token = os.environ.get("OPENCONNECT_WORKER_TOKEN")
        # Canonical cache dir is OC_MODEL_CACHE_DIR (default /models);
        # MODEL_DIR is the deprecated alias (OC_ wins). Informational until
        # a provisioner lands: never triggers downloads. Unknown provisioning
        # modes fail closed at startup; "disabled" never provisions, so missing
        # assets leave the worker unready.
        model_dir = Path(
            resolve_cache_dir(os.environ.get("OC_MODEL_CACHE_DIR"), os.environ.get("MODEL_DIR"))
        )
        # Canonical engine switch is OC_WORKER_ENGINE (real|fake, default
        # real); OPENCONNECT_FAKE_ENGINE is the deprecated alias (OC_ wins).
        fake = fake_requested()
        # Fail closed at startup: fake requested in production never serves.
        validate_startup(fake=fake)
        try:
            max_chars = int(os.environ.get("MAX_TEXT_CHARS", "5000"))
        except ValueError:
            max_chars = 5000
        accepted = _as_bool(os.environ.get("LICENSE_ACCEPTED"))
        return cls(
            worker_token=token if token else None,
            model_dir=model_dir,
            fake_engine=fake,
            is_production=is_production(),
            model_device=parse_device(os.environ.get("OC_MODEL_DEVICE")),
            max_text_chars=max(1, max_chars),
            license_accepted=accepted,
            provisioning=parse_provisioning(os.environ.get("OC_MODEL_PROVISIONING")),
        )
