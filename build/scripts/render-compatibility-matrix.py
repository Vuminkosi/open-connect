#!/usr/bin/env python3
"""Render docs/compatibility/framework-matrix.md (CF-IMP-017).

Reads real project evidence from the tree (csproj TargetFramework(s),
falling back to Directory.Build.props) and an optional compatibility
manifest JSON (see build/compatibility-report.schema.json). Every cell the
evidence cannot yet prove is rendered as a `pending` placeholder that names
the blocking work (CF-IMP-003 multi-target layout), never as fabricated
PASS data.

Usage:
    python build/scripts/render-compatibility-matrix.py [--manifest FILE] [--out FILE]

Stdlib only (argparse, datetime, json, pathlib, re, xml.etree).
"""

from __future__ import annotations

import argparse
import datetime
import json
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
DEFAULT_OUT = REPO / "docs" / "compatibility" / "framework-matrix.md"
TFMS = ("net8.0", "net9.0", "net10.0")
SCHEMA_VERSION = "openconnect.compatibility/v1"


def expand_props(value: str, props: dict[str, str]) -> str:
    """Expand a single level of $(Prop) references (MSBuild-style)."""

    def repl(m: re.Match) -> str:
        return props.get(m.group(1), m.group(0))

    prev, cur = None, value
    while prev != cur:
        prev, cur = cur, re.sub(r"\$\(([^)]+)\)", repl, cur)
    return cur


def load_msbuild_props() -> dict[str, str]:
    """Read-only view of TFM-related MSBuild properties (owned by CF-IMP-003)."""
    props: dict[str, str] = {}
    for rel in ("build/supported-frameworks.props", "Directory.Build.props"):
        p = REPO / rel
        if not p.is_file():
            continue
        try:
            root = ET.parse(p).getroot()
        except ET.ParseError:
            continue
        for pg in root.findall(".//PropertyGroup"):
            for child in pg:
                if (
                    child.tag
                    in (
                        "TargetFramework",
                        "TargetFrameworks",
                        "OpenConnectLibraryTargetFrameworks",
                    )
                    and child.text
                ):
                    props[child.tag] = child.text.strip()
    return props


def read_tfms(csproj: Path, props: dict[str, str], inherited: list[str]) -> list[str]:
    try:
        root = ET.parse(csproj).getroot()
    except ET.ParseError:
        return list(inherited)
    for tag in ("TargetFrameworks", "TargetFramework"):
        el = root.find(f".//{tag}")
        if el is not None and el.text and el.text.strip():
            return [
                t.strip() for t in expand_props(el.text, props).split(";") if t.strip()
            ]
    return list(inherited)


def repo_default_tfms() -> list[str]:
    props = REPO / "Directory.Build.props"
    if not props.is_file():
        return ["net8.0"]
    try:
        root = ET.parse(props).getroot()
    except ET.ParseError:
        return ["net8.0"]
    for tag in ("TargetFrameworks", "TargetFramework"):
        el = root.find(f".//{tag}")
        if el is not None and el.text and el.text.strip():
            return [t.strip() for t in el.text.split(";") if t.strip()]
    return ["net8.0"]


def package_id(csproj: Path) -> str:
    try:
        root = ET.parse(csproj).getroot()
        el = root.find(".//PackageId")
        if el is not None and el.text and el.text.strip():
            return el.text.strip()
    except ET.ParseError:
        pass
    return csproj.stem


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(
        description="Render the framework compatibility matrix."
    )
    ap.add_argument(
        "--manifest", default=None, help="Optional compatibility manifest JSON"
    )
    ap.add_argument("--out", default=str(DEFAULT_OUT), help="Markdown output path")
    args = ap.parse_args(argv)

    manifest: dict = {}
    if args.manifest:
        manifest = json.loads(Path(args.manifest).read_text(encoding="utf-8"))
        if manifest.get("schemaVersion") != SCHEMA_VERSION:
            print(
                f"ERROR: manifest schemaVersion must be {SCHEMA_VERSION!r}",
                file=sys.stderr,
            )
            return 2
    manifest_pkgs = {p.get("id", ""): p for p in manifest.get("packages", [])}

    inherited = repo_default_tfms()
    props = load_msbuild_props()
    lib_tfms = [
        t.strip()
        for t in expand_props(
            props.get("OpenConnectLibraryTargetFrameworks", ";".join(inherited)), props
        ).split(";")
        if t.strip()
    ]
    projects = sorted((REPO / "src").glob("*/*.csproj"))
    rows: list[tuple[str, dict[str, str], str]] = []
    for csproj in projects:
        pid = package_id(csproj)
        tfms = read_tfms(csproj, props, inherited)
        cells: dict[str, str] = {}
        note = ""
        manifest_pkg = manifest_pkgs.get(pid, {})
        for tfm in TFMS:
            if tfm in tfms:
                smoke = (manifest_pkg.get("consumerSmoke") or {}).get(tfm)
                if smoke == "pass":
                    cells[tfm] = "supported (consumer smoke: pass)"
                else:
                    cells[tfm] = "targeted — evidence: pending (CF-IMP-003)"
            else:
                cells[tfm] = "not targeted"
        if len(tfms) == 1 and not tfms[0].startswith("net"):
            note = f"unresolved TFM expression ({tfms[0]}); needs manifest evidence"
        elif not any(t in TFMS for t in tfms):
            note = f"TFMs ({', '.join(tfms)}) outside net8/9/10 matrix scope"
        rows.append((pid, cells, note))

    stamp = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%d %H:%M UTC")
    lines = [
        "# Framework Compatibility Matrix",
        "",
        f"_Generated {stamp} by `build/scripts/render-compatibility-matrix.py` "
        f"(schema `{SCHEMA_VERSION}`). Do not hand-edit cells; re-run the script. "
        "Cells marked `pending` are evidence placeholders, not results._",
        "",
        f"Repo default TFM(s) (Directory.Build.props): `{', '.join(inherited)}`.",
        "See `docs/compatibility/runtime-support-policy.md` for the support policy.",
        "",
        "| Package | net8.0 | net9.0 | net10.0 | Notes |",
        "|---|---|---|---|---|",
    ]
    for pid, cells, note in rows:
        lines.append(
            f"| `{pid}` | {cells['net8.0']} | {cells['net9.0']} | {cells['net10.0']} | {note} |"
        )
    lines += [
        "",
        "## Legend",
        "",
        "- `supported (consumer smoke: pass)` — manifest evidence (pack + per-TFM consumer run) recorded.",
        "- `targeted — evidence: pending (CF-IMP-003)` — csproj targets the TFM but no CI evidence recorded yet.",
        "- `not targeted` — csproj does not list the TFM.",
        "",
        "## Open questions",
        "",
        "- Test-project partition per TFM is reconciled once CF-IMP-003 lands (see CF-IMP-017 handoff).",
        "- Nightly OS/RID matrix expansion is out of scope for CF-IMP-017.",
        "",
    ]
    out = Path(args.out)
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text("\n".join(lines), encoding="utf-8")
    print(f"Wrote {out} ({len(rows)} package row(s)).")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
