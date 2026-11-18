import hashlib
import json

from openconnect_nllb.manifest import check_readiness, sha256_file


def _write_manifest(tmp_path, **overrides) -> None:
    tokenizer = tmp_path / "tokenizer.json"
    tokenizer.write_bytes(b'{"model":"tok"}')
    data = {
        "modelId": "facebook/nllb-200-distilled-600M",
        "revision": "rev123",
        "checksum": "deadbeef",
        "tokenizerHash": sha256_file(tokenizer),
        "license": "cc-by-nc-4.0",
    }
    data.update(overrides)
    (tmp_path / "manifest.json").write_text(json.dumps(data), encoding="utf-8")


def test_missing_manifest_is_unready(tmp_path) -> None:
    result = check_readiness(tmp_path, license_accepted=True)
    assert (result.ready, result.reason) == (False, "model_unavailable")


def test_missing_weights_is_unready(tmp_path) -> None:
    _write_manifest(tmp_path)
    result = check_readiness(tmp_path, license_accepted=True)
    assert (result.ready, result.reason) == (False, "model_unavailable")


def test_missing_tokenizer_is_unready(tmp_path) -> None:
    _write_manifest(tmp_path)
    (tmp_path / "tokenizer.json").unlink()
    (tmp_path / "model.safetensors").write_bytes(b"weights")
    result = check_readiness(tmp_path, license_accepted=True)
    assert (result.ready, result.reason) == (False, "model_unavailable")


def test_tokenizer_hash_mismatch_blocks(tmp_path) -> None:
    _write_manifest(tmp_path)
    (tmp_path / "model.safetensors").write_bytes(b"weights")
    (tmp_path / "tokenizer.json").write_bytes(b'{"model":"tampered"}')
    result = check_readiness(tmp_path, license_accepted=True)
    assert (result.ready, result.reason) == (False, "tokenizer_mismatch")


def test_license_denied_blocks_ready(tmp_path) -> None:
    _write_manifest(tmp_path)
    (tmp_path / "model.safetensors").write_bytes(b"weights")
    blocked = check_readiness(tmp_path, license_accepted=False)
    assert (blocked.ready, blocked.reason) == (False, "license_denied")
    accepted = check_readiness(tmp_path, license_accepted=True)
    assert accepted.ready is True
    assert accepted.manifest is not None and accepted.manifest.revision == "rev123"


def test_permissive_license_needs_no_acceptance(tmp_path) -> None:
    _write_manifest(tmp_path, license="mit")
    (tmp_path / "model.safetensors").write_bytes(b"weights")
    result = check_readiness(tmp_path, license_accepted=False)
    assert result.ready is True
    assert (
        hashlib.sha256(b"weights").hexdigest() != result.manifest.checksum
    )  # checksum recorded, not verified here
