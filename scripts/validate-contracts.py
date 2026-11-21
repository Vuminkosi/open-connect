#!/usr/bin/env python3
"""Validate OpenConnect JSON Schema documents and their shared fixture corpus."""

from __future__ import annotations

import argparse
import json
import math
import sys
from pathlib import Path
from typing import Any

try:
    from jsonschema import Draft202012Validator, FormatChecker
except ImportError as exc:  # pragma: no cover - environment diagnostic
    raise SystemExit(
        "Missing jsonschema dependency. Install jsonschema>=4.18 to validate contracts."
    ) from exc


ROOT = Path(__file__).resolve().parents[1]
SCHEMA_DIR = ROOT / "contracts" / "jsonschema"
FIXTURE_DIR = ROOT / "contracts" / "fixtures"


def load(path: Path) -> Any:
    with path.open(encoding="utf-8") as stream:
        return json.load(stream)


def describe_error(error: Any) -> str:
    """Format schema and cross-field validation errors consistently."""
    return error if isinstance(error, str) else error.message


def semantic_errors(schema: dict[str, Any], instance: Any) -> list[str]:
    """Check cross-field invariants that JSON Schema cannot express portably."""
    if schema.get("$id", "").endswith("cloud-knowledge-embed-response-v1.json") and isinstance(instance, dict):
        result = instance.get("result")
        if not isinstance(result, dict) or not isinstance(result.get("items"), list):
            return []
        errors = []
        for index, item in enumerate(result["items"]):
            if not isinstance(item, dict) or not isinstance(item.get("vector"), list):
                continue
            vector = item["vector"]
            if len(vector) != result.get("dimension"):
                errors.append(f"item {index}: vector dimension does not match")
                continue
            try:
                if any(isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value) for value in vector):
                    errors.append(f"item {index}: vector contains non-finite values")
                    continue
                norm = math.sqrt(sum(value * value for value in vector))
                reported = item.get("norm")
                if (isinstance(reported, bool) or not isinstance(reported, (int, float)) or not math.isfinite(reported)
                        or not math.isfinite(norm) or abs(norm - 1) > 0.001 or abs(reported - norm) > 0.001):
                    errors.append(f"item {index}: vector norm is invalid")
            except (OverflowError, ValueError):
                errors.append(f"item {index}: vector norm is invalid")
        return errors
    if schema.get("$id", "").endswith("security-scan-response.json") and isinstance(
        instance, dict
    ):
        spans = instance.get("spans", [])
        if isinstance(spans, list):
            return [
                f"span {index}: start must be less than end"
                for index, span in enumerate(spans)
                if isinstance(span, dict)
                and isinstance(span.get("start"), int)
                and not isinstance(span.get("start"), bool)
                and isinstance(span.get("end"), int)
                and not isinstance(span.get("end"), bool)
                and span["start"] >= span["end"]
            ]
    return []


def validate_fixture_set(folder: Path, should_be_valid: bool) -> tuple[int, int]:
    count = 0
    failures = 0
    for fixture in sorted(folder.glob("*.json")):
        schema_path = SCHEMA_DIR / fixture.name
        if not schema_path.exists():
            print(
                f"FAIL {fixture.relative_to(ROOT)}: no matching schema {schema_path.name}"
            )
            count += 1
            failures += 1
            continue
        schema = load(schema_path)
        instance = load(fixture)
        errors = list(
            Draft202012Validator(schema, format_checker=FormatChecker()).iter_errors(
                instance
            )
        )
        errors.extend(semantic_errors(schema, instance))
        passed = not errors if should_be_valid else bool(errors)
        if not passed:
            failures += 1
            detail = (
                "; ".join(describe_error(error) for error in errors[:3])
                or "fixture unexpectedly validates"
            )
            print(f"FAIL {fixture.relative_to(ROOT)}: {detail}")
        else:
            print(f"PASS {fixture.relative_to(ROOT)}")
        count += 1
    return count, failures


def compatibility_check(baseline: Path) -> bool:
    """Ensure this revision still accepts the valid fixture corpus saved at baseline."""
    old_fixtures = baseline / "valid"
    if not old_fixtures.is_dir():
        print(f"Compatibility baseline must contain valid/*.json: {baseline}")
        return False
    count = 0
    for fixture in sorted(old_fixtures.glob("*.json")):
        schema_path = SCHEMA_DIR / fixture.name
        if not schema_path.exists():
            print(f"FAIL compatibility: removed schema for {fixture.name}")
            return False
        schema = load(schema_path)
        instance = load(fixture)
        errors = list(
            Draft202012Validator(schema, format_checker=FormatChecker()).iter_errors(
                instance
            )
        )
        errors.extend(semantic_errors(schema, instance))
        if errors:
            print(
                f"FAIL compatibility: {fixture.name} no longer validates: {describe_error(errors[0])}"
            )
            return False
        count += 1
    print(f"PASS compatibility: {count} baseline valid fixtures remain accepted")
    return True


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--compat-baseline",
        type=Path,
        help="directory containing a prior valid/*.json corpus",
    )
    args = parser.parse_args()

    for schema_path in sorted(SCHEMA_DIR.glob("*.json")):
        Draft202012Validator.check_schema(load(schema_path))
        print(f"PASS schema {schema_path.relative_to(ROOT)}")
    valid_count, valid_failures = validate_fixture_set(FIXTURE_DIR / "valid", True)
    invalid_count, invalid_failures = validate_fixture_set(FIXTURE_DIR / "invalid", False)
    if valid_count == 0 or invalid_count == 0:
        print("FAIL: both fixture classes must contain at least one fixture")
        return 1
    if valid_failures or invalid_failures:
        print(f"FAIL: {valid_failures + invalid_failures} fixture checks failed")
        return 1
    if args.compat_baseline and not compatibility_check(args.compat_baseline):
        return 1
    print(f"Validated {valid_count} valid and {invalid_count} invalid fixtures.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
