"""Uniform engine-mode contract: real-by-default, env-gated fake, device selection.

Normative behavior (identical in every Python worker):

1. Real execution is the DEFAULT. The fake engine is used ONLY when explicitly
   enabled via ``OPENCONNECT_FAKE_ENGINE=1`` (exact variable name; accepted
   values ``1``/``true``/``yes``, case-insensitive, surrounding whitespace
   ignored). Anything else -- including an absent variable -- selects real
   mode. NOTE: unlike some generic boolean helpers, ``on`` does NOT enable
   fake mode; the accepted set is exactly ``1``/``true``/``yes``.
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
"""

from __future__ import annotations

import os
from collections.abc import Callable, Mapping
from dataclasses import dataclass
from typing import Literal

DeviceKind = Literal["cpu", "cuda", "auto"]
EffectiveDevice = Literal["cpu", "cuda"]

FAKE_ENV_VAR = "OPENCONNECT_FAKE_ENGINE"
ENV_ENV_VAR = "OPENCONNECT_ENV"
DEVICE_ENV_VAR = "OC_MODEL_DEVICE"
GPU_ALLOWED_ENV_VAR = "OC_GPU_ALLOWED"

PRODUCTION_VALUE = "production"

FAKE_NOT_ALLOWED_CODE = "fake_not_allowed_in_production"
GPU_UNAVAILABLE_CODE = "gpu_unavailable"

_ON_VALUES = frozenset({"1", "true", "yes"})

Detector = Callable[[], bool]


class FakeNotAllowedInProductionError(ValueError):
    """Startup refusal: fake engine requested while OPENCONNECT_ENV=production."""


def _get(env: Mapping[str, str] | None, name: str) -> str | None:
    if env is not None:
        return env.get(name)
    return os.environ.get(name)


def fake_requested(env: Mapping[str, str] | None = None) -> bool:
    """True only when OPENCONNECT_FAKE_ENGINE is 1/true/yes (case-insensitive)."""
    raw = _get(env, FAKE_ENV_VAR)
    return raw is not None and raw.strip().lower() in _ON_VALUES


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
