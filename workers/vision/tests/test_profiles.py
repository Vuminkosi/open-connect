import pytest

from openconnect_vision.profiles import CLIP_REVISION, MOBILENET_REVISION, clip_initializer, mobilenet_initializer
from openconnect_worker_common.models.resource_admission import ResourceBudget, ResourceRequest


def test_profile_is_pinned_and_never_resolves_network(monkeypatch):
    monkeypatch.setattr("socket.getaddrinfo", lambda *a, **k: pytest.fail("network accessed"))
    request = ResourceRequest(memoryMb=512, cpuMillicores=1000)
    budget = ResourceBudget(memoryMbAvailable=1024, vramMbAvailable=0, cpuMillicoresAvailable=2000)
    plan = mobilenet_initializer(request, budget, allowed_hosts=["media.githubusercontent.com", "raw.githubusercontent.com"],
                                 timeout_seconds=120, disk_reserve_bytes=0)
    assert plan.manifest.immutableRevision == MOBILENET_REVISION
    assert {asset.relativePath for asset in plan.manifest.assets} == {"model.onnx", "labels.txt"}
    assert plan.maxDownloadBytes == sum(asset.sizeBytes for asset in plan.manifest.assets)


def test_profile_requires_cpu_resource_admission():
    budget = ResourceBudget(memoryMbAvailable=1024, vramMbAvailable=0, cpuMillicoresAvailable=2000)
    with pytest.raises(ValueError, match="invalid_cpu_resource_request"):
        mobilenet_initializer(ResourceRequest(), budget, allowed_hosts=["example.com"],
                              timeout_seconds=120, disk_reserve_bytes=0)


def test_clip_candidate_requires_review_and_pins_both_encoders_without_network(monkeypatch):
    monkeypatch.setattr("socket.getaddrinfo", lambda *a, **k: pytest.fail("network accessed"))
    request = ResourceRequest(memoryMb=1024, cpuMillicores=1000)
    budget = ResourceBudget(memoryMbAvailable=2048, vramMbAvailable=0, cpuMillicoresAvailable=2000)
    settings = dict(allowed_hosts=["huggingface.co"], timeout_seconds=120, disk_reserve_bytes=0)
    with pytest.raises(ValueError, match="artifact_license_review_required"):
        clip_initializer(request, budget, **settings)
    plan = clip_initializer(request, budget, artifact_license_reviewed=True, **settings)
    assert plan.manifest.immutableRevision == CLIP_REVISION
    assets = {asset.relativePath: asset for asset in plan.manifest.assets}
    assert {"onnx/vision_model_quantized.onnx", "onnx/text_model_quantized.onnx", "tokenizer.json",
            "preprocessor_config.json"} <= assets.keys()
    assert all(len(asset.sha256) == 64 for asset in assets.values())
    assert plan.maxDownloadBytes == sum(asset.sizeBytes for asset in assets.values())
    assert all(CLIP_REVISION in source.url for source in plan.sources.values())
