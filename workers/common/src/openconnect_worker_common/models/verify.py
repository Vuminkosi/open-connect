"""Verification: compare observed local files against the pinned manifest."""

from pydantic import BaseModel, ConfigDict, Field

from openconnect_worker_common.models.manifest import ModelFile


class FileVerification(BaseModel):
    """Per-file verification outcome."""

    model_config = ConfigDict(extra="forbid")

    relativePath: str = Field(min_length=1)
    digestMatch: bool
    sizeMatch: bool

    @property
    def verified(self) -> bool:
        return self.digestMatch and self.sizeMatch


class VerificationResult(BaseModel):
    """Aggregate verification outcome for a model package."""

    model_config = ConfigDict(extra="forbid")

    name: str = Field(min_length=1)
    version: str = Field(min_length=1)
    files: list[FileVerification] = Field(default_factory=list)

    @property
    def verified(self) -> bool:
        return bool(self.files) and all(f.verified for f in self.files)


def verify_file(
    expected: ModelFile, actual_sha256: str, actual_size_bytes: int
) -> FileVerification:
    """Pure comparison of observed file identity against the manifest (no I/O)."""
    return FileVerification(
        relativePath=expected.relativePath,
        digestMatch=expected.sha256 == actual_sha256.lower(),
        sizeMatch=expected.sizeBytes == actual_size_bytes,
    )
