from dataclasses import replace

import pytest

from openconnect_knowledge.chunking import DocumentScope, SourcePage
from openconnect_knowledge.embeddings import Embedding, EmbeddingBatch
from openconnect_knowledge.index_preparation import prepare_document

SCOPE = DocumentScope("tenant", "app", "collection", "document", "revision", "a" * 64)


class Engine:
    def __init__(self):
        self.calls = []

    def embed(self, texts, *, input_kind):
        assert input_kind == "passage"
        self.calls.append(texts)
        return EmbeddingBatch(tuple(Embedding((1.0, 0.0), False, 1.0) for _ in texts),
                              "model", "b" * 64, 2, SCOPE.embedding_space_id)


def test_projection_batches_and_keeps_exact_source_provenance():
    source = SourcePage("page", "x" * 4000)
    engine = Engine()
    result = prepare_document(SCOPE, [source], engine, max_chars=100, overlap_chars=0)
    assert [len(call) for call in engine.calls] == [32, 8]
    assert len(result.chunks) == 40 and result.dimension == 2
    assert all(item.chunk.text == source.text[item.chunk.start:item.chunk.end] for item in result.chunks)
    assert all(item.vector == (1.0, 0.0) for item in result.chunks)
    assert result == prepare_document(SCOPE, [source], Engine(), max_chars=100, overlap_chars=0)


def test_batch_size_also_respects_total_input_characters():
    engine = Engine()
    result = prepare_document(SCOPE, [SourcePage("p", "x" * 100000)], engine,
                              max_chars=8192, overlap_chars=0)
    assert len(result.chunks) == 13
    assert all(sum(len(text) for text in call) <= 65536 for call in engine.calls)


@pytest.mark.parametrize("mutation", ["space", "fingerprint", "dimension", "short", "truncated", "nan", "norm"])
def test_bad_or_inconsistent_embedding_batches_fail_without_projection(mutation):
    class InvalidEngine(Engine):
        def embed(self, texts, *, input_kind):
            batch = super().embed(texts, input_kind=input_kind)
            if mutation == "space":
                return replace(batch, space_id="c" * 64)
            if mutation == "fingerprint" and len(self.calls) > 1:
                return replace(batch, fingerprint="c" * 64)
            if mutation == "dimension":
                return replace(batch, dimension=3)
            if mutation == "short":
                return replace(batch, items=batch.items[:-1])
            if mutation == "truncated":
                return replace(batch, items=(replace(batch.items[0], truncated=True), *batch.items[1:]))
            if mutation == "nan":
                return replace(batch, items=(Embedding((float("nan"), 0), False, 1), *batch.items[1:]))
            if mutation == "norm":
                return replace(batch, items=(Embedding((.2, 0), False, 1), *batch.items[1:]))
            return batch
    with pytest.raises(ValueError):
        prepare_document(SCOPE, [SourcePage("p", "x" * 4000)], InvalidEngine(), max_chars=100, overlap_chars=0)


def test_empty_document_cannot_create_projection():
    with pytest.raises(ValueError, match="empty_document"):
        prepare_document(SCOPE, [SourcePage("p", "  \n")], Engine())
