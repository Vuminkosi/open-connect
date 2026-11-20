from pathlib import Path
from unittest.mock import MagicMock
import base64
import hmac

import pytest

from openconnect_inference.cloud_consumption import CloudConsumptionClient

RESERVATION = "11111111-1111-1111-1111-111111111111"
CLAIM = "22222222-2222-2222-2222-222222222222"
PAYLOAD = b"worker fixture"
KEY = b"k" * 32


@pytest.fixture
def make_client(monkeypatch, tmp_path):
    context = MagicMock()
    monkeypatch.setattr("openconnect_inference.cloud_consumption.ssl.SSLContext", lambda _: context)

    def create(origin="https://control.example:444", timeout=5, paths=None):
        files = paths or (tmp_path / "ca", tmp_path / "cert", tmp_path / "key")
        return CloudConsumptionClient(origin, *files, timeout=timeout)

    return create, context


def test_rejects_unsafe_configuration_and_loads_private_tls_material(make_client):
    create, context = make_client
    create()
    context.load_verify_locations.assert_called_once()
    context.load_cert_chain.assert_called_once()
    for origin in ("http://control.example", "https://user:secret@control.example", "https://control.example/path",
                   "https://control.example/?next=other", "https://control.example/#fragment", "https://control.example:0"):
        with pytest.raises(ValueError):
            create(origin)
    for timeout in (0, 31, True, float("nan")):
        with pytest.raises(ValueError):
            create(timeout=timeout)
    with pytest.raises(ValueError):
        create(paths=(Path("relative"), Path("relative"), Path("relative")))


@pytest.mark.parametrize(
    ("status", "body", "encoding", "expected"),
    [
        (200, (b'{"status":"consumed","reservedTokens":7,"modelId":"registry/model-a","modelRevision":"revision-1","modelDigest":"sha256:' + b'a' * 64 + b'"}'), None, {"status": "consumed", "reservedTokens": 7, "modelId": "registry/model-a", "modelRevision": "revision-1", "modelDigest": "sha256:" + "a" * 64}),
        (409, b'{"status":"already_consumed"}', None, None),
        (200, b'{"status":"already_consumed"}', None, None),
        (200, b'{"status":"consumed","status":"consumed"}', None, None),
        (200, b'{"status":"consumed","extra":1}', None, None),
        (200, b'{"status":"consumed"}', "gzip", None),
        (302, b'{"status":"consumed"}', None, None),
        (200, b"x" * 4097, None, None),
    ],
)
def test_response_is_bounded_and_never_follows_redirects(monkeypatch, make_client, status, body, encoding, expected):
    create, _ = make_client
    response = MagicMock(status=status)
    response.getheader.return_value = encoding
    response.read.return_value = body
    connection = MagicMock()
    connection.getresponse.return_value = response
    factory = MagicMock(return_value=connection)
    monkeypatch.setattr("openconnect_inference.cloud_consumption.http.client.HTTPSConnection", factory)
    assert create().consume(RESERVATION, CLAIM, PAYLOAD, KEY) == expected
    factory.assert_called_once()
    connection.request.assert_called_once_with(
        "POST", f"/cloud/internal/v1/inference/quota/reservations/{RESERVATION}/claims/{CLAIM}/consume",
        body=b"", headers={"Cache-Control": "no-store", "X-OpenConnect-Worker-Payload-Fingerprint":
                           base64.b64encode(hmac.digest(KEY, PAYLOAD, "sha256")).decode("ascii")},
    )
    connection.close.assert_called_once()


def test_invalid_ids_and_ambiguous_network_failure_never_retry(monkeypatch, make_client):
    create, _ = make_client
    connection = MagicMock()
    connection.getresponse.side_effect = OSError("private diagnostic")
    factory = MagicMock(return_value=connection)
    monkeypatch.setattr("openconnect_inference.cloud_consumption.http.client.HTTPSConnection", factory)
    client = create()
    assert client.consume("not-a-uuid", CLAIM, PAYLOAD, KEY) is None
    assert client.consume(RESERVATION, "00000000-0000-0000-0000-000000000000", PAYLOAD, KEY) is None
    assert client.consume(RESERVATION, CLAIM, PAYLOAD, b"short") is None
    factory.assert_not_called()
    assert client.consume(RESERVATION, CLAIM, PAYLOAD, KEY) is None
    connection.request.assert_called_once()
    connection.close.assert_called_once()


def test_readiness_requires_bounded_ready_response(monkeypatch, make_client):
    create, _ = make_client
    response = MagicMock(status=200)
    response.getheader.return_value = None
    response.read.return_value = b'{"status":"ready","deploymentId":"11111111-1111-1111-1111-111111111111"}'
    connection = MagicMock()
    connection.getresponse.return_value = response
    monkeypatch.setattr("openconnect_inference.cloud_consumption.http.client.HTTPSConnection", lambda *args, **kwargs: connection)
    client = create()
    assert client.ready_deployment() == RESERVATION
    response.read.return_value = b'{"status":"ready","status":"ready"}'
    assert client.ready_deployment() is None
    response.status = 503
    assert client.ready_deployment() is None


@pytest.mark.parametrize(("status", "body", "expected"), [
    (200, b'{"status":"completed"}', True),
    (200, b'{"status":"already_completed"}', True),
    (409, b'{"status":"overage_requires_reconciliation"}', False),
    (200, b'{"status":"completed","status":"completed"}', False),
    (200, b'x' * 4097, False),
])
def test_settlement_requires_bounded_success(monkeypatch, make_client, status, body, expected):
    create, _ = make_client
    response = MagicMock(status=status)
    response.getheader.return_value = None
    response.read.return_value = body
    connection = MagicMock()
    connection.getresponse.return_value = response
    monkeypatch.setattr("openconnect_inference.cloud_consumption.http.client.HTTPSConnection", lambda *args, **kwargs: connection)
    assert create().settle(RESERVATION, CLAIM, 3, 2) is expected
    connection.request.assert_called_once()
    connection.close.assert_called_once()
