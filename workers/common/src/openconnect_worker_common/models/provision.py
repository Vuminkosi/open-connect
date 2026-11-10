"""Provision planning: validate *what* would be provisioned, without fetching anything.

Provisioning performs no model download. These models only describe and
validate a provisioning request (paths, digests, sizes) so workers fail closed
on unsafe or inconsistent requests before touching the filesystem.
"""

from pathlib import Path

from pydantic import BaseModel, ConfigDict, Field

from openconnect_worker_common.models.manifest import ModelPackage


class ProvisionRequest(BaseModel):
    """Request to make a pinned model package available under the model directory."""

    model_config = ConfigDict(extra="forbid")

    package: ModelPackage
    modelDir: str = Field(min_length=1, max_length=1024)
    workerId: str = Field(min_length=1, max_length=256)


def is_safe_target(model_dir: str, relative_path: str) -> bool:
    """Return True when the relative path stays inside the model directory."""
    base = Path(model_dir)
    if not base.is_absolute():
        return False
    candidate = (base / relative_path).resolve()
    try:
        candidate.relative_to(base.resolve())
    except ValueError:
        return False
    return True


def plan_provision(request: ProvisionRequest) -> list[str]:
    """Return the ordered local file targets for a request.

    Raises ``ValueError`` when any file would escape the model directory.
    Performs no I/O and no download.
    """
    targets: list[str] = []
    for model_file in request.package.files:
        if not is_safe_target(request.modelDir, model_file.relativePath):
            raise ValueError(
                f"unsafe provisioning path: {model_file.relativePath!r} escapes modelDir"
            )
        targets.append(str(Path(request.modelDir) / model_file.relativePath))
    return targets
