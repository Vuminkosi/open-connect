"""Environment-driven settings for the OpenConnect IMessaging worker.

Defaults are fail-closed: the worker is disabled until ``OC_IMESSAGING_ENABLED=true``
and private authentication credentials are configured.
"""

from __future__ import annotations

import os
from dataclasses import dataclass, field
from pathlib import Path

from openconnect_worker_common.security.signing import key_from_environment
from openconnect_worker_common.transport.nats import DEFAULT_NATS_URL

VALID_PUSH_MODES = frozenset({"disabled", "direct", "relay"})
FORBIDDEN_EXTERNAL_CHANNELS = frozenset(
    {"whatsapp", "whatsapp-business", "telegram", "discord", "sms", "signal"}
)


def _env_bool(name: str, default: bool = False) -> bool:
    raw = os.environ.get(name)
    if raw is None:
        return default
    return raw.strip().lower() in {"1", "true", "yes", "on"}


def _env_int(name: str, default: int, *, minimum: int, maximum: int) -> int:
    raw = os.environ.get(name)
    if raw is None or not raw.strip():
        return default
    value = int(raw.strip())
    if value < minimum or value > maximum:
        raise ValueError(f"{name} must be between {minimum} and {maximum}")
    return value


def _parse_rotation_keys(raw: str | None) -> dict[str, bytes]:
    """Parse ``kid1:hex1,kid2:hex2`` key rotation map into validated >=32-byte keys."""
    if not raw or not raw.strip():
        return {}
    keys: dict[str, bytes] = {}
    for entry in raw.split(","):
        item = entry.strip()
        if not item:
            continue
        if ":" not in item:
            raise ValueError("OPENCONNECT_WORKER_SIGNING_KEYS entries must be 'kid:hexkey'")
        kid, hex_key = item.split(":", 1)
        kid = kid.strip()
        if not kid:
            raise ValueError("signing key id must not be empty")
        key_bytes = bytes.fromhex(hex_key.strip())
        if len(key_bytes) < 32:
            raise ValueError(f"signing key {kid!r} must be at least 32 bytes")
        keys[kid] = key_bytes
    return keys


@dataclass(frozen=True)
class Settings:
    """Validated runtime settings for the IMessaging worker."""

    enabled: bool = False
    worker_token: str | None = None
    signing_key: bytes | None = None
    rotation_keys: dict[str, bytes] = field(default_factory=dict)
    nats_url: str = DEFAULT_NATS_URL
    nats_required: bool = False
    push_mode: str = "disabled"
    relay_enabled: bool = False
    fcm_project_id: str | None = None
    fcm_credentials_path: Path | None = None
    fcm_access_token: str | None = None
    webpush_vapid_public_key: str | None = None
    webpush_vapid_private_key: str | None = None
    webpush_subject: str | None = None
    max_body_bytes: int = 512 * 1024
    max_queue_depth: int = 1024
    max_clock_skew_seconds: int = 60
    max_request_ttl_seconds: int = 300
    nonce_cache_size: int = 10_000
    event_dedup_size: int = 20_000
    presence_max_entries: int = 50_000

    @classmethod
    def from_environment(cls) -> Settings:
        enabled = _env_bool("OC_IMESSAGING_ENABLED", False)
        token = os.environ.get("OPENCONNECT_WORKER_TOKEN") or None
        signing_key = key_from_environment("OPENCONNECT_WORKER_SIGNING_KEY")
        if signing_key is not None and len(signing_key) < 32:
            signing_key = None
        rotation_keys = _parse_rotation_keys(os.environ.get("OPENCONNECT_WORKER_SIGNING_KEYS"))
        push_mode = os.environ.get("OC_IMESSAGING_PUSH_MODE", "disabled").strip().lower()
        if push_mode not in VALID_PUSH_MODES:
            raise ValueError(
                f"OC_IMESSAGING_PUSH_MODE must be one of {sorted(VALID_PUSH_MODES)}"
            )
        relay_enabled = _env_bool("OC_IMESSAGING_RELAY_ENABLED", False)
        fcm_cred_raw = os.environ.get("OC_IMESSAGING_FCM_CREDENTIALS_PATH")
        fcm_credentials_path = Path(fcm_cred_raw) if fcm_cred_raw else None

        return cls(
            enabled=enabled,
            worker_token=token,
            signing_key=signing_key,
            rotation_keys=rotation_keys,
            nats_url=os.environ.get("OPENCONNECT_NATS_URL", DEFAULT_NATS_URL),
            nats_required=_env_bool("OC_IMESSAGING_NATS_REQUIRED", False),
            push_mode=push_mode,
            relay_enabled=relay_enabled,
            fcm_project_id=os.environ.get("OC_IMESSAGING_FCM_PROJECT_ID") or None,
            fcm_credentials_path=fcm_credentials_path,
            fcm_access_token=os.environ.get("OC_IMESSAGING_FCM_ACCESS_TOKEN") or None,
            webpush_vapid_public_key=os.environ.get("OC_IMESSAGING_WEBPUSH_VAPID_PUBLIC_KEY")
            or None,
            webpush_vapid_private_key=os.environ.get("OC_IMESSAGING_WEBPUSH_VAPID_PRIVATE_KEY")
            or None,
            webpush_subject=os.environ.get("OC_IMESSAGING_WEBPUSH_SUBJECT") or None,
            max_body_bytes=_env_int(
                "OC_IMESSAGING_MAX_BODY_BYTES", 512 * 1024, minimum=1024, maximum=2 * 1024 * 1024
            ),
            max_queue_depth=_env_int(
                "OC_IMESSAGING_MAX_QUEUE_DEPTH", 1024, minimum=1, maximum=65536
            ),
            max_clock_skew_seconds=_env_int(
                "OC_IMESSAGING_MAX_CLOCK_SKEW_SECONDS", 60, minimum=1, maximum=300
            ),
            max_request_ttl_seconds=_env_int(
                "OC_IMESSAGING_MAX_REQUEST_TTL_SECONDS", 300, minimum=5, maximum=900
            ),
            nonce_cache_size=_env_int(
                "OC_IMESSAGING_NONCE_CACHE_SIZE", 10_000, minimum=16, maximum=500_000
            ),
            event_dedup_size=_env_int(
                "OC_IMESSAGING_EVENT_DEDUP_SIZE", 20_000, minimum=16, maximum=500_000
            ),
            presence_max_entries=_env_int(
                "OC_IMESSAGING_PRESENCE_MAX_ENTRIES", 50_000, minimum=16, maximum=500_000
            ),
        )

    @property
    def all_signing_keys(self) -> dict[str, bytes]:
        keys = dict(self.rotation_keys)
        if self.signing_key is not None:
            keys.setdefault("default", self.signing_key)
        return keys

    @property
    def is_auth_configured(self) -> bool:
        return bool(self.worker_token) and bool(self.all_signing_keys)
