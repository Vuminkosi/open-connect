import base64
import hmac
from pathlib import Path
from unittest.mock import MagicMock

import pytest

from openconnect_analytics_inference.cloud_consumption import AnalyticsClaimConsumer

ID = "11111111-1111-1111-1111-111111111111"


@pytest.fixture
def make_client(monkeypatch, tmp_path):
    context = MagicMock()
    monkeypatch.setattr("openconnect_worker_common.security.cloud_claims.ssl.SSLContext", lambda _: context)
    return lambda origin="https://authority.example", timeout=5: AnalyticsClaimConsumer(
        origin, tmp_path / "ca", tmp_path / "cert", tmp_path / "key", timeout
    )


def test_configuration_requires_absolute_mtls_paths_and_https(make_client):
    make_client()
    for origin in ("http://authority.example", "https://user@host", "https://host/path", "https://host:0"):
        with pytest.raises(ValueError):
            make_client(origin)
    for timeout in (True, 0, 31, float("nan")):
        with pytest.raises(ValueError):
            make_client(timeout=timeout)
    with pytest.raises(ValueError):
        AnalyticsClaimConsumer("https://authority.example", Path("ca"), Path("cert"), Path("key"))


@pytest.mark.parametrize(("status", "body", "encoding", "expected"), [
    (200, b'{"status":"consumed"}', None, "consumed"),
    (409, b'{"status":"already_consumed"}', None, "already_consumed"),
    (200, b'{"status":"consumed","status":"consumed"}', None, "unavailable"),
    (200, b'{"status":"consumed","extra":1}', None, "unavailable"),
    (200, b'{"status":"consumed"}', "gzip", "unavailable"),
    (302, b'{"status":"consumed"}', None, "unavailable"),
    (200, b'x' * 4097, None, "unavailable"),
])
def test_claim_proof_and_response_validation(monkeypatch, make_client, status, body, encoding, expected):
    response = MagicMock(status=status)
    response.getheader.return_value = encoding
    response.read.return_value = body
    connection = MagicMock()
    connection.getresponse.return_value = response
    monkeypatch.setattr("openconnect_worker_common.security.cloud_claims.http.client.HTTPSConnection",
                        lambda *args, **kwargs: connection)
    payload, key = b"private payload", b"k" * 32
    assert make_client().consume(ID, ID, payload, key) == expected
    proof = base64.b64encode(hmac.digest(key, payload, "sha256")).decode("ascii")
    connection.request.assert_called_once_with(
        "POST", f"/cloud/internal/v1/analytics/quota/reservations/{ID}/claims/{ID}/consume",
        body=b"", headers={"Cache-Control": "no-store", "X-OpenConnect-Worker-Payload-Fingerprint": proof},
    )
    connection.close.assert_called_once()


def test_invalid_proof_and_ambiguous_failure_are_never_retried(monkeypatch, make_client):
    connection = MagicMock()
    connection.getresponse.side_effect = OSError("private diagnostic")
    factory = MagicMock(return_value=connection)
    monkeypatch.setattr("openconnect_worker_common.security.cloud_claims.http.client.HTTPSConnection", factory)
    client = make_client()
    assert client.consume("invalid", ID, b"payload", bytes(32)) == "unavailable"
    assert client.consume(ID, ID, b"payload", b"short") == "unavailable"
    factory.assert_not_called()
    assert client.consume(ID, ID, b"payload", bytes(32)) == "unavailable"
    connection.request.assert_called_once()
    connection.close.assert_called_once()


def test_readiness_requires_an_authority_bound_deployment(monkeypatch, make_client):
    response = MagicMock(status=200)
    response.getheader.return_value = None
    response.read.return_value = ('{"status":"ready","deploymentId":"' + ID + '"}').encode()
    connection = MagicMock()
    connection.getresponse.return_value = response
    monkeypatch.setattr("openconnect_worker_common.security.cloud_claims.http.client.HTTPSConnection",
                        lambda *args, **kwargs: connection)
    client = make_client()
    assert client.ready_deployment() == ID
    response.read.return_value = b'{"status":"ready","status":"ready"}'
    assert client.ready_deployment() is None
