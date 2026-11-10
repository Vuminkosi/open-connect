"""Runtime configuration, sourced only from environment variables."""

from __future__ import annotations

import os
from dataclasses import dataclass

from openconnect_mms_tts.engine_mode import (
    fake_requested,
    is_production,
    parse_device,
    parse_provisioning,
    resolve_cache_dir,
    validate_startup,
)

# The full MMS catalogue (~1107 languages) is never pulled; only explicitly
# opted-in checkpoints may be loaded.
MAX_OPT_IN_LANGUAGES = 2

# Known per-checkpoint licenses for the small opt-in set. CC-BY-NC-4.0
# checkpoints must not serve commercial traffic.
CHECKPOINT_LICENSES: dict[str, str] = {
    "eng": "CC-BY-NC-4.0",
    "fra": "CC-BY-NC-4.0",
    "deu": "CC-BY-NC-4.0",
    "spa": "CC-BY-NC-4.0",
}


def _as_bool(value: str | None, default: bool) -> bool:
    if value is None:
        return default
    return value.strip().lower() in {"1", "true", "yes", "on"}


@dataclass(frozen=True)
class Settings:
    """Worker configuration; auth must be configured before serving requests."""

    worker_token: str | None
    model_dir: str
    fake_engine: bool
    is_production: bool
    model_device: str
    languages: tuple[str, ...]
    commercial_use: bool
    cache_size: int
    provisioning: str = "first-start-if-missing"

    @classmethod
    def from_environment(cls) -> "Settings":
        token = os.environ.get("OPENCONNECT_WORKER_TOKEN")
        # Canonical engine switch is OC_WORKER_ENGINE (real|fake, default
        # real); OPENCONNECT_FAKE_ENGINE is the deprecated alias (OC_ wins).
        fake = fake_requested()
        # Fail closed at startup: fake requested in production never serves.
        validate_startup(fake=fake)
        raw = os.environ.get("MMS_LANGUAGES", "eng")
        languages = tuple(lang.strip().lower() for lang in raw.split(",") if lang.strip()) or (
            "eng",
        )
        try:
            cache_size = max(1, int(os.environ.get("MMS_CACHE_SIZE", "2")))
        except ValueError:
            cache_size = 2
        return cls(
            worker_token=token if token else None,
            # Canonical cache dir is OC_MODEL_CACHE_DIR (default /models);
            # MODEL_DIR is the deprecated alias (OC_ wins). Informational
            # until a provisioner lands: never triggers downloads.
            model_dir=resolve_cache_dir(
                os.environ.get("OC_MODEL_CACHE_DIR"), os.environ.get("MODEL_DIR")
            ),
            fake_engine=fake,
            is_production=is_production(),
            model_device=parse_device(os.environ.get("OC_MODEL_DEVICE")),
            languages=languages,
            commercial_use=_as_bool(os.environ.get("MMS_COMMERCIAL_USE"), False),
            cache_size=cache_size,
            provisioning=parse_provisioning(os.environ.get("OC_MODEL_PROVISIONING")),
        )

    def configuration_error(self) -> str | None:
        if len(self.languages) > MAX_OPT_IN_LANGUAGES:
            return (
                f"at most {MAX_OPT_IN_LANGUAGES} opt-in languages allowed, "
                f"got {len(self.languages)}"
            )
        return None

    def license_for(self, language: str) -> str:
        return CHECKPOINT_LICENSES.get(language, "CC-BY-NC-4.0")
