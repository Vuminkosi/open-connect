import hashlib
import json
from pathlib import Path

import pytest
from pydantic import ValidationError

from openconnect_worker_common.models import initialize as initializer

DATA = b"model data"


def config_dict() -> dict:
    return {
        "manifest": {
            "workerKind": "knowledge", "modelId": "example/model",
            "immutableRevision": "a" * 40, "license": "mit", "assets": [{
                "relativePath": "model.onnx", "sha256": hashlib.sha256(DATA).hexdigest(),
                "sizeBytes": len(DATA),
            }],
        },
        "sources": {"model.onnx": {
            "url": "https://models.example/model", "mediaTypes": ["application/octet-stream"]
        }},
        "allowedHosts": ["models.example"],
        "resourceRequest": {"memoryMb": 50, "cpuMillicores": 200},
        "resourceBudget": {"memoryMbAvailable": 100, "vramMbAvailable": 0,
                           "cpuMillicoresAvailable": 500},
        "maxDownloadBytes": 1000, "downloadTimeoutSeconds": 10,
    }


def test_init_provisions_once_and_restart_is_offline(tmp_path: Path, monkeypatch) -> None:
    calls: list[int] = []

    class Fetcher:
        def __init__(self, *args, **kwargs):
            pass

        def __call__(self, asset, target):
            calls.append(1)
            target.write_bytes(DATA)

    monkeypatch.setattr(initializer, "StartupAssetFetcher", Fetcher)
    config = initializer.InitializerConfig.model_validate(config_dict())
    result = initializer.initialize(config, tmp_path, provisioning="first-start-if-missing",
                                    offline=False)
    assert result.ready and calls == [1]
    result = initializer.initialize(config, tmp_path, provisioning="verify-existing-only",
                                    offline=True)
    assert result.ready and result.cache_hit and calls == [1]


def test_missing_offline_and_resource_denial_do_not_download(tmp_path: Path, monkeypatch) -> None:
    monkeypatch.setattr(initializer, "StartupAssetFetcher", lambda *a, **k: pytest.fail("fetcher"))
    config = initializer.InitializerConfig.model_validate(config_dict())
    result = initializer.initialize(config, tmp_path, provisioning="first-start-if-missing",
                                    offline=True)
    assert result.reason == "model_unavailable"
    config.resourceBudget.memoryMbAvailable = 1
    result = initializer.initialize(config, tmp_path, provisioning="first-start-if-missing",
                                    offline=False)
    assert result.reason == "resource_denied"


@pytest.mark.parametrize("field,value", [
    ("workerKind", "../escape"), ("immutableRevision", "main"),
])
def test_manifest_rejects_unsafe_worker_and_mutable_revision(field: str, value: str) -> None:
    config = config_dict()
    config["manifest"][field] = value
    with pytest.raises(ValidationError):
        initializer.InitializerConfig.model_validate(config)


def test_cli_disabled_does_not_read_manifest_or_create_cache(tmp_path: Path, monkeypatch, capsys):
    monkeypatch.setenv("OC_MODEL_PROVISIONING", "disabled")
    monkeypatch.setenv("OC_MODEL_MANIFEST_PATH", str(tmp_path / "missing"))
    monkeypatch.setenv("OC_MODEL_CACHE_DIR", str(tmp_path / "cache"))
    assert initializer.main() == 0
    assert json.loads(capsys.readouterr().out)["reason"] == "worker_disabled"
    assert not (tmp_path / "cache").exists()


def test_cli_rejects_invalid_mode_before_reading_manifest(monkeypatch, capsys):
    monkeypatch.setenv("OC_MODEL_PROVISIONING", "surprise")
    assert initializer.main() == 1
    assert json.loads(capsys.readouterr().out)["reason"] == "invalid_request"


def test_license_denial_before_fetch(tmp_path: Path, monkeypatch) -> None:
    config_data = config_dict()
    config_data["manifest"]["license"] = "research-only"
    config = initializer.InitializerConfig.model_validate(config_data)

    def forbidden(*args):
        pytest.fail("download invoked")

    monkeypatch.setattr(initializer.StartupAssetFetcher, "__call__", forbidden)
    result = initializer.initialize(config, tmp_path, provisioning="first-start-if-missing",
                                    offline=False)
    assert result.reason == "license_denied"
    assert not list(tmp_path.iterdir())
