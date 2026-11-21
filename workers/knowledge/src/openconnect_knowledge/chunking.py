"""Deterministic, source-cited chunks for a tenant-scoped knowledge index."""

from __future__ import annotations

import hashlib
import re
from dataclasses import dataclass
from typing import Sequence

MAX_PAGES = 30
MAX_DOCUMENT_CHARS = 10_000_000
MAX_CHUNKS = 10_000
MAX_CHUNK_CHARS = 8192
CHUNKER_VERSION = "source-chunks-v1"


@dataclass(frozen=True, repr=False)
class DocumentScope:
    """Trusted application scope, including the source and embedding revisions."""

    tenant_id: str
    application_id: str
    collection_id: str
    document_id: str
    source_revision: str
    embedding_space_id: str


@dataclass(frozen=True, repr=False)
class SourcePage:
    page_id: str
    text: str


@dataclass(frozen=True, repr=False)
class Chunk:
    chunk_id: str
    page_id: str
    page_index: int
    start: int
    end: int
    text: str
    content_sha256: str


def _identifier(value: str) -> bytes:
    if (not isinstance(value, str) or not value.strip() or len(value) > 256
            or any(ord(char) < 32 or ord(char) == 127 for char in value)):
        raise ValueError("invalid_scope")
    try:
        return value.encode("utf-8")
    except UnicodeEncodeError as exc:
        raise ValueError("invalid_scope") from exc


def _digest(parts: Sequence[bytes]) -> str:
    digest = hashlib.sha256()
    for part in parts:
        digest.update(len(part).to_bytes(8, "big"))
        digest.update(part)
    return digest.hexdigest()


def chunk_pages(scope: DocumentScope, pages: Sequence[SourcePage], *,
                max_chars: int = 1024, overlap_chars: int = 128) -> tuple[Chunk, ...]:
    """Split admitted pages with exact Unicode code-point citations and bounded overlap.

    Text is not normalized or logged. The trusted host must sanitize and
    authorize the source before this function is called. This function does
    not store content or implement document ACLs, jobs or search.
    """
    if (isinstance(max_chars, bool) or not isinstance(max_chars, int) or not 1 <= max_chars <= MAX_CHUNK_CHARS
            or isinstance(overlap_chars, bool) or not isinstance(overlap_chars, int)
            or not 0 <= overlap_chars <= max_chars // 2):
        raise ValueError("invalid_chunk_configuration")
    if (not isinstance(scope, DocumentScope) or not isinstance(scope.embedding_space_id, str)
            or not re.fullmatch(r"[0-9a-f]{64}", scope.embedding_space_id)):
        raise ValueError("invalid_scope")
    scope_parts = [_identifier(value) for value in (scope.tenant_id, scope.application_id, scope.collection_id,
                   scope.document_id, scope.source_revision, scope.embedding_space_id)]
    if len(pages) > MAX_PAGES:
        raise ValueError("page_limit_exceeded")
    seen: set[str] = set()
    page_data: list[tuple[SourcePage, bytes]] = []
    total = 0
    for page in pages:
        if not isinstance(page, SourcePage):
            raise ValueError("invalid_page")
        page_id = _identifier(page.page_id)
        if page.page_id in seen or not isinstance(page.text, str):
            raise ValueError("invalid_page")
        seen.add(page.page_id)
        total += len(page.text)
        if total > MAX_DOCUMENT_CHARS:
            raise ValueError("document_limit_exceeded")
        try:
            page.text.encode("utf-8")
        except UnicodeEncodeError as exc:
            raise ValueError("invalid_page") from exc
        page_data.append((page, page_id))

    chunks: list[Chunk] = []
    for page_index, (page, page_id) in enumerate(page_data):
        start = 0
        while start < len(page.text):
            end = min(start + max_chars, len(page.text))
            if end < len(page.text):
                # Prefer a boundary in the latter half without risking tiny chunks.
                for candidate in range(end, start + max_chars // 2, -1):
                    if page.text[candidate - 1].isspace():
                        end = candidate
                        break
            actual_start, actual_end = start, end
            while actual_start < actual_end and page.text[actual_start].isspace():
                actual_start += 1
            while actual_end > actual_start and page.text[actual_end - 1].isspace():
                actual_end -= 1
            if actual_start < actual_end:
                if len(chunks) >= MAX_CHUNKS:
                    raise ValueError("chunk_limit_exceeded")
                text = page.text[actual_start:actual_end]
                content_hash = hashlib.sha256(text.encode("utf-8")).hexdigest()
                chunk_id = _digest([CHUNKER_VERSION.encode(), *scope_parts, page_id,
                                    str(actual_start).encode(), str(actual_end).encode(),
                                    str(max_chars).encode(), str(overlap_chars).encode(), content_hash.encode()])
                chunks.append(Chunk(chunk_id, page.page_id, page_index, actual_start,
                                    actual_end, text, content_hash))
            if end == len(page.text):
                break
            start = max(start + 1, end - overlap_chars)
    return tuple(chunks)
