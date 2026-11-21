"""Translation engines: fake (CI stub) and real (local-checkpoint, offline-only)."""

from __future__ import annotations

import os
from dataclasses import dataclass

from openconnect_nllb.languages import is_supported_pair
from openconnect_nllb.manifest import ModelManifest
from openconnect_nllb.decoder import DecoderInputError, DecoderUnavailable, OfflineNllbDecoder

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


class InputTooLongError(ValueError):
    pass


def _offline_enforced() -> bool:
    return os.environ.get("HF_HUB_OFFLINE", "") == "1"


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

    def __init__(
        self, manifest: ModelManifest | None, *, ready: bool, reason: str | None,
        decoder: OfflineNllbDecoder | None = None,
    ) -> None:
        self._manifest = manifest
        self._ready = ready
        self._reason = reason
        self._decoder = decoder

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
        if self._decoder is None:
            raise ModelUnavailableError("local decoder unavailable")
        try:
            translated = self._decoder.translate(text, source_lang, target_lang)
        except DecoderInputError:
            raise InputTooLongError("translation input exceeds token limit") from None
        except DecoderUnavailable:
            raise ModelUnavailableError("local decoder unavailable") from None
        return TranslateOutcome(translated, source_lang, target_lang, self._manifest.revision,
                                self._manifest.tokenizer_hash, self.engine_name)
