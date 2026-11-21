import hashlib
from dataclasses import replace

import pytest

from openconnect_knowledge.chunking import DocumentScope, SourcePage, chunk_pages

SCOPE = DocumentScope("tenant", "app", "collection", "document", "revision-1", "a" * 64)


def test_chunks_preserve_exact_unicode_page_citations_and_cover_text():
    pages = [SourcePage("page-1", "  Hello 😀 world!\nAnother paragraph with details.  "),
             SourcePage("page-2", "Second page")]
    chunks = chunk_pages(SCOPE, pages, max_chars=16, overlap_chars=3)
    assert chunks == chunk_pages(SCOPE, pages, max_chars=16, overlap_chars=3)
    assert all(chunk.text == pages[chunk.page_index].text[chunk.start:chunk.end] for chunk in chunks)
    assert all(chunk.content_sha256 == hashlib.sha256(chunk.text.encode()).hexdigest() for chunk in chunks)
    assert all(0 < len(chunk.text) <= 16 for chunk in chunks)
    for page_index, page in enumerate(pages):
        covered = {i for chunk in chunks if chunk.page_index == page_index for i in range(chunk.start, chunk.end)}
        assert all(i in covered for i, char in enumerate(page.text) if not char.isspace())
    assert len({chunk.chunk_id for chunk in chunks}) == len(chunks)


@pytest.mark.parametrize("change", ["tenant_id", "application_id", "collection_id", "document_id",
                                     "source_revision", "embedding_space_id"])
def test_chunk_identity_separates_every_scope_and_model_space(change):
    alternate = "b" * 64 if change == "embedding_space_id" else "other"
    page = [SourcePage("p1", "same source")]
    assert chunk_pages(SCOPE, page)[0].chunk_id != chunk_pages(replace(SCOPE, **{change: alternate}), page)[0].chunk_id


def test_chunk_ids_change_with_content_and_configuration():
    base = chunk_pages(SCOPE, [SourcePage("p", "sample text")], max_chars=32, overlap_chars=2)[0]
    assert base.chunk_id != chunk_pages(SCOPE, [SourcePage("p", "sample text!")], max_chars=32, overlap_chars=2)[0].chunk_id
    assert base.chunk_id != chunk_pages(SCOPE, [SourcePage("p", "sample text")], max_chars=32, overlap_chars=3)[0].chunk_id


@pytest.mark.parametrize("pages", [[SourcePage("p", "x"), SourcePage("p", "y")],
    [SourcePage("", "x")], [SourcePage("p", "\ud800")], [SourcePage("p", "x")] * 31])
def test_invalid_pages_fail_before_returning_chunks(pages):
    with pytest.raises(ValueError):
        chunk_pages(SCOPE, pages)


@pytest.mark.parametrize("max_chars,overlap", [(0, 0), (8193, 0), (10, 6), (True, 0), (4, -1)])
def test_invalid_limits_fail_closed(max_chars, overlap):
    with pytest.raises(ValueError):
        chunk_pages(SCOPE, [SourcePage("p", "text")], max_chars=max_chars, overlap_chars=overlap)


def test_ten_thousand_chunk_limit_and_no_partial_result():
    text = "x" * 640000
    chunks = chunk_pages(SCOPE, [SourcePage("p", text)], max_chars=64, overlap_chars=0)
    assert len(chunks) == 10_000
    with pytest.raises(ValueError, match="chunk_limit_exceeded"):
        chunk_pages(SCOPE, [SourcePage("p", text + "x")], max_chars=64, overlap_chars=0)


def test_document_scope_pages_and_chunks_do_not_expose_contents_in_repr():
    sensitive = "private-document-content"
    scope = DocumentScope(sensitive, "app", "collection", "doc", "revision", "a" * 64)
    page = SourcePage("page", sensitive)
    chunk = chunk_pages(scope, [page])[0]
    for value in (scope, page, chunk):
        assert sensitive not in repr(value)
