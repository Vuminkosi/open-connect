from uuid import uuid4

from openconnect_knowledge.chunking import Chunk, DocumentScope
from openconnect_knowledge.app import HybridQuery, Payload, RerankCandidate, SearchPayload
from openconnect_knowledge.index_jobs import IndexClaim, IndexJob
from openconnect_knowledge.index_preparation import PreparedChunk, PreparedDocument
from openconnect_knowledge.image_index import ImageHit
from openconnect_knowledge.outbox import Claim
from openconnect_knowledge.postgres_index import JobFence, SearchHit, SearchScope
from openconnect_knowledge.reconciliation import ReconciliationEvent


def test_index_objects_do_not_expose_content_vectors_identifiers_or_lease_tokens_in_repr():
    secret = "private-document-content"
    token = uuid4()
    scope = DocumentScope(secret, "app", "collection", "doc", "revision", "a" * 64)
    chunk = Chunk("b" * 64, "page", 0, 0, len(secret), secret, "c" * 64)
    prepared = PreparedChunk(chunk, (0.123456789,))
    document = PreparedDocument(scope, "model", "d" * 64, 1, (prepared,))
    job = IndexJob(scope, "e" * 64, "f" * 64, 42, "version", 0, (secret,))
    claim = IndexClaim(secret, uuid4(), token, 1, 1, {"text": secret})
    fence = JobFence(uuid4(), token, 1)
    search_scope = SearchScope(secret, "app", "collection", secret)
    hit = SearchHit(secret, "revision", "b" * 64, "page", 0, 0, len(secret), secret, 0.5)
    outbox_claim = Claim(1, secret, "app", "collection", "doc", 1, "delete", token, 1)
    event = ReconciliationEvent(1, secret, "app", "collection", "doc", 1, "delete")
    image_hit = ImageHit(secret, secret, secret, 0.5)
    for value in (prepared, document, job, claim, fence, search_scope, hit, outbox_claim, event, image_hit):
        representation = repr(value)
        assert secret not in representation
        assert "0.123456789" not in representation
        assert str(token) not in representation


def test_request_models_hide_sensitive_inputs_from_diagnostics_but_still_serialize():
    secret = "private-customer-value"
    vector = [0.123456789]
    hybrid = HybridQuery(vector=vector, embeddingSpaceId="a" * 64, modelId="model", fingerprint="b" * 64)
    payload = Payload(inputKind="query", texts=[secret])
    candidate = RerankCandidate(id=secret, text=secret)
    search = SearchPayload(applicationId=secret, collectionId=secret, principalId=secret,
                           roleIds=[secret], query=secret, hybrid=hybrid)
    for value in (payload, candidate, hybrid, search):
        assert secret not in repr(value) and secret not in str(value)
        assert "0.123456789" not in repr(value) and "0.123456789" not in str(value)
    assert payload.model_dump()["texts"] == [secret]
    assert search.model_dump()["hybrid"]["vector"] == vector
