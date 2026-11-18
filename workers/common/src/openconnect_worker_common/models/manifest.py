"""Local model inventory: files present on disk. No downloads, no network."""

import hashlib
from pathlib import Path

from pydantic import BaseModel, ConfigDict, Field


class ModelFile(BaseModel):
    """One model file expected under the local model directory."""

    model_config = ConfigDict(extra="forbid")

    relativePath: str = Field(min_length=1, max_length=1024)
    sha256: str = Field(pattern=r"^[0-9a-f]{64}$")
    sizeBytes: int = Field(ge=0)


class ModelPackage(BaseModel):
    """A downloadable unit pinned by digest; provisioning only uses local files."""

    model_config = ConfigDict(extra="forbid")

    name: str = Field(min_length=1, max_length=256)
    version: str = Field(min_length=1, max_length=64)
    files: list[ModelFile] = Field(min_length=1)


def hash_file(path: Path, chunk_size: int = 1024 * 1024) -> str:
    """Return the hex SHA-256 of a local file. Reads only; never downloads."""
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        while chunk := handle.read(chunk_size):
            digest.update(chunk)
    return digest.hexdigest()
