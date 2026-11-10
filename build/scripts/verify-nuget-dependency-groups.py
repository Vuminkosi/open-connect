#!/usr/bin/env python3
"""Verify per-TFM NuGet dependency groups in packed packages (CF-IMP-017).

For every eligible .nupkg (or loose .nuspec) under the given path(s):
  1. Opens the embedded .nuspec and asserts dependency groups exist for each
     required TFM (default: net8.0, net9.0, net10.0), honouring both short
     (`net8.0`) and long (`.NETCoreApp,Version=v8.0`) target-framework
     monikers.
  2. Asserts the net8/net9 groups carry EF Core / MAF dependency entries
     (the cross-framework spec requires per-TFM EF/MAF groups where the
     package depends on them at all).
  3. Asserts no net10-only dependency (see --net10-only list) appears in a
     net8/net9 group. A net10-only package leaking into an older-TFM group
     breaks restore/compile on the matching runtime.

Exit codes: 0 = PASS, 1 = FAIL (violations found), 2 = usage/no input error.
Stdlib only (argparse, pathlib, sys, xml.etree, zipfile).
"""

from __future__ import annotations

import argparse
import re
import sys
import zipfile
import xml.etree.ElementTree as ET
from pathlib import Path

# Packages known to require .NET 10+ (no net8/net9 asset). EMPTY by default:
# entries require positive evidence (e.g. a dependency that only ships a
# net10.0 asset) and must be passed explicitly via --net10-only. A wrong
# entry here fails the gate on valid packages, so never guess.
DEFAULT_NET10_ONLY: list[str] = []

# Dependency-name substrings that mark an EF Core / MAF group entry. A group
# is only subject to the EF/MAF-presence rule when the package references EF
# or MAF on ANY TFM (library packages without such references are exempt).
EF_MARKERS = ("Microsoft.EntityFrameworkCore", "Npgsql.EntityFrameworkCore")
MAF_MARKERS = ("Microsoft.Agents.AI",)


def normalize_tfm(moniker: str) -> str:
    """Map a nuspec targetFramework value to a short `netX.0` form."""
    m = moniker.strip()
    short = re.fullmatch(r"net(\d+)\.(\d+)", m, re.IGNORECASE)
    if short:
        return f"net{short.group(1)}.{short.group(2)}"
    long = re.search(r"Version=v(\d+)\.(\d+)", m, re.IGNORECASE)
    if long and ("NETCoreApp" in m or "NETStandard" not in m):
        return f"net{long.group(1)}.{long.group(2)}"
    return m


def load_nuspecs(paths: list[Path]) -> list[tuple[str, ET.Element]]:
    """Return (display_name, nuspec_root) for every package found."""
    found: list[tuple[str, ET.Element]] = []
    inputs: list[Path] = []
    for p in paths:
        if p.is_dir():
            inputs += sorted(p.glob("*.nupkg")) + sorted(p.glob("*.nuspec"))
        elif p.is_file():
            inputs.append(p)
    for item in inputs:
        if item.suffix == ".nupkg" and ".symbols." not in item.name:
            with zipfile.ZipFile(item) as z:
                nuspec_names = [n for n in z.namelist() if n.endswith(".nuspec")]
                if not nuspec_names:
                    print(f"SKIP (no nuspec inside): {item.name}")
                    continue
                root = ET.fromstring(z.read(nuspec_names[0]).decode("utf-8"))
                found.append((item.name, root))
        elif item.suffix == ".nuspec":
            found.append((item.name, ET.parse(item).getroot()))
    return found


def groups(root: ET.Element) -> dict[str, list[tuple[str, str | None]]]:
    """Map normalized TFM -> [(dependency id, version)] per nuspec group."""
    ns = {"n": root.tag.split("}")[0].strip("{")} if root.tag.startswith("{") else {}
    pref = "n:" if ns else ""
    result: dict[str, list[tuple[str, str | None]]] = {}
    for g in root.findall(f".//{pref}dependencies/{pref}group", ns):
        tfm = normalize_tfm(g.get("targetFramework", ""))
        deps = [
            (d.get("id", ""), d.get("version"))
            for d in g.findall(f"{pref}dependency", ns)
        ]
        result.setdefault(tfm, []).extend(deps)
    # Ungrouped <dependencies><dependency/> applies to all TFMs.
    ungrouped = [
        (d.get("id", ""), d.get("version"))
        for d in root.findall(f".//{pref}dependencies/{pref}dependency", ns)
    ]
    if ungrouped:
        result.setdefault("*ungrouped*", []).extend(ungrouped)
    return result


def check_package(
    name: str, root: ET.Element, required: list[str], net10_only: list[str]
) -> list[str]:
    errors: list[str] = []
    dep_groups = groups(root)
    if not dep_groups:
        print(f"SKIP (no dependency groups): {name}")
        return []

    tfms_present = {t for t in dep_groups if not t.startswith("*")}
    for tfm in required:
        if tfm not in tfms_present and "*ungrouped*" not in dep_groups:
            errors.append(
                f"{name}: missing dependency group for {tfm} (present: {sorted(tfms_present) or 'none'})"
            )

    uses_ef = any(
        any(any(m.lower() in dep_id.lower() for m in EF_MARKERS) for dep_id, _ in deps)
        for deps in dep_groups.values()
    )
    uses_maf = any(
        any(any(m.lower() in dep_id.lower() for m in MAF_MARKERS) for dep_id, _ in deps)
        for deps in dep_groups.values()
    )
    for tfm in required:
        deps = dep_groups.get(tfm, []) + dep_groups.get("*ungrouped*", [])
        ids = [d.lower() for d, _ in deps]
        if uses_ef and not any(any(m.lower() in i for m in EF_MARKERS) for i in ids):
            errors.append(
                f"{name}: group {tfm} lacks EF Core dependency entries present on other TFMs"
            )
        if uses_maf and not any(any(m.lower() in i for m in MAF_MARKERS) for i in ids):
            errors.append(
                f"{name}: group {tfm} lacks MAF dependency entries present on other TFMs"
            )
        for banned in net10_only:
            if banned.lower() in ids and tfm in ("net8.0", "net9.0"):
                errors.append(
                    f"{name}: net10-only dependency '{banned}' leaked into {tfm} group"
                )
    return errors


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description="Verify per-TFM NuGet dependency groups.")
    ap.add_argument(
        "paths", nargs="+", help=".nupkg/.nuspec files or directories containing them"
    )
    ap.add_argument(
        "--required",
        default="net8.0,net9.0,net10.0",
        help="Comma-separated required TFMs (default: net8.0,net9.0,net10.0)",
    )
    ap.add_argument(
        "--net10-only",
        default=",".join(DEFAULT_NET10_ONLY),
        help="Comma-separated dependency IDs forbidden in net8/net9 groups",
    )
    ap.add_argument(
        "--exclude",
        default="",
        help="Comma-separated package filename prefixes to skip",
    )
    args = ap.parse_args(argv)

    required = [t.strip() for t in args.required.split(",") if t.strip()]
    net10_only = [d.strip() for d in args.net10_only.split(",") if d.strip()]
    excluded = [e.strip().lower() for e in args.exclude.split(",") if e.strip()]

    packages = load_nuspecs([Path(p) for p in args.paths])
    packages = [
        (n, r)
        for n, r in packages
        if not any(n.lower().startswith(e) for e in excluded)
    ]
    if not packages:
        print(
            "ERROR: no packages found under given path(s). Run 'dotnet pack' first.",
            file=sys.stderr,
        )
        return 2

    errors: list[str] = []
    checked = 0
    for name, root in packages:
        errs = check_package(name, root, required, net10_only)
        if not errs and groups(root):
            print(f"PASS: {name} dependency groups OK for {', '.join(required)}")
            checked += 1
        errors.extend(errs)
    print(f"Checked: {checked} package(s) with dependency groups.")

    if errors:
        print("NuGet dependency-group verification FAILED:", file=sys.stderr)
        for e in errors:
            print(f"  - {e}", file=sys.stderr)
        return 1
    print("NuGet dependency-group verification PASSED.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
