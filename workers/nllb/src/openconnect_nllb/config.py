"""Runtime configuration, sourced only from environment variables."""

from dataclasses import dataclass
from pathlib import Path
import os


def _as_bool(value: str | None) -> bool:
    return value is not None and value.strip().lower() in {"1", "true", "yes", "on"}


@dataclass(frozen=True)
class Settings:
    """Worker configuration; auth must be configured before serving requests."""

    worker_token: str | None
    model_dir: Path
    fake_engine: bool
    max_text_chars: int
    license_accepted: bool

    @classmethod
    def from_environment(cls) -> "Settings":
        token = os.environ.get("OPENCONNECT_WORKER_TOKEN")
        model_dir = Path(os.environ.get("MODEL_DIR", "/models"))
        fake = _as_bool(os.environ.get("OPENCONNECT_FAKE_ENGINE"))
        try:
            max_chars = int(os.environ.get("MAX_TEXT_CHARS", "5000"))
        except ValueError:
            max_chars = 5000
        accepted = _as_bool(os.environ.get("LICENSE_ACCEPTED"))
        return cls(
            worker_token=token if token else None,
            model_dir=model_dir,
            fake_engine=fake,
            max_text_chars=max(1, max_chars),
            license_accepted=accepted,
        )
