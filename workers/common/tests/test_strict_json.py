import pytest

from openconnect_worker_common.contracts.strict_json import parse_signed_json


@pytest.mark.parametrize("body", [
    b"{\"a\":1,\"a\":2}", b"{\"nested\":{\"a\":1,\"a\":2}}",
    b"{\"a\":NaN}", b"{\"a\":Infinity}", b"{\"a\":1e999}", b"\xff",
])
def test_rejects_ambiguous_or_invalid_json(body):
    with pytest.raises(ValueError):
        parse_signed_json(body)


def test_accepts_valid_nested_utf8_json():
    assert parse_signed_json('{"name":"café","numbers":[1,-2.5]}'.encode()) == {
        "name": "café", "numbers": [1, -2.5]
    }
