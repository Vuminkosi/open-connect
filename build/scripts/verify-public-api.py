#!/usr/bin/env python3
"""Verify Roslyn public-API tracking files (CF-IMP-017, owned path).

Convention: library projects that opt into API compat tracking carry
`PublicAPI.Shipped.txt` / `PublicAPI.Unshipped.txt` (Roslyn RS0016/RS0017).
This script enforces file hygiene so the cross-framework-ci `contract` job
fails loudly on malformed tracking data instead of silently passing:

  * no duplicate entries within a file;
  * entries sorted (ordinal, case-sensitive, matching Roslyn analyzer order);
  * no trailing whitespace; file ends with exactly one newline;
  * no blank lines between entries.

If no tracking files exist yet (expected until CF-IMP-003 introduces them),
the script exits 0 and reports NOT APPLICABLE rather than failing.

Exit codes: 0 = PASS / NOT APPLICABLE, 1 = FAIL, 2 = usage error.
Stdlib only (argparse, pathlib, sys).
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

TRACKING_NAMES = ("PublicAPI.Shipped.txt", "PublicAPI.Unshipped.txt")


def check_file(path: Path) -> list[str]:
    errors: list[str] = []
    text = path.read_text(encoding="utf-8-sig")
    if "\t" in text:
        errors.append(f"{path}: tab characters are not allowed")
    lines = text.split("\n")
    if text and not text.endswith("\n"):
        errors.append(f"{path}: file must end with a single trailing newline")
    entries = [ln for ln in lines if ln.strip() != ""]
    if len(lines) - 1 > len(entries) + (0 if text.endswith("\n") else 0):
        # More blank lines than the single terminal newline allows.
        errors.append(f"{path}: blank lines between entries are not allowed")
    stripped = [ln for ln in entries]
    for ln in stripped:
        if ln != ln.rstrip():
            errors.append(f"{path}: trailing whitespace in entry: {ln!r}")
            break
    clean = [ln.rstrip() for ln in stripped]
    if len(set(clean)) != len(clean):
        dupes = sorted({ln for ln in clean if clean.count(ln) > 1})
        errors.append(f"{path}: duplicate entries: {dupes}")
    if clean != sorted(clean):
        errors.append(f"{path}: entries are not in ordinal sorted order")
    return errors


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description="Verify PublicAPI tracking file hygiene.")
    ap.add_argument("--src", default="src", help="Source tree to scan (default: src)")
    args = ap.parse_args(argv)

    src = Path(args.src)
    if not src.is_dir():
        print(f"ERROR: source tree not found: {src}", file=sys.stderr)
        return 2

    files: list[Path] = []
    for name in TRACKING_NAMES:
        files.extend(sorted(src.rglob(name)))
    if not files:
        print(
            "NOT APPLICABLE: no PublicAPI.Shipped/Unshipped.txt files found "
            "(expected until CF-IMP-003 introduces API tracking)."
        )
        return 0

    errors: list[str] = []
    for f in files:
        errs = check_file(f)
        if errs:
            errors.extend(errs)
        else:
            print(f"PASS: {f.as_posix()}")
    if errors:
        print("Public API verification FAILED:", file=sys.stderr)
        for e in errors:
            print(f"  - {e}", file=sys.stderr)
        return 1
    print(f"Public API verification PASSED ({len(files)} file(s)).")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
