"""Fake MMS synthesis engine with opt-in checkpoints, license gate, LRU RAM cache.

- Only languages in the configured opt-in set may synthesize; anything else
  is rejected without touching the network (the ~1107-language catalogue is
  never pulled).
- Each loaded checkpoint is LRU-cached in RAM only; nothing is persisted.
- CC-BY-NC-4.0 checkpoints refuse commercial traffic (fail closed).
"""

from __future__ import annotations

import base64
import hashlib
import threading
from collections import OrderedDict


class LanguageNotOptedIn(Exception):
    pass


class LicenseBlocked(Exception):
    pass


class Misconfigured(Exception):
    pass


def _fake_audio_bytes(text: str, language: str) -> bytes:
    digest = hashlib.sha256(f"fake-mms|{language}|{text}".encode("utf-8")).digest()
    return b"FAKE-MMS-WAV-16KHZ-MONO|" + digest * 64


class CheckpointCache:
    """LRU cache of loaded checkpoints, RAM only, bounded size."""

    def __init__(self, max_entries: int) -> None:
        self._max_entries = max(1, max_entries)
        self._lock = threading.Lock()
        self._entries: OrderedDict[str, dict[str, object]] = OrderedDict()

    def load(self, language: str, license: str) -> dict[str, object]:
        with self._lock:
            cached = self._entries.get(language)
            if cached is not None:
                self._entries.move_to_end(language)
                return dict(cached)
            record = {"language": language, "license": license, "engine": "fake-engine"}
            self._entries[language] = record
            while len(self._entries) > self._max_entries:
                self._entries.popitem(last=False)
            return dict(record)

    def cached_languages(self) -> list[str]:
        with self._lock:
            return list(self._entries.keys())


class FakeMmsEngine:
    engine = "fake-engine"
    version = "0.1.0"

    def __init__(self, cache: CheckpointCache) -> None:
        self._cache = cache

    def synthesize(
        self,
        text: str,
        language: str,
        opt_in_languages: tuple[str, ...],
        license_for: str,
        commercial_use: bool,
    ) -> dict[str, object]:
        if not opt_in_languages or len(opt_in_languages) > 2:
            raise Misconfigured("mms worker misconfigured: invalid opt-in language set")
        if language not in opt_in_languages:
            # Never fetch the catalogue or the checkpoint; reject outright.
            raise LanguageNotOptedIn(f"language {language!r} is not opted in")
        if commercial_use and license_for.startswith("CC-BY-NC"):
            raise LicenseBlocked(
                f"checkpoint {language!r} is {license_for} and blocks commercial use"
            )
        checkpoint = self._cache.load(language, license_for)
        audio = _fake_audio_bytes(text, language)
        return {
            "text": text,
            "language": language,
            "license": checkpoint["license"],
            "format": "wav",
            "sampleRateHz": 16000,
            "channels": 1,
            "audioBase64": base64.b64encode(audio).decode("ascii"),
            "cacheHit": language in self._cache.cached_languages(),
        }
