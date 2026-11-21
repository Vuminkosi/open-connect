"""Prepare a bounded, model-consistent document projection for an index writer."""

from __future__ import annotations

import math
import re
from dataclasses import dataclass
from itertools import islice
from typing import Literal, Protocol, Sequence

from openconnect_knowledge.chunking import Chunk, DocumentScope, SourcePage, chunk_pages
from openconnect_knowledge.embeddings import MAX_BATCH, MAX_TOTAL_CHARS, EmbeddingBatch

MAX_VECTOR_VALUES = 4_000_000


class PassageEmbedder(Protocol):
    def embed(self, texts: list[str], *, input_kind: Literal["query", "passage"]) -> EmbeddingBatch: ...


@dataclass(frozen=True, repr=False)
class PreparedChunk:
    chunk: Chunk
    vector: tuple[float, ...]


@dataclass(frozen=True, repr=False)
class PreparedDocument:
    scope: DocumentScope
    model_id: str
    fingerprint: str
    dimension: int
    chunks: tuple[PreparedChunk, ...]


def prepare_document(scope: DocumentScope, pages: Sequence[SourcePage], engine: PassageEmbedder, *,
                     max_chars: int = 1024, overlap_chars: int = 128) -> PreparedDocument:
    """Build an all-or-nothing in-memory projection; persistence and ACLs remain caller duties."""
    chunks = chunk_pages(scope, pages, max_chars=max_chars, overlap_chars=overlap_chars)
    if not chunks:
        raise ValueError("empty_document")
    prepared: list[PreparedChunk] = []
    model_id: str | None = None
    fingerprint: str | None = None
    dimension: int | None = None
    offset = 0
    while offset < len(chunks):
        end = offset
        chars = 0
        while (end < len(chunks) and end - offset < MAX_BATCH
               and chars + len(chunks[end].text) <= MAX_TOTAL_CHARS):
            chars += len(chunks[end].text)
            end += 1
        if end == offset:
            raise ValueError("chunk_exceeds_embedding_limit")
        batch = engine.embed([item.text for item in chunks[offset:end]], input_kind="passage")
        if (len(batch.items) != end - offset or not isinstance(batch.model_id, str) or not batch.model_id
                or not isinstance(batch.fingerprint, str) or not re.fullmatch(r"[0-9a-f]{64}", batch.fingerprint)
                or batch.space_id != scope.embedding_space_id or isinstance(batch.dimension, bool)
                or not isinstance(batch.dimension, int) or not 1 <= batch.dimension <= 8192
                or len(chunks) * batch.dimension > MAX_VECTOR_VALUES):
            raise ValueError("embedding_batch_invalid")
        if model_id is None:
            model_id, fingerprint, dimension = batch.model_id, batch.fingerprint, batch.dimension
        elif (batch.model_id, batch.fingerprint, batch.dimension) != (model_id, fingerprint, dimension):
            raise ValueError("embedding_space_changed")
        for chunk, embedding in zip(chunks[offset:end], batch.items, strict=True):
            snapshot = tuple(islice(embedding.vector, 8193))
            if (embedding.truncated or len(snapshot) != dimension
                    or any(not isinstance(value, (int, float)) or isinstance(value, bool) for value in snapshot)):
                raise ValueError("embedding_invalid_or_truncated")
            try:
                vector = tuple(float(value) for value in snapshot)
            except (OverflowError, ValueError, TypeError):
                raise ValueError("embedding_invalid_or_truncated") from None
            if any(not math.isfinite(value) or abs(value) > 1 for value in vector):
                raise ValueError("embedding_invalid_or_truncated")
            norm = math.sqrt(math.fsum(value * value for value in vector))
            if not math.isfinite(norm) or abs(norm - 1) > 0.001:
                raise ValueError("embedding_invalid_or_truncated")
            prepared.append(PreparedChunk(chunk, vector))
        offset = end
    assert model_id is not None and fingerprint is not None and dimension is not None
    return PreparedDocument(scope, model_id, fingerprint, dimension, tuple(prepared))
