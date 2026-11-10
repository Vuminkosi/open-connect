"""Translation engines: fake (CI stub) and real (local-checkpoint, offline-only)."""

from __future__ import annotations

import os
from dataclasses import dataclass
import importlib.util

from openconnect_nllb.languages import is_supported_pair
from openconnect_nllb.manifest import ModelManifest

FAKE_MODEL_REVISION = "fake-nllb-0.1.0"
FAKE_TOKENIZER_HASH = "fake-tokenizer-hash"
FAKE_WARNING = "fake-engine-non-production: deterministic stub for CI only"


@dataclass(frozen=True)
class TranslateOutcome:
    translated_text: str
    source_lang: str
    target_lang: str
    model_revision: str
    tokenizer_hash: str
    engine: str


class LanguageUnsupportedError(ValueError):
    pass


class ModelUnavailableError(RuntimeError):
    pass


class LicenseDeniedError(RuntimeError):
    pass


def _offline_enforced() -> bool:
    return os.environ.get("HF_HUB_OFFLINE", "") == "1"


def decoder_available() -> bool:
    """Pinned neural decoder present (torch + transformers)."""
    return importlib.util.find_spec("torch") is not None and (
        importlib.util.find_spec("transformers") is not None
    )


def _require_pair(source: str, target: str) -> None:
    if source == target:
        raise LanguageUnsupportedError("source and target languages must differ")
    if not is_supported_pair(source, target):
        raise LanguageUnsupportedError(f"unsupported language pair: {source}->{target}")


class FakeEngine:
    engine_name = "fake"

    def translate(self, text: str, *, source_lang: str, target_lang: str) -> TranslateOutcome:
        _require_pair(source_lang, target_lang)
        return TranslateOutcome(
            translated_text=f"[{target_lang} fake] {text}",
            source_lang=source_lang,
            target_lang=target_lang,
            model_revision=FAKE_MODEL_REVISION,
            tokenizer_hash=FAKE_TOKENIZER_HASH,
            engine=self.engine_name,
        )


class RealEngine:
    """Local-checkpoint adapter: manifest-gated, offline-only, no remote fallback."""

    engine_name = "real"

    def __init__(self, manifest: ModelManifest | None, *, ready: bool, reason: str | None) -> None:
        self._manifest = manifest
        self._ready = ready
        self._reason = reason

    def translate(self, text: str, *, source_lang: str, target_lang: str) -> TranslateOutcome:
        _require_pair(source_lang, target_lang)
        if not self._ready:
            if self._reason == "license_denied":
                raise LicenseDeniedError("model license not accepted")
            raise ModelUnavailableError("model unavailable")
        if not _offline_enforced():
            raise ModelUnavailableError("HF_HUB_OFFLINE=1 required; refusing network fetch")
        if self._manifest is None:  # pragma: no cover - guarded by readiness
            raise ModelUnavailableError("model unavailable")
        # Local-checkpoint decoding plugs in here (pinned torch/transformers
        # versions). Without the pinned neural runtime this worker must not
        # fabricate translations and must not fetch anything remotely: there
        # is no remote fallback.
        if not decoder_available():
            raise ModelUnavailableError("real decoder not installed")
        raise ModelUnavailableError("model unavailable")
