"""Model manifest wire contract: which models a worker offers, without fetching them."""

from typing import Literal

from pydantic import BaseModel, ConfigDict, Field, model_validator

SCHEMA_VERSION = "openconnect.v1"


class ModelEntry(BaseModel):
    """A single model offered by a worker. Descriptive only; never triggers a download."""

    model_config = ConfigDict(extra="forbid")

    name: str = Field(min_length=1, max_length=256)
    version: str = Field(min_length=1, max_length=64)
    digest: str = Field(pattern=r"^[0-9a-f]{64}$")
    sizeBytes: int = Field(ge=0)
    localOnly: bool = True


class ModelManifest(BaseModel):
    """Advertised set of models for one worker."""

    model_config = ConfigDict(extra="forbid")

    schemaVersion: Literal["openconnect.v1"] = SCHEMA_VERSION
    workerId: str = Field(min_length=1, max_length=256)
    revision: str = Field(min_length=1, max_length=128)
    models: list[ModelEntry] = Field(default_factory=list)

    @model_validator(mode="after")
    def _unique_models(self) -> "ModelManifest":
        seen = {(m.name, m.version) for m in self.models}
        if len(seen) != len(self.models):
            raise ValueError("models must have unique (name, version) pairs")
        return self
