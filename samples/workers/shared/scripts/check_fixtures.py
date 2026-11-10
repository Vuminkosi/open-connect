#!/usr/bin/env python3
"""T1 offline fixture validator: JSON parses + required top-level fields exist.

Usage: check_fixtures.py <fixture.json> <field1,field2,...>
Exits 0 on PASS, 1 on FAIL. No network, no docker, no secrets.
"""

import json
import sys


def main() -> int:
    if len(sys.argv) != 3:
        print(
            f"usage: {sys.argv[0]} <fixture.json> <field1,field2,...>", file=sys.stderr
        )
        return 2
    path, fieldspec = sys.argv[1], sys.argv[2]
    try:
        with open(path, encoding="utf-8") as fh:
            doc = json.load(fh)
    except (OSError, json.JSONDecodeError) as exc:
        print(f"FAIL {path}: unreadable JSON ({exc})")
        return 1
    if not isinstance(doc, dict):
        print(f"FAIL {path}: top-level JSON must be an object")
        return 1
    missing = [f for f in fieldspec.split(",") if f and f not in doc]
    if missing:
        print(f"FAIL {path}: missing required fields {missing}")
        return 1
    print(f"PASS {path}: valid JSON with {fieldspec}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
