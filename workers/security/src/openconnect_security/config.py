"""Runtime configuration, sourced only from environment variables."""

from dataclasses import dataclass
import os


@dataclass(frozen=True)
class Settings:
    """Worker configuration; auth must be configured before serving scans."""

    scan_token: str | None

    @classmethod
    def from_environment(cls) -> "Settings":
        token = os.environ.get("OPENCONNECT_SCAN_TOKEN")
        return cls(scan_token=token if token else None)
