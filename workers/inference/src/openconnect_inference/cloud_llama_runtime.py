"""Offline, digest-pinned llama.cpp inference runtime for Cloud claims."""
from __future__ import annotations

import hashlib
import os
import re
from pathlib import Path
from typing import Any, Protocol

from openconnect_inference.cloud_execution import _Payload


class LocalModel(Protocol):
    def tokenize(self, text: bytes, add_bos: bool = True, special: bool = False) -> list[int]: ...
    def create_completion(self, **kwargs: Any) -> dict[str, Any]: ...


class PreparedLlama:
    def __init__(self, model: LocalModel, tokens: list[int]):
        self._model = model
        self._tokens = tuple(tokens)
        self.input_tokens = len(tokens)

    def generate(self, max_output_tokens: int) -> tuple[str, int]:
        if type(max_output_tokens) is not int or not 1 <= max_output_tokens <= 4096:
            raise ValueError("invalid token limit")
        response = self._model.create_completion(prompt=list(self._tokens), max_tokens=max_output_tokens,
                                                 temperature=0, stream=False, stop=["<|im_end|>"])
        usage = response["usage"]
        output = response["choices"][0]["text"]
        if (not isinstance(output, str) or type(usage["prompt_tokens"]) is not int or
                usage["prompt_tokens"] != self.input_tokens or type(usage["completion_tokens"]) is not int or
                not 0 <= usage["completion_tokens"] <= max_output_tokens):
            raise ValueError("invalid model usage")
        return output, usage["completion_tokens"]


def _valid_configuration(model_id: str, model_revision: str, model_digest: str, context_tokens: int) -> bool:
    return (isinstance(model_id, str) and re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._:/-]{0,255}", model_id) is not None and
            isinstance(model_revision, str) and bool(model_revision.strip()) and len(model_revision) <= 256 and
            isinstance(model_digest, str) and re.fullmatch(r"sha256:[0-9a-f]{64}", model_digest) is not None and
            type(context_tokens) is int and 512 <= context_tokens <= 8192)


class CloudLlamaRuntime:
    """One preloaded local model; callers serialize access to its native context."""

    def __init__(self, model: LocalModel, model_id: str, model_revision: str, model_digest: str, context_tokens: int):
        if not _valid_configuration(model_id, model_revision, model_digest, context_tokens):
            raise ValueError("invalid model configuration")
        self._model = model
        self.model_id = model_id
        self.model_revision = model_revision
        self.model_digest = model_digest
        self._context_tokens = context_tokens

    def close(self) -> None:
        close = getattr(self._model, "close", None)
        if callable(close):
            close()

    def prepare(self, payload: _Payload) -> PreparedLlama:
        if payload.modelId != self.model_id or payload.input.stream:
            raise ValueError("model unavailable")
        parts = []
        for message in payload.input.messages:
            if any(marker in message.content for marker in ("<|im_start|>", "<|im_end|>", "\x00")):
                raise ValueError("invalid message")
            parts.append(f"<|im_start|>{message.role}\n{message.content}<|im_end|>\n")
        prompt = ("".join(parts) + "<|im_start|>assistant\n").encode("utf-8")
        tokens = self._model.tokenize(prompt, add_bos=True, special=True)
        if (not isinstance(tokens, list) or not tokens or any(type(token) is not int or token < 0 for token in tokens) or
                len(tokens) + payload.input.maxTokens > self._context_tokens):
            raise ValueError("input exceeds context")
        return PreparedLlama(self._model, tokens)


def load_verified_runtime(path: Path, model_id: str, model_revision: str, model_digest: str,
                          context_tokens: int, threads: int, *, license_accepted: bool, artifact_reviewed: bool) -> CloudLlamaRuntime:
    """Load an already-provisioned local file after explicit approval and SHA-256 verification."""
    if (license_accepted is not True or artifact_reviewed is not True or not path.is_absolute() or
            any(part.is_symlink() for part in (path, *path.parents)) or not path.is_file() or path.suffix != ".gguf" or
            not _valid_configuration(model_id, model_revision, model_digest, context_tokens) or
            type(threads) is not int or not 1 <= threads <= 16):
        raise ValueError("model unavailable")
    before = path.stat()
    if before.st_size < 1 or before.st_size > 8 * 1024 * 1024 * 1024:
        raise ValueError("model unavailable")
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(chunk)
    after = path.stat()
    if (digest.hexdigest() != model_digest[7:] or before.st_size != after.st_size or
            before.st_mtime_ns != after.st_mtime_ns or before.st_ino != after.st_ino):
        raise ValueError("model unavailable")
    # This package is optional and must be installed explicitly; no model download occurs here.
    from llama_cpp import Llama

    model = Llama(model_path=os.fspath(path), n_ctx=context_tokens, n_threads=threads,
                  n_batch=256, n_gpu_layers=0, verbose=False)
    return CloudLlamaRuntime(model, model_id, model_revision, model_digest, context_tokens)
