import json
import sys
from types import SimpleNamespace

import pytest

from openconnect_inference import extraction_runtime


def config():
    return {"manifest": {"workerKind": "inference", "modelId": "fixture", "immutableRevision": "a" * 40,
                         "license": "apache-2.0", "assets": [{"relativePath": "model.gguf", "sizeBytes": 1,
                         "sha256": "b" * 64}]}, "modelAsset": "model.gguf", "artifactReviewed": True,
            "resourceRequest": {"memoryMb": 100, "cpuMillicores": 1000},
            "resourceBudget": {"memoryMbAvailable": 200, "cpuMillicoresAvailable": 1000, "vramMbAvailable": 0}}


@pytest.fixture
def setup(monkeypatch, tmp_path):
    path = tmp_path / "config.json"
    path.write_text(json.dumps(config()), encoding="utf-8")
    for key, value in {"OC_EXTRACTION_ENABLED": "true", "OC_WORKER_ENGINE": "real",
                       "OC_MODEL_DEVICE": "cpu", "OC_EXTRACTION_CONFIG_PATH": str(path),
                       "OC_MODEL_CACHE_DIR": str(tmp_path)}.items():
        monkeypatch.setenv(key, value)
    calls = []

    class Cache:
        def __init__(self, *args):
            pass

        def verify_for_request(self, manifest, **kwargs):
            calls.append((manifest.modelId, kwargs))
            return SimpleNamespace(ready=True, revision_dir=tmp_path, fingerprint="c" * 64)

    class Llama:
        def __init__(self, **kwargs):
            calls.append(kwargs)

    monkeypatch.setattr(extraction_runtime, "RuntimeModelCacheManager", Cache)
    monkeypatch.setitem(sys.modules, "llama_cpp", SimpleNamespace(Llama=Llama))
    return path, calls


def test_loads_only_after_cache_verification_and_rechecks_on_request(setup):
    _, calls = setup
    runtime = extraction_runtime.load_runtime()
    assert runtime is not None
    assert calls[0][0] == "fixture" and calls[1]["n_gpu_layers"] == 0
    assert runtime.verify() and len(calls) == 3


@pytest.mark.parametrize("change", ["unreviewed", "resource_denied", "wrong_worker", "mutable_revision", "gpu"])
def test_rejected_configuration_never_loads_or_accesses_model(setup, change):
    path, calls = setup
    value = config()
    if change == "unreviewed":
        value["artifactReviewed"] = False
    elif change == "resource_denied":
        value["resourceBudget"]["memoryMbAvailable"] = 1
    elif change == "wrong_worker":
        value["manifest"]["workerKind"] = "other"
    elif change == "mutable_revision":
        value["manifest"]["immutableRevision"] = "main"
    else:
        value["resourceRequest"]["gpuRequired"] = True
    path.write_text(json.dumps(value), encoding="utf-8")
    assert extraction_runtime.load_runtime() is None and calls == []


def test_disabled_never_reads_config(monkeypatch):
    monkeypatch.setenv("OC_EXTRACTION_ENABLED", "false")
    monkeypatch.delenv("OC_EXTRACTION_CONFIG_PATH", raising=False)
    assert extraction_runtime.load_runtime() is None
