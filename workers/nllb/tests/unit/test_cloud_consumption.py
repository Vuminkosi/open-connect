import base64
import hashlib
import hmac
from pathlib import Path
from unittest.mock import MagicMock
from uuid import uuid4

import pytest

from openconnect_nllb.cloud_consumption import CloudConsumptionClient


@pytest.fixture
def make_client(monkeypatch, tmp_path):
    context = MagicMock()
    monkeypatch.setattr("openconnect_nllb.cloud_consumption.ssl.SSLContext", lambda _: context)

    def create(origin="https://control.example:444", timeout=5, paths=None):
        return CloudConsumptionClient(origin, *(paths or (tmp_path / "ca", tmp_path / "cert", tmp_path / "key")), timeout=timeout)

    return create, context


def test_requires_https_pinned_files_and_bounded_timeout(make_client):
    create, context = make_client
    create()
    context.load_verify_locations.assert_called_once()
    context.load_cert_chain.assert_called_once()
    for origin in ("http://control.example", "https://user:pass@control.example", "https://control.example/path",
                   "https://control.example/?x=y", "https://control.example/#x", "https://control.example:0"):
        with pytest.raises(ValueError):
            create(origin)
    for timeout in (0, 31, True, float("nan")):
        with pytest.raises(ValueError):
            create(timeout=timeout)
    with pytest.raises(ValueError):
        create(paths=(Path("relative"), Path("relative"), Path("relative")))


@pytest.mark.parametrize(("status", "body", "encoding", "expected"), [
    (200, b'{"status":"consumed"}', None, "consumed"),
    (409, b'{"status":"already_consumed"}', None, "already_consumed"),
    (200, b'{"status":"already_consumed"}', None, "unavailable"),
    (200, b'{"status":"consumed","status":"consumed"}', None, "unavailable"),
    (200, b'{"status":"consumed","extra":1}', None, "unavailable"),
    (200, b'{"status":"consumed"}', "gzip", "unavailable"),
    (302, b'{"status":"consumed"}', None, "unavailable"),
    (200, b'x' * 4097, None, "unavailable"),
])
def test_consume_is_payload_bound_and_never_redirects(monkeypatch, make_client, status, body, encoding, expected):
    create, _ = make_client
    response = MagicMock(status=status)
    response.getheader.return_value = encoding
    response.read.return_value = body
    connection = MagicMock()
    connection.getresponse.return_value = response
    factory = MagicMock(return_value=connection)
    monkeypatch.setattr("openconnect_nllb.cloud_consumption.http.client.HTTPSConnection", factory)
    reservation, claim = str(uuid4()), str(uuid4())
    payload, key = b"translation fixture", b"k" * 32
    assert create().consume(reservation, claim, payload, key) == expected
    connection.request.assert_called_once_with(
        "POST", f"/cloud/internal/v1/translation/quota/reservations/{reservation}/claims/{claim}/consume",
        body=b"", headers={"Cache-Control": "no-store", "X-OpenConnect-Worker-Payload-Fingerprint":
                           base64.b64encode(hmac.digest(key, payload, hashlib.sha256)).decode("ascii")},
    )
    connection.close.assert_called_once()


def test_completion_and_readiness_validate_exact_responses(monkeypatch, make_client):
    create, _ = make_client
    response = MagicMock(status=200)
    response.getheader.return_value = None
    connection = MagicMock()
    connection.getresponse.return_value = response
    monkeypatch.setattr("openconnect_nllb.cloud_consumption.http.client.HTTPSConnection", lambda *args, **kwargs: connection)
    client = create()
    deployment = str(uuid4())
    response.read.return_value = ('{"status":"ready","deploymentId":"' + deployment + '"}').encode()
    assert client.ready_deployment() == deployment
    response.read.return_value = b'{"status":"completed"}'
    assert client.complete(str(uuid4()), str(uuid4())) == "completed"
    response.read.return_value = b'{"status":"completed","extra":true}'
    assert client.complete(str(uuid4()), str(uuid4())) == "unavailable"
    assert client.consume("invalid", str(uuid4()), b"x", b"k" * 32) == "unavailable"


def test_network_failure_is_unavailable_without_retry(monkeypatch, make_client):
    create, _ = make_client
    factory = MagicMock(side_effect=OSError("network error"))
    monkeypatch.setattr("openconnect_nllb.cloud_consumption.http.client.HTTPSConnection", factory)
    assert create().consume(str(uuid4()), str(uuid4()), b"payload", b"k" * 32) == "unavailable"
    factory.assert_called_once()
