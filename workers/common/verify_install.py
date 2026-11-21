"""Check that the installed shared worker package matches this checkout."""

from __future__ import annotations

from pathlib import Path

import openconnect_worker_common


def differences(source: Path, installed: Path) -> list[str]:
    """Compare Python sources without importing their individual modules."""
    source_files = {path.relative_to(source) for path in source.rglob("*.py")}
    installed_files = {path.relative_to(installed) for path in installed.rglob("*.py")}
    if not source_files or not installed_files:
        return ["package source or installation is empty"]
    mismatches = source_files ^ installed_files
    mismatches.update(path for path in source_files & installed_files
                      if (source / path).read_bytes() != (installed / path).read_bytes())
    return sorted(path.as_posix() for path in mismatches)


def main() -> int:
    source = Path(__file__).resolve().parent / "src" / "openconnect_worker_common"
    installed = Path(openconnect_worker_common.__file__).resolve().parent
    mismatches = differences(source, installed)
    if mismatches:
        print("Shared worker package differs from the checkout: " + ", ".join(sorted(mismatches)))
        print("Run: uv sync --locked --reinstall-package openconnect-worker-common")
        return 1
    print("Shared worker package matches the checkout.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
