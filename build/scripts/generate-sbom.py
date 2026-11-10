#!/usr/bin/env python3
"""Generate per-package CycloneDX SBOM JSON from a local .nupkg feed (CF-IMP-020d).

Reads each ``*.nupkg`` in ``--feed`` (a directory of packages produced by a
prior ``dotnet pack`` run, e.g. the 19 triple-TFM packages) and emits one
``<package-id>.cdx.json`` CycloneDX 1.5 minimal document per package into
``--out``.

For every package the script records, from observed local artifacts only:

* package id / version / repository commit (from the embedded ``.nuspec``),
* ``lib/net8.0``, ``lib/net9.0``, ``lib/net10.0`` asset lists (from the
  ``.nupkg`` zip entries),
* nuspec dependency groups per TFM (short ``netX.0`` form, honouring long
  ``.NETCoreApp,Version=vX.0`` monikers too).

Stdlib only (argparse, json, xml.etree, pathlib, hashlib, datetime, plus
sys/zipfile/re which are likewise stdlib). No network, no installs, no
writes outside ``--out`` (and the script file itself, which lives in the
repo at ``build/scripts/generate-sbom.py``).

Exit codes: 0 = all packages emitted, 1 = failure, 2 = usage/no input.
"""

from __future__ import annotations

import argparse
import datetime
import hashlib
import json
import re
import sys
import zipfile
import xml.etree.ElementTree as ET
from pathlib import Path

REQUIRED_TFMS = ("net8.0", "net9.0", "net10.0")


def normalize_tfm(moniker: str) -> str:
    """Map a nuspec targetFramework value to short ``netX.0`` form."""
    m = moniker.strip()
    short = re.fullmatch(r"net(\d+)\.(\d+)", m, re.IGNORECASE)
    if short:
        return f"net{short.group(1)}.{short.group(2)}"
    long = re.search(r"Version=v(\d+)\.(\d+)", m, re.IGNORECASE)
    if long and ("NETCoreApp" in m or "NETStandard" not in m):
        return f"net{long.group(1)}.{long.group(2)}"
    return m


def parse_nuspec(
    root: ET.Element,
) -> tuple[str, str, str, dict[str, list[tuple[str, str | None]]]]:
    """Return (id, version, commit, {tfm: [(dep id, version)]}) from a nuspec root."""
    ns = {"n": root.tag.split("}")[0].strip("{")} if root.tag.startswith("{") else {}
    pref = "n:" if ns else ""

    def text(path: str) -> str:
        el = root.find(f".//{pref}{path}", ns)
        return (el.text or "").strip() if el is not None and el.text else ""

    pkg_id = text("metadata/id") or text("id")
    version = text("metadata/version") or text("version")

    commit = ""
    repo_el = root.find(f".//{pref}metadata/{pref}repository", ns)
    if repo_el is None:
        repo_el = root.find(f".//{pref}repository", ns)
    if repo_el is not None:
        commit = (repo_el.get("commit") or "").strip()

    dep_groups: dict[str, list[tuple[str, str | None]]] = {}
    for g in root.findall(f".//{pref}dependencies/{pref}group", ns):
        tfm = normalize_tfm(g.get("targetFramework", ""))
        deps = [
            (d.get("id", ""), d.get("version"))
            for d in g.findall(f"{pref}dependency", ns)
        ]
        dep_groups.setdefault(tfm, []).extend(deps)
    ungrouped = [
        (d.get("id", ""), d.get("version"))
        for d in root.findall(f".//{pref}dependencies/{pref}dependency", ns)
    ]
    if ungrouped:
        dep_groups.setdefault("*ungrouped*", []).extend(ungrouped)
    return pkg_id, version, commit, dep_groups


def sha256_of(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def build_bom(
    pkg_id: str,
    version: str,
    commit: str,
    nupkg_name: str,
    nupkg_hash: str,
    tfm_assets: dict[str, list[str]],
    dep_groups: dict[str, list[tuple[str, str | None]]],
    timestamp: str,
) -> dict:
    """Build a CycloneDX 1.5 minimal document for one package."""
    asset_props = [
        {"name": f"assets:{tfm}", "value": ",".join(sorted(tfm_assets.get(tfm, [])))}
        for tfm in REQUIRED_TFMS
    ]
    root_props = [
        {"name": "nupkg", "value": nupkg_name},
        {"name": "sha256", "value": nupkg_hash},
    ]
    if commit:
        root_props.append({"name": "commit", "value": commit})
    root_props.extend(asset_props)

    components: list[dict] = []
    seen: set[tuple[str, str, str]] = set()
    for tfm, deps in sorted(dep_groups.items()):
        # FIND-01 (CF-REV-029): ungrouped nuspec dependencies (no
        # targetFramework) apply to every TFM per verifier semantics, so
        # emit each once per REQUIRED_TFM instead of dropping them.
        tfms = list(REQUIRED_TFMS) if tfm.startswith("*") else [tfm]
        for dep_id, dep_ver in sorted(deps, key=lambda d: d[0].lower()):
            if not dep_id:
                continue
            for target_tfm in tfms:
                key = (dep_id, dep_ver or "", target_tfm)
                if key in seen:
                    continue
                seen.add(key)
                comp: dict = {
                    "type": "library",
                    "name": dep_id,
                    "scope": "required",
                    "properties": [{"name": "tfm", "value": target_tfm}],
                }
                if dep_ver:
                    comp["version"] = dep_ver
                components.append(comp)

    return {
        "bomFormat": "CycloneDX",
        "specVersion": "1.5",
        "version": 1,
        "metadata": {
            "timestamp": timestamp,
            "component": {
                "type": "library",
                "name": pkg_id,
                "version": version,
                "properties": root_props,
            },
        },
        "components": components,
    }


def process_package(nupkg: Path) -> tuple[str, dict]:
    """Read one .nupkg and return (package id, BOM document)."""
    with zipfile.ZipFile(nupkg) as z:
        names = z.namelist()
        nuspec_names = [n for n in names if n.endswith(".nuspec")]
        if not nuspec_names:
            raise ValueError(f"no .nuspec inside {nupkg.name}")
        root = ET.fromstring(z.read(nuspec_names[0]).decode("utf-8"))
        pkg_id, version, commit, dep_groups = parse_nuspec(root)
        tfm_assets: dict[str, list[str]] = {tfm: [] for tfm in REQUIRED_TFMS}
        for entry in names:
            parts = entry.replace("\\", "/").split("/")
            if len(parts) >= 3 and parts[0] == "lib" and parts[1] in tfm_assets:
                tfm_assets[parts[1]].append(parts[-1])
    if not pkg_id:
        raise ValueError(f"empty package id in {nupkg.name}")
    timestamp = (
        datetime.datetime.now(datetime.timezone.utc)
        .isoformat(timespec="seconds")
        .replace("+00:00", "Z")
    )
    bom = build_bom(
        pkg_id=pkg_id,
        version=version,
        commit=commit,
        nupkg_name=nupkg.name,
        nupkg_hash=sha256_of(nupkg),
        tfm_assets=tfm_assets,
        dep_groups=dep_groups,
        timestamp=timestamp,
    )
    return pkg_id, bom


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(
        description="Generate per-package CycloneDX 1.5 SBOM JSON from a local .nupkg feed (stdlib only)."
    )
    ap.add_argument("--feed", required=True, help="Directory containing *.nupkg files")
    ap.add_argument(
        "--out", required=True, help="Output directory for <id>.cdx.json files"
    )
    args = ap.parse_args(argv)

    feed = Path(args.feed)
    out = Path(args.out)
    if not feed.is_dir():
        print(f"ERROR: feed directory not found: {feed}", file=sys.stderr)
        return 2
    nupkgs = sorted(
        p for p in feed.glob("*.nupkg") if ".symbols." not in p.name.lower()
    )
    if not nupkgs:
        print(f"ERROR: no .nupkg files in feed: {feed}", file=sys.stderr)
        return 2
    out.mkdir(parents=True, exist_ok=True)

    failures = 0
    emitted = 0
    for nupkg in nupkgs:
        try:
            pkg_id, bom = process_package(nupkg)
        except (zipfile.BadZipFile, ValueError, ET.ParseError) as ex:
            print(f"FAIL: {nupkg.name}: {ex}", file=sys.stderr)
            failures += 1
            continue
        dest = out / f"{pkg_id}.cdx.json"
        dest.write_text(json.dumps(bom, indent=2) + "\n", encoding="utf-8")
        print(f"OK: {nupkg.name} -> {dest.name}")
        emitted += 1

    print(f"Emitted: {emitted} SBOM(s) to {out}")
    if failures:
        print(f"SBOM generation FAILED ({failures} package(s)).", file=sys.stderr)
        return 1
    print("SBOM generation PASSED.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
