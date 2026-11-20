"""Check a deployment-provided ONNX bundle before starting the worker."""

from __future__ import annotations

import hashlib
import os
from pathlib import Path
import re
import sys


def verify(root: Path, expected: str) -> bool:
    if not root.is_absolute() or root.is_symlink() or not re.fullmatch(r"[0-9a-f]{64}", expected):
        return False
    try:
        manifest = root / "classification.onnx.json"
        model = root / "model.onnx"
        tokenizer = root / "tokenizer.json"
        if any(path.is_symlink() or not path.is_file() for path in (manifest, model, tokenizer)):
            return False
        if not 0 < manifest.stat().st_size <= 65536 or model.stat().st_size == 0 or tokenizer.stat().st_size == 0:
            return False
        with manifest.open("rb") as source:
            content = source.read(65537)
        return len(content) <= 65536 and hashlib.sha256(content).hexdigest() == expected
    except OSError:
        return False


def main() -> int:
    if verify(Path(os.environ.get("MODEL_DIR", "/models")), os.environ.get("OC_CLASSIFICATION_BUNDLE_SHA256", "")):
        return 0
    print("classification ONNX bundle or contract pin unavailable", file=sys.stderr)
    return 1


if __name__ == "__main__":
    raise SystemExit(main())
