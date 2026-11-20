"""Pinned model candidates; creating a profile never downloads assets."""

from __future__ import annotations

from openconnect_knowledge.app import Profile, ServiceConfig
from openconnect_worker_common.models.initialize import InitializerConfig
from openconnect_worker_common.models.resource_admission import ResourceBudget, ResourceRequest
from openconnect_worker_common.models.runtime_cache import RuntimeModelAsset, RuntimeModelManifest

MINILM_REVISION = "1110a243fdf4706b3f48f1d95db1a4f5529b4d41"
MINILM_MODEL_ID = "sentence-transformers/all-MiniLM-L6-v2"


def minilm_configs(
    request: ResourceRequest,
    budget: ResourceBudget,
    *,
    allowed_hosts: list[str],
    timeout_seconds: float,
    disk_reserve_bytes: int,
) -> tuple[InitializerConfig, ServiceConfig]:
    """Return opt-in init and serving configs for the pinned English candidate.

    Values for resources, network hosts, timeout and disk reserve are explicit
    operator inputs; constructing this profile does not approve its use.
    """
    if (request.memoryMb <= 0 or request.cpuMillicores <= 0 or request.gpuRequired
            or request.vramMb != 0):
        raise ValueError("invalid_cpu_resource_request")
    manifest = RuntimeModelManifest(
        workerKind="knowledge", modelId=MINILM_MODEL_ID, immutableRevision=MINILM_REVISION,
        license="apache-2.0", assets=[
            RuntimeModelAsset(
                relativePath="model.onnx", sizeBytes=90405214,
                sha256="6fd5d72fe4589f189f8ebc006442dbb529bb7ce38f8082112682524616046452",
            ),
            RuntimeModelAsset(
                relativePath="tokenizer.json", sizeBytes=466247, kind="tokenizer",
                sha256="be50c3628f2bf5bb5e3a7f17b1f74611b2561a3a27eeab05e5aa30f411572037",
            ),
        ],
    )
    base = f"https://huggingface.co/{MINILM_MODEL_ID}/resolve/{MINILM_REVISION}/"
    init = InitializerConfig.model_validate({
        "manifest": manifest,
        "sources": {
            "model.onnx": {"url": base + "onnx/model.onnx",
                           "mediaTypes": ["application/octet-stream"]},
            "tokenizer.json": {"url": base + "tokenizer.json",
                               "mediaTypes": ["text/plain", "application/json",
                                              "application/octet-stream"]},
        },
        "allowedHosts": allowed_hosts,
        "resourceRequest": request,
        "resourceBudget": budget,
        "maxDownloadBytes": sum(asset.sizeBytes for asset in manifest.assets),
        "downloadTimeoutSeconds": timeout_seconds,
        "diskReserveBytes": disk_reserve_bytes,
    })
    service = ServiceConfig(
        manifest=manifest, profile=Profile(modelAsset="model.onnx", tokenizerAsset="tokenizer.json",
                                           dimension=384, maxTokens=256),
        resourceRequest=request, resourceBudget=budget,
    )
    return init, service
