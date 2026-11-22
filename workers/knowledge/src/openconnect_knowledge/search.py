"""Host-authorized retrieval followed by bounded, local candidate reranking."""

from __future__ import annotations

import math
from dataclasses import dataclass, replace
from typing import Sequence

from openconnect_knowledge.postgres_index import PostgresKnowledgeIndex, SearchHit, SearchScope, _acl
from openconnect_knowledge.reranker import Candidate, OnnxReranker


@dataclass(frozen=True)
class RerankedHit:
    hit: SearchHit
    rerank_score: float


@dataclass(frozen=True)
class SearchResult:
    hits: tuple[RerankedHit, ...]
    model_id: str | None
    fingerprint: str | None
    score_type: str
    warnings: tuple[str, ...]


def search_and_rerank(store: PostgresKnowledgeIndex, reranker: OnnxReranker, scope: SearchScope,
                      query: str, *, limit: int = 10, vector: Sequence[float] | None = None,
                      embedding_space_id: str | None = None, model_id: str | None = None,
                      fingerprint: str | None = None) -> SearchResult:
    """Use only ACL-prefiltered local hits; recheck their access after scoring.

    The trusted host authenticates the principal and roles, authorizes the
    tenant and query, and applies the privacy gate before invoking this function.
    The reranker must be local and trusted with retrieved content.
    """
    if isinstance(limit, bool) or not isinstance(limit, int) or not 1 <= limit <= 32:
        raise ValueError("invalid_search")
    scope = replace(scope, role_ids=tuple(_acl(scope.role_ids)))
    if vector is None:
        if any(value is not None for value in (embedding_space_id, model_id, fingerprint)):
            raise ValueError("invalid_search")
        hits = store.search(scope, query, limit=limit)
    else:
        if any(value is None for value in (embedding_space_id, model_id, fingerprint)):
            raise ValueError("invalid_search")
        hits = store.hybrid_search(scope, query, vector, embedding_space_id=embedding_space_id,
                                   model_id=model_id, fingerprint=fingerprint, limit=limit)
    if not hits:
        return SearchResult((), None, None, "raw_logit", ())
    # Opaque, request-local identifiers avoid assuming chunk IDs are globally unique.
    ranking = reranker.rank(query, [Candidate(str(index), hit.text) for index, hit in enumerate(hits)])
    ids = [item.id for item in ranking.candidates]
    if (len(ids) != len(hits) or set(ids) != {str(index) for index in range(len(hits))}
            or len(set(ids)) != len(ids) or any(not math.isfinite(item.score) for item in ranking.candidates)
            or ranking.score_type != "raw_logit"):
        raise RuntimeError("rerank_result_invalid")
    valid = (store.revalidate(scope, hits) if vector is None else
             store.revalidate(scope, hits, embedding_space_id=embedding_space_id,
                              model_id=model_id, fingerprint=fingerprint))
    result = tuple(RerankedHit(hits[int(item.id)], item.score) for item in ranking.candidates
                   if (hits[int(item.id)].document_id, hits[int(item.id)].chunk_id) in valid)
    warnings = ("access_or_source_changed",) if len(result) != len(hits) else ()
    return SearchResult(result, ranking.model_id, ranking.fingerprint, ranking.score_type, warnings)
