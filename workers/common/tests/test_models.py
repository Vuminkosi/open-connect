"""Model helper tests: verification, provisioning plans, admission, hashing."""

import pytest

from openconnect_worker_common.models.manifest import ModelFile, hash_file
from openconnect_worker_common.models.provision import (
    ProvisionRequest,
    is_safe_target,
    plan_provision,
)
from openconnect_worker_common.models.resource_admission import (
    AdmissionVerdict,
    ResourceBudget,
    ResourceRequest,
    admit,
)
from openconnect_worker_common.models.verify import verify_file

DIGEST = "b" * 64


def _package(tmp_name: str = "m.bin"):
    from openconnect_worker_common.models.manifest import ModelPackage

    return ModelPackage(
        name="m", version="1", files=[ModelFile(relativePath=tmp_name, sha256=DIGEST, sizeBytes=3)]
    )


def test_verify_file_matches() -> None:
    expected = ModelFile(relativePath="m.bin", sha256=DIGEST, sizeBytes=3)
    assert verify_file(expected, DIGEST, 3).verified is True
    assert verify_file(expected, "c" * 64, 3).verified is False
    assert verify_file(expected, DIGEST, 4).verified is False


def test_hash_file_reads_local_only(tmp_path) -> None:
    target = tmp_path / "m.bin"
    target.write_bytes(b"abc")
    assert hash_file(target) == ("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")


def test_provision_plan_rejects_escape(tmp_path) -> None:
    from openconnect_worker_common.models.manifest import ModelPackage

    evil = ModelPackage(
        name="m",
        version="1",
        files=[ModelFile(relativePath="../evil.bin", sha256=DIGEST, sizeBytes=1)],
    )
    request = ProvisionRequest(package=evil, modelDir=str(tmp_path.resolve()), workerId="w-1")
    with pytest.raises(ValueError):
        plan_provision(request)


def test_provision_plan_lists_targets(tmp_path) -> None:
    request = ProvisionRequest(package=_package(), modelDir=str(tmp_path.resolve()), workerId="w-1")
    assert plan_provision(request) == [str(tmp_path.resolve() / "m.bin")]


def test_is_safe_target_requires_absolute_base() -> None:
    assert is_safe_target("relative/dir", "m.bin") is False


def test_admit_within_budget() -> None:
    budget = ResourceBudget(
        memoryMbAvailable=8192,
        vramMbAvailable=4096,
        cpuMillicoresAvailable=4000,
        gpuAvailable=True,
    )
    decision = admit(ResourceRequest(memoryMb=1024, vramMb=512, cpuMillicores=500), budget)
    assert decision.verdict == AdmissionVerdict.ADMIT


def test_admit_denies_limiting_resource() -> None:
    budget = ResourceBudget(
        memoryMbAvailable=512,
        vramMbAvailable=0,
        cpuMillicoresAvailable=4000,
        gpuAvailable=False,
    )
    assert (
        admit(ResourceRequest(memoryMb=1024, cpuMillicores=100), budget).verdict
        == AdmissionVerdict.DENY
    )
    assert (
        admit(ResourceRequest(memoryMb=1, gpuRequired=True), budget).verdict
        == AdmissionVerdict.DENY
    )
    assert admit(ResourceRequest(memoryMb=1, vramMb=64), budget).verdict == AdmissionVerdict.DENY
