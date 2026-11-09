import json

from openconnect_classification.manifest import check_readiness


def _write_manifest(tmp_path, **overrides) -> None:
    data = {
        "modelId": "org/cls",
        "revision": "abc123",
        "checksum": "deadbeef",
        "license": "apache-2.0",
    }
    data.update(overrides)
    (tmp_path / "manifest.json").write_text(json.dumps(data), encoding="utf-8")


def test_missing_manifest_is_unready(tmp_path) -> None:
    result = check_readiness(tmp_path, license_accepted=False)
    assert (result.ready, result.reason) == (False, "model_unavailable")


def test_missing_weights_is_unready(tmp_path) -> None:
    _write_manifest(tmp_path)
    result = check_readiness(tmp_path, license_accepted=False)
    assert (result.ready, result.reason) == (False, "model_unavailable")


def test_ready_with_manifest_and_weights(tmp_path) -> None:
    _write_manifest(tmp_path)
    (tmp_path / "model.bin").write_bytes(b"weights")
    result = check_readiness(tmp_path, license_accepted=False)
    assert result.ready is True
    assert result.manifest is not None and result.manifest.revision == "abc123"


def test_restrictive_license_blocks_without_acceptance(tmp_path) -> None:
    _write_manifest(tmp_path, license="cc-by-nc-4.0")
    (tmp_path / "model.safetensors").write_bytes(b"weights")
    blocked = check_readiness(tmp_path, license_accepted=False)
    assert (blocked.ready, blocked.reason) == (False, "license_denied")
    accepted = check_readiness(tmp_path, license_accepted=True)
    assert accepted.ready is True
