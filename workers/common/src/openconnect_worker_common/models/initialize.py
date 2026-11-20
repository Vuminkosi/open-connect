"""Explicit startup initialization for operator-controlled model manifests."""

from __future__ import annotations

import json
import os
import re
import sys
from pathlib import Path
from typing import Literal

from pydantic import BaseModel, ConfigDict, Field, ValidationError, model_validator

from openconnect_worker_common.models.download import AssetSource, StartupAssetFetcher
from openconnect_worker_common.models.resource_admission import (
    AdmissionVerdict,
    ResourceBudget,
    ResourceRequest,
    admit,
)
from openconnect_worker_common.models.runtime_cache import (
    CacheProvisionResult,
    RuntimeModelCacheManager,
    RuntimeModelManifest,
)


class SourceConfig(BaseModel):
    model_config = ConfigDict(extra="forbid")

    url: str = Field(min_length=1, max_length=2048)
    mediaTypes: list[str] = Field(min_length=1, max_length=16)


class InitializerConfig(BaseModel):
    """Deployment-owned manifest, sources and measured resource request."""

    model_config = ConfigDict(extra="forbid")

    manifest: RuntimeModelManifest
    sources: dict[str, SourceConfig]
    allowedHosts: list[str] = Field(min_length=1, max_length=32)
    resourceRequest: ResourceRequest
    resourceBudget: ResourceBudget
    maxDownloadBytes: int = Field(gt=0)
    downloadTimeoutSeconds: float = Field(gt=0, le=3600, allow_inf_nan=False)
    diskReserveBytes: int = Field(default=0, ge=0)
    licenseAccepted: bool = False
    commercialUse: bool = False

    @model_validator(mode="after")
    def validate_manifest(self) -> "InitializerConfig":
        if not re.fullmatch(r"[a-z0-9][a-z0-9-]{0,63}", self.manifest.workerKind):
            raise ValueError("invalid_worker_kind")
        if not re.fullmatch(r"(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{64})", self.manifest.immutableRevision):
            raise ValueError("revision_must_be_immutable_digest")
        paths = [asset.relativePath for asset in self.manifest.assets]
        if len(set(paths)) != len(paths) or set(paths) != set(self.sources):
            raise ValueError("invalid_asset_sources")
        if any(asset.sizeBytes <= 0 for asset in self.manifest.assets):
            raise ValueError("empty_asset")
        return self


def initialize(
    config: InitializerConfig,
    cache_dir: Path,
    *,
    provisioning: Literal["verify-existing-only", "first-start-if-missing"],
    offline: bool,
) -> CacheProvisionResult:
    """Apply admission before provisioning or verifying an approved revision."""
    if provisioning not in {"verify-existing-only", "first-start-if-missing"}:
        raise ValueError("invalid_provisioning_mode")
    if admit(config.resourceRequest, config.resourceBudget).verdict != AdmissionVerdict.ADMIT:
        return CacheProvisionResult(
            ready=False, status="unavailable", reason="resource_denied", fingerprint=None,
            model_id=config.manifest.modelId, revision=config.manifest.immutableRevision,
            revision_dir=None,
        )
    fetcher = None
    if not offline and provisioning == "first-start-if-missing":
        fetcher = StartupAssetFetcher(
            config.manifest,
            {path: AssetSource(source.url, frozenset(source.mediaTypes))
             for path, source in config.sources.items()},
            frozenset(config.allowedHosts), max_bytes=config.maxDownloadBytes,
            timeout_seconds=config.downloadTimeoutSeconds, reserve_bytes=config.diskReserveBytes,
        )
    return RuntimeModelCacheManager(cache_dir, config.manifest.workerKind).provision_or_verify(
        config.manifest, provisioning=provisioning, offline=offline,
        license_accepted=config.licenseAccepted, commercial_use=config.commercialUse,
        asset_fetcher=fetcher,
    )


def main() -> int:
    """Initialize once; output only a safe status and never asset locations."""
    mode = os.environ.get("OC_MODEL_PROVISIONING", "first-start-if-missing")
    if mode == "disabled":
        print(json.dumps({"ready": False, "reason": "worker_disabled"}))
        return 0
    if mode not in {"verify-existing-only", "first-start-if-missing"}:
        print(json.dumps({"ready": False, "reason": "invalid_request"}))
        return 1
    offline_value = os.environ.get("OC_MODEL_OFFLINE", "false").lower()
    if offline_value not in {"true", "false"}:
        print(json.dumps({"ready": False, "reason": "invalid_request"}))
        return 1
    try:
        path = Path(os.environ["OC_MODEL_MANIFEST_PATH"])
        if path.stat().st_size > 1024 * 1024:
            raise ValueError("manifest_too_large")
        config = InitializerConfig.model_validate_json(path.read_bytes())
        result = initialize(config, Path(os.environ.get("OC_MODEL_CACHE_DIR", "/models")),
                            provisioning=mode, offline=offline_value == "true")
    except (KeyError, OSError, ValueError, ValidationError):
        print(json.dumps({"ready": False, "reason": "invalid_request"}))
        return 1
    print(json.dumps({"ready": result.ready, "reason": result.reason}))
    return 0 if result.ready else 1


if __name__ == "__main__":
    sys.exit(main())
