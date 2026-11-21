import base64
import hmac
from pathlib import Path
from unittest.mock import MagicMock

import pytest

from openconnect_kokoro.cloud_consumption import CloudConsumptionClient

RESERVATION = "11111111-1111-1111-1111-111111111111"
CLAIM = "22222222-2222-2222-2222-222222222222"


@pytest.fixture
def make_client(monkeypatch, tmp_path):
    context = MagicMock()
    monkeypatch.setattr("openconnect_kokoro.cloud_consumption.ssl.SSLContext", lambda _: context)
    def create(origin="https://control.example:444", timeout=5, paths=None):
        return CloudConsumptionClient(origin, *(paths or (tmp_path / "ca", tmp_path / "cert", tmp_path / "key")), timeout=timeout)
    return create, context


def test_requires_tls_pins_absolute_paths_and_bounded_timeout(make_client):
    create, context = make_client
    create()
    context.load_verify_locations.assert_called_once()
    context.load_cert_chain.assert_called_once()
    for origin in ("http://control.example", "https://user:pass@control.example", "https://control.example/path",
                   "https://control.example/?next=x", "https://control.example/#x", "https://control.example:0"):
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
def test_claim_consumption_is_bound_bounded_and_never_redirects(monkeypatch, make_client, status, body, encoding, expected):
    create, _ = make_client
    response = MagicMock(status=status)
    response.getheader.return_value = encoding
    response.read.return_value = body
    connection = MagicMock()
    connection.getresponse.return_value = response
    factory = MagicMock(return_value=connection)
    monkeypatch.setattr("openconnect_kokoro.cloud_consumption.http.client.HTTPSConnection", factory)
    payload, key = b"speech fixture", b"k" * 32
    assert create().consume(RESERVATION, CLAIM, payload, key) == expected
    connection.request.assert_called_once_with(
        "POST", f"/cloud/internal/v1/voice-synthesis/quota/reservations/{RESERVATION}/claims/{CLAIM}/consume",
        body=b"", headers={"Cache-Control": "no-store", "X-OpenConnect-Worker-Payload-Fingerprint":
                           base64.b64encode(hmac.digest(key, payload, "sha256")).decode("ascii")})
    connection.close.assert_called_once()


def test_invalid_claim_and_network_failure_never_retry(monkeypatch, make_client):
    create, _ = make_client
    connection = MagicMock()
    connection.getresponse.side_effect = OSError("private diagnostic")
    factory = MagicMock(return_value=connection)
    monkeypatch.setattr("openconnect_kokoro.cloud_consumption.http.client.HTTPSConnection", factory)
    client = create()
    assert client.consume("invalid", CLAIM, b"fixture", b"k" * 32) == "unavailable"
    assert client.consume(RESERVATION, CLAIM, b"fixture", b"short") == "unavailable"
    factory.assert_not_called()
    assert client.consume(RESERVATION, CLAIM, b"fixture", b"k" * 32) == "unavailable"
    factory.assert_called_once()
    connection.close.assert_called_once()


@pytest.mark.parametrize(("status", "body", "encoding", "expected"), [
    (200, b'{"status":"completed"}', None, "completed"),
    (200, b'{"status":"already_completed"}', None, "already_completed"),
    (200, b'{"status":"completed","status":"completed"}', None, "unavailable"),
    (200, b'{"status":"completed"}', "gzip", "unavailable"),
    (409, b'{"status":"completed"}', None, "unavailable"),
    (200, b'x' * 4097, None, "unavailable"),
])
def test_completion_requires_bounded_exact_response(monkeypatch, make_client, status, body, encoding, expected):
    create, _ = make_client
    response = MagicMock(status=status)
    response.getheader.return_value = encoding
    response.read.return_value = body
    connection = MagicMock()
    connection.getresponse.return_value = response
    factory = MagicMock(return_value=connection)
    monkeypatch.setattr("openconnect_kokoro.cloud_consumption.http.client.HTTPSConnection", factory)
    assert create().complete(RESERVATION, CLAIM) == expected
    factory.assert_called_once()
    connection.request.assert_called_once_with(
        "POST", f"/cloud/internal/v1/voice-synthesis/quota/reservations/{RESERVATION}/claims/{CLAIM}/complete",
        body=b"", headers={"Cache-Control": "no-store"})
    connection.close.assert_called_once()
