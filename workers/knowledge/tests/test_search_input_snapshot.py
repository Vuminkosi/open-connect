from itertools import repeat

import pytest

from openconnect_knowledge.postgres_index import SearchHit, SearchScope, _acl, _search_vector
from openconnect_knowledge.reranker import RankedCandidate, Ranking
from openconnect_knowledge.search import search_and_rerank
from openconnect_knowledge.image_index import _vector as _image_vector


class ChangingValues:
    def __init__(self, first, second):
        self.first = first
        self.second = second
        self.iterations = 0

    def __iter__(self):
        self.iterations += 1
        return iter(self.first if self.iterations == 1 else self.second)


def test_acl_is_validated_and_used_from_one_snapshot():
    values = ChangingValues(["reader"], ["administrator"])
    assert _acl(values) == ["reader"]
    assert values.iterations == 1


def test_vector_is_validated_and_used_from_one_snapshot():
    values = ChangingValues([1.0, 0.0], [float("nan"), 0.0])
    assert _search_vector(values) == [1.0, 0.0]
    assert values.iterations == 1


@pytest.mark.parametrize("operation", [_search_vector, _image_vector])
def test_numeric_values_are_converted_once_before_validation(operation):
    class ChangingFloat(float):
        calls = 0

        def __float__(self):
            self.calls += 1
            return 1.0 if self.calls == 1 else float("nan")

    value = ChangingFloat(1.0)
    assert operation([value, 0.0]) == [1.0, 0.0]
    assert value.calls == 1


@pytest.mark.parametrize("operation", [_search_vector, _image_vector])
def test_converted_nonfinite_numeric_values_are_rejected(operation):
    class NonfiniteFloat(float):
        def __float__(self):
            return float("nan")

    with pytest.raises(ValueError):
        operation([NonfiniteFloat(1.0), 0.0])


@pytest.mark.parametrize(("operation", "value"), [(_acl, "reader"), (_search_vector, 0.0)])
def test_snapshots_are_bounded(operation, value):
    with pytest.raises(ValueError):
        operation(repeat(value))


def test_reranking_rechecks_the_same_role_snapshot():
    roles = ["reader"]
    scope = SearchScope("tenant", "app", "collection", "principal", roles)
    hit = SearchHit("doc", "v1", "a" * 64, "page", 0, 0, 6, "forest", 1.0)
    observed = []

    class Store:
        def search(self, received, query, *, limit):
            observed.append(received.role_ids)
            return (hit,)

        def revalidate(self, received, hits):
            observed.append(received.role_ids)
            return frozenset({("doc", "a" * 64)})

    class Reranker:
        def rank(self, query, candidates):
            roles[0] = "administrator"
            return Ranking((RankedCandidate(candidates[0].id, 0.5),), "fixture", "b" * 64)

    result = search_and_rerank(Store(), Reranker(), scope, "forest")
    assert len(result.hits) == 1
    assert observed == [("reader",), ("reader",)]


def test_hybrid_reranking_revalidates_the_original_model_identity():
    scope = SearchScope("tenant", "app", "collection", "principal")
    hit = SearchHit("doc", "v1", "a" * 64, "page", 0, 0, 6, "forest", 1.0)
    space, fingerprint = "b" * 64, "c" * 64

    class Store:
        def hybrid_search(self, received, query, vector, **options):
            assert options["embedding_space_id"] == space
            return (hit,)

        def revalidate(self, received, hits, **identity):
            assert identity == {"embedding_space_id": space, "model_id": "model", "fingerprint": fingerprint}
            return frozenset()

    class Reranker:
        def rank(self, query, candidates):
            return Ranking((RankedCandidate(candidates[0].id, 0.5),), "fixture", "d" * 64)

    result = search_and_rerank(Store(), Reranker(), scope, "forest", vector=(1.0, 0.0),
                               embedding_space_id=space, model_id="model", fingerprint=fingerprint)
    assert result.hits == ()
    assert result.warnings == ("access_or_source_changed",)
