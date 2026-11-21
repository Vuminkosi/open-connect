"""Offline NLLB decoder over an already verified local bundle."""

from __future__ import annotations

import os
import threading
from importlib.metadata import PackageNotFoundError, version

from openconnect_nllb.decoder_bundle import DecoderBundle
from openconnect_nllb.languages import SUPPORTED_LANGUAGES, is_supported_pair

_MAX_INPUT_TOKENS = 1024
_MAX_NEW_TOKENS = 256


class DecoderUnavailable(RuntimeError):
    """A local model or its runtime is not usable."""


class DecoderInputError(ValueError):
    """Input exceeds the decoder's supported bounds."""


def _runtime_versions_valid() -> bool:
    try:
        return version("torch").split("+")[0] == "2.7.1" and version("transformers") == "5.23.0"
    except PackageNotFoundError:
        return False


class OfflineNllbDecoder:
    """One CPU decoder, serialized because the tokenizer source language is mutable."""

    def __init__(self, bundle: DecoderBundle) -> None:
        if os.environ.get("HF_HUB_OFFLINE") != "1" or not _runtime_versions_valid():
            raise DecoderUnavailable("local decoder runtime unavailable")
        try:
            import torch
            from transformers import AutoModelForSeq2SeqLM, AutoTokenizer, NllbTokenizer

            self._torch = torch
            self._tokenizer = AutoTokenizer.from_pretrained(
                str(bundle.assets_dir), local_files_only=True, trust_remote_code=False
            )
            if not isinstance(self._tokenizer, NllbTokenizer):
                raise DecoderUnavailable("unsupported tokenizer")
            self._model = AutoModelForSeq2SeqLM.from_pretrained(
                str(bundle.assets_dir), local_files_only=True, trust_remote_code=False,
                use_safetensors=True,
            )
            if self._model.config.model_type != "m2m_100":
                raise DecoderUnavailable("unsupported model architecture")
            self._model.eval()
            for language in SUPPORTED_LANGUAGES:
                token_id = self._tokenizer.convert_tokens_to_ids(language)
                if not isinstance(token_id, int) or token_id < 0 or token_id == self._tokenizer.unk_token_id:
                    raise DecoderUnavailable("missing language token")
        except DecoderUnavailable:
            raise
        except Exception:
            raise DecoderUnavailable("local decoder could not be loaded") from None
        self._lock = threading.Lock()

    def translate(self, text: str, source_lang: str, target_lang: str) -> str:
        if not text or not is_supported_pair(source_lang, target_lang):
            raise DecoderInputError("invalid translation input")
        try:
            with self._lock, self._torch.inference_mode():
                self._tokenizer.src_lang = source_lang
                inputs = self._tokenizer(text, return_tensors="pt", truncation=False)
                if inputs["input_ids"].shape[-1] > _MAX_INPUT_TOKENS:
                    raise DecoderInputError("translation input exceeds token limit")
                target_id = self._tokenizer.convert_tokens_to_ids(target_lang)
                output = self._model.generate(
                    **inputs, forced_bos_token_id=target_id, max_new_tokens=_MAX_NEW_TOKENS,
                    num_beams=1, num_return_sequences=1, do_sample=False,
                )
                tokens = output[0]
                if len(tokens) >= _MAX_NEW_TOKENS and int(tokens[-1]) != self._tokenizer.eos_token_id:
                    raise DecoderUnavailable("translation output exceeded limit")
                result = self._tokenizer.decode(tokens, skip_special_tokens=True)
                if not isinstance(result, str) or not result.strip() or len(result) > 20000:
                    raise DecoderUnavailable("invalid translation output")
                return result
        except (DecoderUnavailable, DecoderInputError):
            raise
        except Exception:
            raise DecoderUnavailable("local translation failed") from None
