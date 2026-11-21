"""Strict UTF-8 JSON decoding for signed worker request envelopes."""

from __future__ import annotations

import json
import math
from typing import Any


def _unique(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("duplicate_property")
        result[key] = value
    return result


def _finite_float(value: str) -> float:
    number = float(value)
    if not math.isfinite(number):
        raise ValueError("invalid_number")
    return number


def _invalid_constant(value: str) -> None:
    raise ValueError("invalid_number")


def parse_signed_json(body: bytes | bytearray) -> Any:
    """Reject duplicate keys and non-finite numbers at every nesting level."""
    return json.loads(body.decode("utf-8"), object_pairs_hook=_unique,
                      parse_float=_finite_float, parse_constant=_invalid_constant)
