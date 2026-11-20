import hashlib
import os
from pathlib import Path
import subprocess
import sys


SCRIPT = Path(__file__).resolve().parents[2] / "scripts" / "verify_bundle.py"


def run_check(root: Path, pin: str) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        [sys.executable, str(SCRIPT)],
        env={**os.environ, "MODEL_DIR": str(root), "OC_CLASSIFICATION_BUNDLE_SHA256": pin},
        capture_output=True,
        text=True,
        check=False,
    )


def test_preflight_requires_exact_pinned_contract_and_all_assets(tmp_path):
    manifest = b'{"schemaVersion":"fixture"}'
    (tmp_path / "classification.onnx.json").write_bytes(manifest)
    (tmp_path / "model.onnx").write_bytes(b"model")
    (tmp_path / "tokenizer.json").write_bytes(b"tokenizer")
    digest = hashlib.sha256(manifest).hexdigest()
    assert run_check(tmp_path, digest).returncode == 0
    assert run_check(tmp_path, "").returncode != 0
    assert run_check(tmp_path, "0" * 64).returncode != 0
    (tmp_path / "tokenizer.json").write_bytes(b"")
    assert run_check(tmp_path, digest).returncode != 0
    (tmp_path / "tokenizer.json").write_bytes(b"tokenizer")
    (tmp_path / "classification.onnx.json").write_bytes(b"x" * 65537)
    assert run_check(tmp_path, digest).returncode != 0
