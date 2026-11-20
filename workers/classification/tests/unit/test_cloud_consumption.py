from pathlib import Path
from unittest.mock import MagicMock

import pytest

from openconnect_classification.cloud_consumption import CloudConsumptionClient

RESERVATION = "11111111-1111-1111-1111-111111111111"
CLAIM = "22222222-2222-2222-2222-222222222222"


@pytest.fixture
def make_client(monkeypatch, tmp_path):
    context = MagicMock()
    monkeypatch.setattr("openconnect_classification.cloud_consumption.ssl.SSLContext", lambda _: context)

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
        (200, b'{"status":"consumed"}', None, "consumed"),
        (409, b'{"status":"already_consumed"}', None, "already_consumed"),
        (200, b'{"status":"already_consumed"}', None, "unavailable"),
        (200, b'{"status":"consumed","status":"consumed"}', None, "unavailable"),
        (200, b'{"status":"consumed","extra":1}', None, "unavailable"),
        (200, b'{"status":"consumed"}', "gzip", "unavailable"),
        (302, b'{"status":"consumed"}', None, "unavailable"),
        (200, b"x" * 4097, None, "unavailable"),
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
    monkeypatch.setattr("openconnect_classification.cloud_consumption.http.client.HTTPSConnection", factory)
    assert create().consume(RESERVATION, CLAIM) == expected
    factory.assert_called_once()
    connection.request.assert_called_once_with(
        "POST", f"/cloud/internal/v1/classification/quota/reservations/{RESERVATION}/claims/{CLAIM}/consume",
        body=b"", headers={"Cache-Control": "no-store"},
    )
    connection.close.assert_called_once()


def test_invalid_ids_and_ambiguous_network_failure_never_retry(monkeypatch, make_client):
    create, _ = make_client
    connection = MagicMock()
    connection.getresponse.side_effect = OSError("private diagnostic")
    factory = MagicMock(return_value=connection)
    monkeypatch.setattr("openconnect_classification.cloud_consumption.http.client.HTTPSConnection", factory)
    client = create()
    assert client.consume("not-a-uuid", CLAIM) == "unavailable"
    assert client.consume(RESERVATION, "00000000-0000-0000-0000-000000000000") == "unavailable"
    factory.assert_not_called()
    assert client.consume(RESERVATION, CLAIM) == "unavailable"
    connection.request.assert_called_once()
    connection.close.assert_called_once()
