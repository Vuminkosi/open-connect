import base64
import hmac
from unittest.mock import MagicMock

from openconnect_knowledge.cloud_consumption import KnowledgeEmbeddingClaimConsumer

ID = "11111111-1111-1111-1111-111111111111"


def test_exact_payload_is_bound_to_embedding_claim_without_retry(monkeypatch, tmp_path):
    context = MagicMock()
    monkeypatch.setattr("openconnect_worker_common.security.cloud_claims.ssl.SSLContext", lambda _: context)
    response = MagicMock(status=200)
    response.getheader.return_value = None
    response.read.return_value = b'{"status":"consumed"}'
    connection = MagicMock()
    connection.getresponse.return_value = response
    monkeypatch.setattr("openconnect_worker_common.security.cloud_claims.http.client.HTTPSConnection",
                        lambda *args, **kwargs: connection)
    consumer = KnowledgeEmbeddingClaimConsumer("https://authority.example", tmp_path / "ca", tmp_path / "cert", tmp_path / "key")
    payload, key = b"private payload", b"k" * 32
    assert consumer.consume(ID, ID, payload, key) == "consumed"
    proof = base64.b64encode(hmac.digest(key, payload, "sha256")).decode("ascii")
    connection.request.assert_called_once_with(
        "POST", f"/cloud/internal/v1/knowledge-embed/quota/reservations/{ID}/claims/{ID}/consume",
        body=b"", headers={"Cache-Control": "no-store", "X-OpenConnect-Worker-Payload-Fingerprint": proof},
    )
    connection.close.assert_called_once()
