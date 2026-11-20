import pytest

from openconnect_knowledge.profiles import MINILM_MODEL_ID, MINILM_REVISION, minilm_configs
from openconnect_worker_common.models.resource_admission import ResourceBudget, ResourceRequest


def test_minilm_candidate_has_matching_pinned_init_and_service_manifests(monkeypatch):
    monkeypatch.setattr("socket.getaddrinfo", lambda *args, **kwargs: pytest.fail("network used"))
    request = ResourceRequest(memoryMb=500, cpuMillicores=1000)
    budget = ResourceBudget(memoryMbAvailable=1000, vramMbAvailable=0, cpuMillicoresAvailable=2000)
    init, service = minilm_configs(request, budget, allowed_hosts=["huggingface.co", "mirror.example"],
                                   timeout_seconds=120, disk_reserve_bytes=1024)
    assert init.manifest == service.manifest
    assert init.manifest.modelId == MINILM_MODEL_ID
    assert init.manifest.immutableRevision == MINILM_REVISION
    assert init.manifest.license == "apache-2.0"
    assert init.maxDownloadBytes == sum(asset.sizeBytes for asset in init.manifest.assets)
    assert service.profile.dimension == 384 and service.profile.maxTokens == 256
    assert service.resourceRequest == request and service.resourceBudget == budget


@pytest.mark.parametrize("resource_request", [
    ResourceRequest(), ResourceRequest(memoryMb=1, cpuMillicores=1, gpuRequired=True),
    ResourceRequest(memoryMb=1, cpuMillicores=1, vramMb=1),
])
def test_profile_requires_explicit_cpu_resource_measurements(resource_request):
    budget = ResourceBudget(memoryMbAvailable=1000, vramMbAvailable=1000, cpuMillicoresAvailable=2000)
    with pytest.raises(ValueError, match="invalid_cpu_resource_request"):
        minilm_configs(resource_request, budget, allowed_hosts=["huggingface.co"],
                       timeout_seconds=120, disk_reserve_bytes=0)
