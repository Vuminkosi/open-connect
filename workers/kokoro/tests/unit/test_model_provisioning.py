import hashlib

import pytest

from openconnect_kokoro.engine import ModelUnavailable, _ensure_artifacts


def test_missing_artifacts_do_not_download_by_default(tmp_path, monkeypatch):
    monkeypatch.delenv("OC_KOKORO_ALLOW_DOWNLOAD", raising=False)
    monkeypatch.setattr("urllib.request.urlopen", lambda *args, **kwargs: pytest.fail("unexpected network request"))
    with pytest.raises(ModelUnavailable, match="automatic model download is disabled"):
        _ensure_artifacts(tmp_path)


def test_invalid_existing_artifact_is_preserved(tmp_path, monkeypatch):
    monkeypatch.setenv("OC_KOKORO_ALLOW_DOWNLOAD", "true")
    artifact = tmp_path / "kokoro-v1_0.pth"
    artifact.write_bytes(b"unexpected contents")
    before = hashlib.sha256(artifact.read_bytes()).digest()
    with pytest.raises(ModelUnavailable, match="hash mismatch"):
        _ensure_artifacts(tmp_path)
    assert hashlib.sha256(artifact.read_bytes()).digest() == before
