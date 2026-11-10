"""Runtime configuration, sourced only from environment variables."""

from __future__ import annotations

from dataclasses import dataclass
import json
import os
import re

from openconnect_kokoro.engine_mode import (
    fake_requested,
    is_production,
    parse_device,
    parse_offline,
    parse_provisioning,
    resolve_cache_dir,
    resolve_environment,
    validate_startup,
)
from openconnect_kokoro.runtime_cache import RuntimeModelCacheManager, RuntimeModelManifest

_VOICE_PATTERN = re.compile(r"^[a-z]{2}_[a-z0-9_]{1,32}$")


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
    require_model: bool
    voices: tuple[str, ...]
    default_voice: str
    provisioning: str = "first-start-if-missing"
    kokoro_model: str = "hexgrad/Kokoro-82M"
    environment: str = "Production"
    model_offline: bool = False

    @classmethod
    def from_environment(cls) -> "Settings":
        token = os.environ.get("OPENCONNECT_WORKER_TOKEN")
        fake = fake_requested()
        validate_startup(fake=fake)
        oc_voice = os.environ.get("OC_KOKORO_VOICE")
        raw_voices = os.environ.get("KOKORO_VOICES") or oc_voice or "af_heart"
        voices = tuple(voice.strip() for voice in raw_voices.split(",") if voice.strip()) or (
            "af_heart",
        )
        verified = tuple(voice for voice in voices if _VOICE_PATTERN.match(voice))
        default = oc_voice or os.environ.get("KOKORO_DEFAULT_VOICE", "af_heart")
        if default not in verified:
            default = verified[0] if verified else "af_heart"
        model_id = os.environ.get("OC_KOKORO_MODEL", "hexgrad/Kokoro-82M").strip() or (
            "hexgrad/Kokoro-82M"
        )
        return cls(
            worker_token=token if token else None,
            model_dir=resolve_cache_dir(
                os.environ.get("OC_MODEL_CACHE_DIR"), os.environ.get("MODEL_DIR")
            ),
            fake_engine=fake,
            is_production=is_production(),
            model_device=parse_device(os.environ.get("OC_MODEL_DEVICE")),
            require_model=_as_bool(os.environ.get("KOKORO_REQUIRE_MODEL"), False),
            voices=verified,
            default_voice=default,
            provisioning=parse_provisioning(os.environ.get("OC_MODEL_PROVISIONING")),
            kokoro_model=model_id,
            environment=resolve_environment(),
            model_offline=parse_offline(os.environ.get("OC_MODEL_OFFLINE")),
        )

    def verified_cache_ready(self) -> bool:
        """Return True when a verified revision exists in the runtime model cache."""
        mgr = RuntimeModelCacheManager(self.model_dir, "kokoro")
        active = mgr.read_active(self.kokoro_model)
        if active is None:
            return False
        fp = str(active.get("activeFingerprint", ""))
        lock_path = mgr.paths_for(self.kokoro_model).manifest_lock_path(fp)
        if not fp or not lock_path.is_file():
            return False
        try:
            lock_data = json.loads(lock_path.read_text(encoding="utf-8"))
            manifest = RuntimeModelManifest.model_validate(lock_data["manifest"])
        except Exception:
            return False
        return mgr.verify_for_request(manifest, license_accepted=True).ready

    def model_marker_present(self) -> bool:
        marker = os.path.join(self.model_dir, "kokoro-init.json")
        return os.path.exists(marker) or self.verified_cache_ready()
