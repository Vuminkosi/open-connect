"""Uniform engine-mode contract: real-by-default, env-gated fake, device selection.

Normative behavior (identical in every Python worker):

1. Real execution is the DEFAULT. The canonical switch is
   ``OC_WORKER_ENGINE=real|fake`` (exact variable name; accepted values
   ``real``/``fake``, case-insensitive, surrounding whitespace ignored).
   ``OPENCONNECT_FAKE_ENGINE=1|true|yes`` remains a deprecated alias that
   still works and logs a one-time deprecation warning; when both are set,
   ``OC_WORKER_ENGINE`` wins. An unknown ``OC_WORKER_ENGINE`` value fails
   closed at startup (``EngineModeConfigError``). Anything else -- including
   an absent variable -- selects real mode. NOTE: unlike some generic boolean
   helpers, ``on`` does NOT enable fake mode; the legacy accepted set is
   exactly ``1``/``true``/``yes``.
2. Production rejects fake. When ``OPENCONNECT_ENV=production`` (exact variable
   name and exact, case-sensitive value) AND fake is requested, the worker
   fails closed twice: at startup (``Settings.from_environment`` raises
   ``FakeNotAllowedInProductionError``, so the container never serves) and on
   execute (HTTP 503, stable code ``fake_not_allowed_in_production``).
3. Fake mode bypasses ONLY model provisioning/init. Auth (Bearer), policy
   checks (license gates, language allowlists, privacy blocks), schema
   validation, and event/lease contracts behave IDENTICALLY in both modes.
4. Device selection via ``OC_MODEL_DEVICE`` (exact name): ``cpu`` | ``cuda`` |
   ``auto`` (default; unknown values fall back to ``auto``).
   Precedence (highest to lowest):
   a. An explicit ``cpu`` or ``cuda`` request always wins over ``auto`` logic.
   b. ``cuda`` (explicit or via ``auto``) additionally requires a compatible
      NVIDIA GPU to be detected; without one the worker is unready and
      execute fails closed with ``gpu_unavailable`` (HTTP 503).
   c. ``auto`` prefers GPU only when ``OC_GPU_ALLOWED=1`` (exact variable
      name; accepted values ``1``/``true``/``yes``, case-insensitive) AND a
      GPU is detected; otherwise it serves on CPU.
   d. ``cpu`` never consults GPU detection at all (no CUDA init attempt).
   GPU detection never raises and never downloads: it lazily probes
   ``torch.cuda.is_available()`` (absent torch means no GPU) and honours an
   empty or ``-1`` ``CUDA_VISIBLE_DEVICES`` as "no GPU" without importing
   torch. The detector is injectable for unit tests.
5. Model provisioning via ``OC_MODEL_PROVISIONING`` (exact name):
   ``disabled`` | ``verify-existing-only`` | ``first-start-if-missing``
   (default; unknown values fail closed at startup). No worker downloads
   model weights today -- provisioning is informational until a provisioner
   lands -- so ``disabled`` and ``first-start-if-missing`` behave identically:
   missing assets make the worker unready, and nothing is ever fetched.
   The effective value is exposed in ``/health/ready``.
6. Model cache via ``OC_MODEL_CACHE_DIR`` (exact name; default ``/models``).
   ``MODEL_DIR`` remains a deprecated alias that still works (with a one-time
   warning); ``OC_MODEL_CACHE_DIR`` wins when set. Informational until the
   provisioner lands: exposed in ``/health/ready``, never triggers downloads.
"""

from __future__ import annotations

import logging
import os
from collections.abc import Callable, Mapping
from dataclasses import dataclass
from typing import Literal

logger = logging.getLogger(__name__)

DeviceKind = Literal["cpu", "cuda", "auto"]
EffectiveDevice = Literal["cpu", "cuda"]

ENGINE_ENV_VAR = "OC_WORKER_ENGINE"
ENGINE_REAL = "real"
ENGINE_FAKE = "fake"
FAKE_ENV_VAR = "OPENCONNECT_FAKE_ENGINE"
ENV_ENV_VAR = "OPENCONNECT_ENV"
DEVICE_ENV_VAR = "OC_MODEL_DEVICE"
GPU_ALLOWED_ENV_VAR = "OC_GPU_ALLOWED"
PROVISIONING_ENV_VAR = "OC_MODEL_PROVISIONING"
CACHE_DIR_ENV_VAR = "OC_MODEL_CACHE_DIR"
LEGACY_MODEL_DIR_ENV_VAR = "MODEL_DIR"

DEFAULT_PROVISIONING = "first-start-if-missing"
DEFAULT_CACHE_DIR = "/models"
PROVISIONING_MODES = ("disabled", "verify-existing-only", "first-start-if-missing")

PRODUCTION_VALUE = "production"

FAKE_NOT_ALLOWED_CODE = "fake_not_allowed_in_production"
GPU_UNAVAILABLE_CODE = "gpu_unavailable"

_ON_VALUES = frozenset({"1", "true", "yes"})

Detector = Callable[[], bool]


class FakeNotAllowedInProductionError(ValueError):
    """Startup refusal: fake engine requested while OPENCONNECT_ENV=production."""


class EngineModeConfigError(ValueError):
    """Startup refusal: OC_WORKER_ENGINE or OC_MODEL_PROVISIONING is invalid."""


_legacy_warned: set[str] = set()


def warn_deprecated_alias(name: str, replacement: str) -> None:
    """Log a one-time deprecation warning for a legacy env var name."""
    if name not in _legacy_warned:
        _legacy_warned.add(name)
        logger.warning("%s is deprecated; use %s instead", name, replacement)


def _get(env: Mapping[str, str] | None, name: str) -> str | None:
    if env is not None:
        return env.get(name)
    return os.environ.get(name)


def resolve_engine_mode(env: Mapping[str, str] | None = None) -> str:
    """Canonical engine mode: ``real`` (default) or ``fake``.

    ``OC_WORKER_ENGINE`` is primary; the legacy ``OPENCONNECT_FAKE_ENGINE``
    1/true/yes flag still works as a deprecated alias (one-time warning) and
    is ignored whenever ``OC_WORKER_ENGINE`` is set. Unknown ``OC_`` values
    raise ``EngineModeConfigError`` (fail closed at startup).
    """
    raw = _get(env, ENGINE_ENV_VAR)
    if raw is not None and raw.strip() != "":
        value = raw.strip().lower()
        if value not in (ENGINE_REAL, ENGINE_FAKE):
            raise EngineModeConfigError(f"{ENGINE_ENV_VAR} must be 'real' or 'fake', got {raw!r}")
        return value
    legacy = _get(env, FAKE_ENV_VAR)
    if legacy is not None and legacy.strip() != "":
        warn_deprecated_alias(FAKE_ENV_VAR, ENGINE_ENV_VAR)
        return ENGINE_FAKE if legacy.strip().lower() in _ON_VALUES else ENGINE_REAL
    return ENGINE_REAL


def fake_requested(env: Mapping[str, str] | None = None) -> bool:
    """True only when the canonical engine mode resolves to ``fake``."""
    return resolve_engine_mode(env) == ENGINE_FAKE


def is_production(env: Mapping[str, str] | None = None) -> bool:
    """True only when OPENCONNECT_ENV is exactly ``production`` (case-sensitive)."""
    return _get(env, ENV_ENV_VAR) == PRODUCTION_VALUE


def validate_startup(*, fake: bool, env: Mapping[str, str] | None = None) -> None:
    """Fail closed at startup: fake requested in production never serves."""
    if fake and is_production(env):
        raise FakeNotAllowedInProductionError(
            f"{FAKE_ENV_VAR}=1 is not allowed when {ENV_ENV_VAR}=production"
        )


def production_fake_active(fake_engine: bool, env: Mapping[str, str] | None = None) -> bool:
    """Live execute-path guard: fake-enabled settings under a production env."""
    return bool(fake_engine) and is_production(env)


def parse_device(raw: str | None) -> DeviceKind:
    """Normalize OC_MODEL_DEVICE; default (and unknown values) is ``auto``."""
    if raw is not None and raw.strip().lower() in ("cpu", "cuda", "auto"):
        return raw.strip().lower()  # type: ignore[return-value]
    return "auto"


def parse_provisioning(raw: str | None) -> str:
    """Normalize OC_MODEL_PROVISIONING; unknown values fail closed at startup."""
    if raw is None or raw.strip() == "":
        return DEFAULT_PROVISIONING
    value = raw.strip().lower()
    if value not in PROVISIONING_MODES:
        raise EngineModeConfigError(
            f"{PROVISIONING_ENV_VAR} must be one of {', '.join(PROVISIONING_MODES)}, got {raw!r}"
        )
    return value


def resolve_cache_dir(primary_raw: str | None, legacy_raw: str | None = None) -> str:
    """Resolve the model cache dir: OC_MODEL_CACHE_DIR wins, MODEL_DIR is the
    deprecated alias (one-time warning), default ``/models``."""
    if primary_raw is not None and primary_raw.strip() != "":
        return primary_raw.strip()
    if legacy_raw is not None and legacy_raw.strip() != "":
        warn_deprecated_alias(LEGACY_MODEL_DIR_ENV_VAR, CACHE_DIR_ENV_VAR)
        return legacy_raw.strip()
    return DEFAULT_CACHE_DIR


def gpu_allowed(env: Mapping[str, str] | None = None) -> bool:
    """True only when OC_GPU_ALLOWED is 1/true/yes (case-insensitive)."""
    raw = _get(env, GPU_ALLOWED_ENV_VAR)
    return raw is not None and raw.strip().lower() in _ON_VALUES


def detect_gpu_present() -> bool:
    """Best-effort GPU probe. Never raises, never downloads, never inits on CPU path.

    An empty or ``-1`` CUDA_VISIBLE_DEVICES means "no GPU" without importing
    torch. Otherwise a lazy ``torch.cuda.is_available()`` probe decides;
    missing/broken torch means no GPU.
    """
    visible = os.environ.get("CUDA_VISIBLE_DEVICES")
    if visible is not None and visible.strip() in ("", "-1"):
        return False
    try:
        import torch

        return bool(torch.cuda.is_available())
    except Exception:
        return False


@dataclass(frozen=True)
class DeviceInfo:
    requested: DeviceKind
    effective: EffectiveDevice
    allowed: bool
    gpu_present: bool | None
    gpu_required_missing: bool


def device_info(
    requested_raw: str | None,
    *,
    env: Mapping[str, str] | None = None,
    gpu_present: bool | None = None,
    detector: Detector | None = None,
) -> DeviceInfo:
    """Resolve the effective device without touching CUDA on the ``cpu`` path.

    ``gpu_present`` (or ``detector``) is consulted only when a GPU could
    actually be used: explicit ``cuda``, or ``auto`` with GPU allowed.
    Pass ``gpu_present``/``detector`` in unit tests to cover both branches
    without real hardware.
    """
    requested = parse_device(requested_raw)
    allowed = gpu_allowed(env)
    if requested == "cpu":
        return DeviceInfo(
            requested="cpu",
            effective="cpu",
            allowed=allowed,
            gpu_present=None,
            gpu_required_missing=False,
        )
    detect = detector if detector is not None else detect_gpu_present
    if requested == "cuda":
        present = gpu_present if gpu_present is not None else detect()
        return DeviceInfo(
            requested="cuda",
            effective="cuda" if present else "cpu",
            allowed=allowed,
            gpu_present=present,
            gpu_required_missing=not present,
        )
    if not allowed:
        return DeviceInfo(
            requested="auto",
            effective="cpu",
            allowed=False,
            gpu_present=None,
            gpu_required_missing=False,
        )
    present = gpu_present if gpu_present is not None else detect()
    return DeviceInfo(
        requested="auto",
        effective="cuda" if present else "cpu",
        allowed=True,
        gpu_present=present,
        gpu_required_missing=False,
    )


def rejection(
    fake_engine: bool,
    model_device: str | None,
    *,
    env: Mapping[str, str] | None = None,
    gpu_present: bool | None = None,
    detector: Detector | None = None,
) -> tuple[DeviceInfo | None, int | None, str | None]:
    """Fail-closed ordering shared by readiness and execute paths.

    Returns ``(info, status_code, code)``; ``status_code``/``code`` are None
    when the request may proceed. Production+fake is checked before GPU
    availability.
    """
    if production_fake_active(fake_engine, env):
        return None, 503, FAKE_NOT_ALLOWED_CODE
    info = device_info(model_device, env=env, gpu_present=gpu_present, detector=detector)
    if info.gpu_required_missing:
        return info, 503, GPU_UNAVAILABLE_CODE
    return info, None, None
