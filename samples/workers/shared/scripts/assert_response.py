#!/usr/bin/env python3
"""T3 response assertion: body JSON contains the fields declared in expected.json.

Usage: assert_response.py <body.json> <expected.json>
Env EXPECTED_STATUS must equal expected.json's expectedStatus (checked by caller).
Exits 0 on PASS, 1 on FAIL. Response files must already be sanitized by the caller.
"""

import json
import os
import sys


def main() -> int:
    if len(sys.argv) != 3:
        print(f"usage: {sys.argv[0]} <body.json> <expected.json>", file=sys.stderr)
        return 2
    body_path, expected_path = sys.argv[1], sys.argv[2]
    try:
        with open(body_path, encoding="utf-8") as fh:
            body = json.load(fh)
        with open(expected_path, encoding="utf-8") as fh:
            expected = json.load(fh)
    except (OSError, json.JSONDecodeError) as exc:
        print(f"FAIL: unreadable JSON ({exc})")
        return 1
    want_status = expected.get("expectedStatus")
    got_status = os.environ.get("EXPECTED_STATUS")
    if got_status is not None and str(want_status) != str(got_status):
        print(f"FAIL: expected HTTP {want_status}, got {got_status}")
        return 1
    if not isinstance(body, dict):
        print("FAIL: response body must be a JSON object")
        return 1
    missing = [f for f in expected.get("requiredResponseFields", []) if f not in body]
    if missing:
        print(f"FAIL: response missing required fields {missing}")
        return 1
    print(
        f"PASS: response has required fields {expected.get('requiredResponseFields', [])}"
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
