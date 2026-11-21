"""Opt-in acceptance for an already provisioned, reviewed CLIP test cache."""

import io
import os
from pathlib import Path

import numpy as np
import pytest
from PIL import Image

from openconnect_vision.embeddings import PairedClipEncoder
from openconnect_vision.profiles import clip_initializer
from openconnect_worker_common.models.resource_admission import ResourceBudget, ResourceRequest
from openconnect_worker_common.models.runtime_cache import RuntimeModelCacheManager


@pytest.mark.skipif(os.getenv("OC_RUN_CLIP_REAL_TEST") != "1", reason="requires an approved, pre-provisioned test cache")
def test_real_clip_cross_modal_similarity_and_offline_reload(monkeypatch):
    cache_dir = os.environ["OC_CLIP_TEST_CACHE"]
    monkeypatch.setattr("socket.getaddrinfo", lambda *args, **kwargs: pytest.fail("network accessed"))
    plan = clip_initializer(ResourceRequest(memoryMb=2048, cpuMillicores=1000),
        ResourceBudget(memoryMbAvailable=4096, vramMbAvailable=0, cpuMillicoresAvailable=2000),
        allowed_hosts=["huggingface.co"], timeout_seconds=300, disk_reserve_bytes=0,
        artifact_license_reviewed=True)
    cache = RuntimeModelCacheManager(Path(cache_dir), "vision")
    engine = PairedClipEncoder(cache, plan.manifest, artifact_license_reviewed=True)
    buffer = io.BytesIO()
    Image.new("RGB", (224, 224), "red").save(buffer, format="PNG")
    image = engine.embed_image(buffer.getvalue())
    red = engine.embed_text("a red square")
    blue = engine.embed_text("a blue square")
    assert image.space_id == red.space_id == blue.space_id
    assert np.dot(image.vector, red.vector) > np.dot(image.vector, blue.vector)
    assert np.linalg.norm(image.vector) == pytest.approx(1, abs=1e-6)
    reloaded = PairedClipEncoder(cache, plan.manifest, artifact_license_reviewed=True)
    after = reloaded.embed_text("a red square")
    assert after.space_id == red.space_id
    assert after.vector == pytest.approx(red.vector, abs=1e-6)
