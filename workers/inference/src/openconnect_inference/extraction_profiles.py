"""Pinned extraction model candidate; constructing a profile never downloads assets."""

from __future__ import annotations

from openconnect_inference.extraction_runtime import ExtractionConfig
from openconnect_worker_common.models.initialize import InitializerConfig
from openconnect_worker_common.models.resource_admission import ResourceBudget, ResourceRequest
from openconnect_worker_common.models.runtime_cache import RuntimeModelAsset, RuntimeModelManifest, is_license_approved

MODEL_ID = "bartowski/Qwen_Qwen3-0.6B-GGUF"
REVISION = "60b85c0e3d8fe0f6474f406922a26d12aca4550d"
FILENAME = "Qwen_Qwen3-0.6B-Q4_K_M.gguf"
SHA256 = "9acfc1e001311f34b4252001b626f2e466d592a42065f66571bff3790d4e1b14"
SIZE_BYTES = 484_220_320


def qwen_configs(request: ResourceRequest, budget: ResourceBudget, *, artifact_reviewed: bool, artifact_license: str,
                 allowed_hosts: list[str], timeout_seconds: float, disk_reserve_bytes: int,
                 commercial_use: bool = False, threads: int = 1) -> tuple[InitializerConfig, ExtractionConfig]:
    """Return pinned configs only after explicit review of the converted artifact's terms."""
    if artifact_reviewed is not True:
        raise ValueError("artifact_review_required")
    if (not isinstance(artifact_license, str) or not artifact_license.strip()
            or not is_license_approved(artifact_license, license_accepted=True, commercial_use=commercial_use)):
        raise ValueError("artifact_license_not_approved")
    if not isinstance(allowed_hosts, list) or not any(
            isinstance(host, str) and host.lower() == "huggingface.co" for host in allowed_hosts):
        raise ValueError("source_host_not_allowed")
    if (request.memoryMb <= 0 or request.cpuMillicores < threads * 1000 or request.gpuRequired
            or request.vramMb != 0):
        raise ValueError("invalid_cpu_resource_request")
    manifest = RuntimeModelManifest(workerKind="inference", modelId=MODEL_ID, immutableRevision=REVISION,
        license=artifact_license.strip(), quantization="Q4_K_M", runtimeCompatibility="llama-cpp-python-0.3.36-cpu",
        assets=[RuntimeModelAsset(relativePath=FILENAME, sizeBytes=SIZE_BYTES, sha256=SHA256)])
    url = f"https://huggingface.co/{MODEL_ID}/resolve/{REVISION}/{FILENAME}"
    initializer = InitializerConfig.model_validate({"manifest": manifest,
        "sources": {FILENAME: {"url": url, "mediaTypes": ["application/octet-stream"]}},
        "allowedHosts": allowed_hosts, "resourceRequest": request, "resourceBudget": budget,
        "maxDownloadBytes": SIZE_BYTES, "downloadTimeoutSeconds": timeout_seconds,
        "diskReserveBytes": disk_reserve_bytes, "licenseAccepted": True, "commercialUse": commercial_use})
    serving = ExtractionConfig(manifest=manifest, modelAsset=FILENAME, resourceRequest=request,
        resourceBudget=budget, artifactReviewed=True, licenseAccepted=True, commercialUse=commercial_use,
        threads=threads)
    return initializer, serving
