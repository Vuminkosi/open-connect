"""Model manifest contract tests."""

import pytest
from pydantic import ValidationError

from openconnect_worker_common.contracts.model_manifest import ModelEntry, ModelManifest

DIGEST = "a" * 64


def _entry(name: str = "rules", version: str = "1.0.0") -> ModelEntry:
    return ModelEntry(name=name, version=version, digest=DIGEST, sizeBytes=10)


def test_manifest_accepts_unique_models() -> None:
    manifest = ModelManifest(workerId="w-1", revision="r1", models=[_entry()])
    assert manifest.schemaVersion == "openconnect.v1"


def test_manifest_rejects_duplicate_models() -> None:
    with pytest.raises(ValidationError):
        ModelManifest(workerId="w-1", revision="r1", models=[_entry(), _entry()])


def test_manifest_rejects_bad_digest() -> None:
    with pytest.raises(ValidationError):
        ModelEntry(name="m", version="1", digest="not-a-digest", sizeBytes=1)
