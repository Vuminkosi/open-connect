"""Load an explicitly reviewed local extraction model from the shared verified cache."""

from __future__ import annotations

import os
import re
from pathlib import Path

from pydantic import BaseModel, ConfigDict, Field

from openconnect_inference.extraction_api import ExtractionRuntime
from openconnect_inference.extraction_llama import LlamaExtractionProvider
from openconnect_worker_common.models.resource_admission import AdmissionVerdict, ResourceBudget, ResourceRequest, admit
from openconnect_worker_common.models.runtime_cache import RuntimeModelCacheManager, RuntimeModelManifest


class ExtractionConfig(BaseModel):
    model_config = ConfigDict(extra="forbid")

    manifest: RuntimeModelManifest
    modelAsset: str = Field(min_length=1, max_length=512)
    resourceRequest: ResourceRequest
    resourceBudget: ResourceBudget
    artifactReviewed: bool = False
    licenseAccepted: bool = False
    commercialUse: bool = False
    contextTokens: int = Field(default=4096, ge=512, le=8192)
    threads: int = Field(default=1, ge=1, le=16)


def load_runtime() -> ExtractionRuntime | None:
    """Load once at startup; never download, create caches, or log configuration."""
    if (os.environ.get("OC_EXTRACTION_ENABLED", "false").lower() != "true"
            or os.environ.get("OC_MODEL_PROVISIONING", "first-start-if-missing") == "disabled"
            or os.environ.get("OC_WORKER_ENGINE", "real").lower() != "real"
            or os.environ.get("OC_MODEL_DEVICE", "cpu").lower() not in {"cpu", "auto"}):
        return None
    try:
        path = Path(os.environ["OC_EXTRACTION_CONFIG_PATH"])
        if path.stat().st_size > 1024 * 1024:
            return None
        config = ExtractionConfig.model_validate_json(path.read_bytes())
        request = config.resourceRequest
        assets = [asset for asset in config.manifest.assets if asset.relativePath == config.modelAsset]
        if (not config.artifactReviewed or config.manifest.workerKind != "inference"
                or not re.fullmatch(r"[0-9a-fA-F]{40}|[0-9a-fA-F]{64}", config.manifest.immutableRevision)
                or len(assets) != 1 or assets[0].kind != "weights" or not config.modelAsset.endswith(".gguf")
                or request.memoryMb <= 0 or request.cpuMillicores <= 0 or request.gpuRequired or request.vramMb != 0
                or config.threads * 1000 > request.cpuMillicores
                or admit(request, config.resourceBudget).verdict != AdmissionVerdict.ADMIT):
            return None
        cache = RuntimeModelCacheManager(Path(os.environ.get("OC_MODEL_CACHE_DIR", "/models")), "inference")
        manifest = config.manifest.model_copy(deep=True)
        verified = cache.verify_for_request(manifest, license_accepted=config.licenseAccepted,
                                            commercial_use=config.commercialUse)
        if not verified.ready or verified.revision_dir is None or verified.fingerprint is None:
            return None
        fingerprint = verified.fingerprint

        def verify() -> bool:
            current = cache.verify_for_request(manifest, license_accepted=config.licenseAccepted,
                                               commercial_use=config.commercialUse)
            return current.ready and current.fingerprint == fingerprint

        from llama_cpp import Llama

        model = Llama(model_path=str(verified.revision_dir / "assets" / config.modelAsset),
                      n_ctx=config.contextTokens, n_threads=config.threads, n_gpu_layers=0, verbose=False)
        return ExtractionRuntime(LlamaExtractionProvider(model), verify)
    except Exception:
        return None
