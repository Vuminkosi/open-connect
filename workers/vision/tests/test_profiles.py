import pytest

from openconnect_vision.profiles import MOBILENET_REVISION, mobilenet_initializer
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
