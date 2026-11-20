"""Opt-in, immutable upstream cross-encoder asset manifest."""

from openconnect_worker_common.models.initialize import InitializerConfig
from openconnect_worker_common.models.resource_admission import ResourceBudget, ResourceRequest
from openconnect_worker_common.models.runtime_cache import RuntimeModelAsset, RuntimeModelManifest

MODEL_ID = "cross-encoder/ms-marco-MiniLM-L6-v2"
REVISION = "233902d25c440f23af6f7d6e94d2946bac0bee0a"


def reranker_config(request: ResourceRequest, budget: ResourceBudget, *, allowed_hosts: list[str],
                    timeout_seconds: float, disk_reserve_bytes: int) -> InitializerConfig:
    """Describe assets only; provisioning and license approval are separate."""
    if request.memoryMb <= 0 or request.cpuMillicores <= 0 or request.gpuRequired or request.vramMb != 0:
        raise ValueError("invalid_cpu_resource_request")
    manifest = RuntimeModelManifest(workerKind="knowledge", modelId=MODEL_ID, immutableRevision=REVISION,
        license="apache-2.0", assets=[
            RuntimeModelAsset(relativePath="model.onnx", sizeBytes=91011230,
                              sha256="5d3e70fd0c9ff14b9b5169a51e957b7a9c74897afd0a35ce4bd318150c1d4d4a"),
            RuntimeModelAsset(relativePath="tokenizer.json", sizeBytes=711396, kind="tokenizer",
                              sha256="d241a60d5e8f04cc1b2b3e9ef7a4921b27bf526d9f6050ab90f9267a1f9e5c66"),
        ])
    base = f"https://huggingface.co/{MODEL_ID}/resolve/{REVISION}/"
    return InitializerConfig.model_validate({
        "manifest": manifest,
        "sources": {"model.onnx": {"url": base + "onnx/model.onnx", "mediaTypes": ["application/octet-stream"]},
                    "tokenizer.json": {"url": base + "tokenizer.json",
                                       "mediaTypes": ["text/plain", "application/json", "application/octet-stream"]}},
        "allowedHosts": allowed_hosts, "resourceRequest": request, "resourceBudget": budget,
        "maxDownloadBytes": sum(asset.sizeBytes for asset in manifest.assets),
        "downloadTimeoutSeconds": timeout_seconds, "diskReserveBytes": disk_reserve_bytes,
    })
