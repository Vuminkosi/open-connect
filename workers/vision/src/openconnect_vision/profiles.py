"""Pinned, opt-in candidate profiles; constructing these never downloads data."""

from openconnect_worker_common.models.initialize import InitializerConfig
from openconnect_worker_common.models.resource_admission import ResourceBudget, ResourceRequest
from openconnect_worker_common.models.runtime_cache import RuntimeModelAsset, RuntimeModelManifest

MOBILENET_REVISION = "c5cae0f1942c8d7727372c7b1f6b485aa39e4421"
MOBILENET_MODEL_ID = "onnx/models/mobilenetv2-12"
MOBILENET_TAXONOMY = "imagenet-1k-c5cae0f1942c8d7727372c7b1f6b485aa39e4421"


def mobilenet_initializer(request: ResourceRequest, budget: ResourceBudget, *, allowed_hosts: list[str],
                          timeout_seconds: float, disk_reserve_bytes: int) -> InitializerConfig:
    """Return an operator-controlled init plan, not a license or deployment approval."""
    if request.memoryMb <= 0 or request.cpuMillicores <= 0 or request.gpuRequired or request.vramMb != 0:
        raise ValueError("invalid_cpu_resource_request")
    manifest = RuntimeModelManifest(workerKind="vision", modelId=MOBILENET_MODEL_ID,
        immutableRevision=MOBILENET_REVISION, license="apache-2.0", assets=[
            RuntimeModelAsset(relativePath="model.onnx", sizeBytes=13964571,
                sha256="c0c3f76d93fa3fd6580652a45618618a220fced18babf65774ed169de0432ad5"),
            RuntimeModelAsset(relativePath="labels.txt", sizeBytes=31675, kind="config",
                sha256="acf75ef0abe89694b19056e0796401068b459c457baa30335f240c7692857355"),
        ])
    base = f"onnx/models/{MOBILENET_REVISION}/validated/vision/classification/"
    return InitializerConfig.model_validate({
        "manifest": manifest,
        "sources": {
            "model.onnx": {"url": f"https://media.githubusercontent.com/media/{base}mobilenet/model/mobilenetv2-12.onnx",
                           "mediaTypes": ["application/octet-stream"]},
            "labels.txt": {"url": f"https://raw.githubusercontent.com/{base}synset.txt",
                           "mediaTypes": ["text/plain", "application/octet-stream"]},
        },
        "allowedHosts": allowed_hosts, "resourceRequest": request, "resourceBudget": budget,
        "maxDownloadBytes": sum(asset.sizeBytes for asset in manifest.assets),
        "downloadTimeoutSeconds": timeout_seconds, "diskReserveBytes": disk_reserve_bytes,
    })
