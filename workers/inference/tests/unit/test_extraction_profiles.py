import pytest

from openconnect_inference.extraction_profiles import FILENAME, MODEL_ID, REVISION, SHA256, SIZE_BYTES, qwen_configs
from openconnect_worker_common.models.resource_admission import ResourceBudget, ResourceRequest


def resources():
    return (ResourceRequest(memoryMb=2048, cpuMillicores=2000),
            ResourceBudget(memoryMbAvailable=2048, cpuMillicoresAvailable=2000, vramMbAvailable=0))


def test_profile_pins_same_weights_for_initialization_and_serving_without_network(monkeypatch):
    monkeypatch.setattr("socket.getaddrinfo", lambda *args: pytest.fail("profile performed network access"))
    init, serve = qwen_configs(*resources(), artifact_reviewed=True, artifact_license="fixture-license", allowed_hosts=["huggingface.co"],
                               timeout_seconds=60, disk_reserve_bytes=1, threads=2)
    assert init.manifest == serve.manifest
    assert init.manifest.modelId == MODEL_ID and init.manifest.immutableRevision == REVISION
    assert init.manifest.license == "fixture-license"
    assert init.manifest.assets[0].sha256 == SHA256 and init.manifest.assets[0].sizeBytes == SIZE_BYTES
    assert serve.modelAsset == FILENAME and REVISION in init.sources[FILENAME].url


def test_profile_requires_explicit_converted_artifact_review():
    with pytest.raises(ValueError, match="artifact_review_required"):
        qwen_configs(*resources(), artifact_reviewed=False, artifact_license="fixture-license", allowed_hosts=["huggingface.co"],
                     timeout_seconds=60, disk_reserve_bytes=1)


def test_profile_requires_explicit_allowlist_for_the_source_host():
    with pytest.raises(ValueError, match="source_host_not_allowed"):
        qwen_configs(*resources(), artifact_reviewed=True, artifact_license="fixture-license",
                     allowed_hosts=["example.test"], timeout_seconds=60, disk_reserve_bytes=1)


@pytest.mark.parametrize("license_name", ["", "unknown", "unlicensed", "cc-by-nc-4.0"])
def test_profile_does_not_approve_unknown_or_noncommercial_license_for_commercial_use(license_name):
    with pytest.raises(ValueError, match="artifact_license_not_approved"):
        qwen_configs(*resources(), artifact_reviewed=True, artifact_license=license_name, commercial_use=True,
                     allowed_hosts=["huggingface.co"], timeout_seconds=60, disk_reserve_bytes=1)
