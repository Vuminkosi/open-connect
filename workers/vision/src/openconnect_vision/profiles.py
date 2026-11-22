"""Pinned, opt-in candidate profiles; constructing these never downloads data."""

from openconnect_worker_common.models.initialize import InitializerConfig
from openconnect_worker_common.models.resource_admission import ResourceBudget, ResourceRequest
from openconnect_worker_common.models.runtime_cache import RuntimeModelAsset, RuntimeModelManifest

MOBILENET_REVISION = "c5cae0f1942c8d7727372c7b1f6b485aa39e4421"
MOBILENET_MODEL_ID = "onnx/models/mobilenetv2-12"
MOBILENET_TAXONOMY = "imagenet-1k-c5cae0f1942c8d7727372c7b1f6b485aa39e4421"
CLIP_REVISION = "6b7c523703ae26e39be4b03774966f8571c7f523"
CLIP_MODEL_ID = "Xenova/clip-vit-base-patch32"


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


def clip_initializer(request: ResourceRequest, budget: ResourceBudget, *, allowed_hosts: list[str],
                     timeout_seconds: float, disk_reserve_bytes: int,
                     artifact_license_reviewed: bool = False) -> InitializerConfig:
    """Plan a paired CLIP export after artifact-level license review; never download here.

    This candidate is not evidence that the ONNX outputs share a usable projection
    space. A serving adapter must verify their graph contracts and retrieval quality.
    """
    if artifact_license_reviewed is not True:
        raise ValueError("artifact_license_review_required")
    if request.memoryMb <= 0 or request.cpuMillicores <= 0 or request.gpuRequired or request.vramMb != 0:
        raise ValueError("invalid_cpu_resource_request")
    specs = (
        ("onnx/vision_model_quantized.onnx", 89117001,
         "583fd1110a514667812fee7d684952aaf82a99b959760c8d7dca7e0ab9839299", "weights"),
        ("onnx/text_model_quantized.onnx", 64504507,
         "73baab855d406190da9faa498cfedf65f15cf309f4cc7385b7b032e6d08e5c3a", "weights"),
        ("tokenizer.json", 2224119, "f7f3b7af117d467b58374797691a6438d3e6b9e9cef800dfd5dced7f697a90cd", "config"),
        ("preprocessor_config.json", 520,
         "6f638fb9401a6d6296feff533ee7efe657b787c49f954f82f5906b36ef2a1b1f", "config"),
        ("config.json", 4524, "493ef57ff783e42d1530c91b53469b7fdf8db8a9c1408e86998fcb7899a4f495", "config"),
        ("tokenizer_config.json", 775, "60ba2912bc6344c94bc16bbdec27fa1209409167b6f2fdf3cfe9e65462ea3967", "config"),
        ("special_tokens_map.json", 472,
         "c4864a9376a8401918425bed71fc14fc0e81f9b59ec45c1cf96cccb2df508eac", "config"),
    )
    manifest = RuntimeModelManifest(workerKind="vision", modelId=CLIP_MODEL_ID, immutableRevision=CLIP_REVISION,
        license="mit", quantization="onnx-quantized", assets=[
            RuntimeModelAsset(relativePath=path, sizeBytes=size, sha256=sha, kind=kind)
            for path, size, sha, kind in specs
        ])
    base = f"https://huggingface.co/{CLIP_MODEL_ID}/resolve/{CLIP_REVISION}/"
    return InitializerConfig.model_validate({
        "manifest": manifest,
        "sources": {path: {"url": base + path,
                           "mediaTypes": ["application/octet-stream", "application/json", "text/plain"]}
                    for path, _, _, _ in specs},
        "allowedHosts": allowed_hosts, "resourceRequest": request, "resourceBudget": budget,
        "maxDownloadBytes": sum(asset.sizeBytes for asset in manifest.assets),
        "downloadTimeoutSeconds": timeout_seconds, "diskReserveBytes": disk_reserve_bytes,
    })
