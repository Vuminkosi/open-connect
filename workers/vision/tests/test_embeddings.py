import io
from types import SimpleNamespace

import numpy as np
import pytest
from PIL import Image

from openconnect_vision import embeddings
from openconnect_vision.profiles import clip_initializer
from openconnect_worker_common.models.resource_admission import ResourceBudget, ResourceRequest


def test_clip_image_applies_bounded_preprocessing():
    stream = io.BytesIO()
    Image.new("RGB", (400, 200), (255, 0, 0)).save(stream, format="PNG")
    result = embeddings._clip_image(stream.getvalue())
    assert result.shape == (1, 3, 224, 224) and result.dtype == np.float32
    assert result[0, 0, 0, 0] == pytest.approx((1 - .48145466) / .26862954)
    assert result[0, 1, 0, 0] == pytest.approx(-.4578275 / .26130258)
    with pytest.raises(ValueError, match="unsupported_format"):
        embeddings._clip_image(b"not an image")


def test_paired_adapter_rejects_unapproved_and_tampered_manifest(tmp_path):
    plan = clip_initializer(ResourceRequest(memoryMb=1, cpuMillicores=1),
        ResourceBudget(memoryMbAvailable=1, vramMbAvailable=0, cpuMillicoresAvailable=1),
        allowed_hosts=["huggingface.co"], timeout_seconds=1, disk_reserve_bytes=0,
        artifact_license_reviewed=True)
    with pytest.raises(ValueError, match="artifact_license_review_required"):
        embeddings.PairedClipEncoder(None, plan.manifest)
    tampered = plan.manifest.model_copy(deep=True)
    tampered.assets[0].sha256 = "0" * 64
    with pytest.raises(ValueError, match="invalid_profile"):
        embeddings.PairedClipEncoder(None, tampered, artifact_license_reviewed=True)


def test_paired_adapter_normalizes_shared_space_and_rejects_bad_output(monkeypatch, tmp_path):
    plan = clip_initializer(ResourceRequest(memoryMb=1, cpuMillicores=1),
        ResourceBudget(memoryMbAvailable=1, vramMbAvailable=0, cpuMillicoresAvailable=1),
        allowed_hosts=["huggingface.co"], timeout_seconds=1, disk_reserve_bytes=0,
        artifact_license_reviewed=True)

    class Cache:
        valid = True

        def verify_for_request(self, *args, **kwargs):
            return SimpleNamespace(ready=self.valid, revision_dir=tmp_path, fingerprint="a" * 64)

    class Tokenizer:
        def no_truncation(self): pass
        def no_padding(self): pass
        def encode(self, text): return SimpleNamespace(ids=[1, 2], attention_mask=[1, 1])

    class Session:
        def __init__(self, path, **kwargs):
            self.vision = "vision_model" in path

        def get_inputs(self):
            return [SimpleNamespace(name=name) for name in (["pixel_values"] if self.vision else ["input_ids"])]

        def get_outputs(self):
            return [SimpleNamespace(name="image_embeds" if self.vision else "text_embeds")]

        def run(self, names, feeds):
            return [np.ones((1, embeddings.DIMENSION), dtype=np.float32)]

    monkeypatch.setattr(embeddings, "Tokenizer", SimpleNamespace(from_file=lambda _: Tokenizer()))
    monkeypatch.setattr(embeddings.onnx, "load_model", lambda *args, **kwargs: None)
    monkeypatch.setattr(embeddings, "_reject_external_tensors", lambda _: None)
    monkeypatch.setattr(embeddings.ort, "InferenceSession", Session)
    cache = Cache()
    engine = embeddings.PairedClipEncoder(cache, plan.manifest, artifact_license_reviewed=True)
    stream = io.BytesIO()
    Image.new("RGB", (32, 32), "red").save(stream, format="PNG")
    image, text = engine.embed_image(stream.getvalue()), engine.embed_text("red image")
    assert image.space_id == text.space_id and image.dimension == text.dimension == 512
    assert np.linalg.norm(image.vector) == pytest.approx(1)
    with pytest.raises(RuntimeError, match="model_output_invalid"):
        engine._result(np.zeros((1, 512)))
    cache.valid = False
    with pytest.raises(RuntimeError, match="model_unavailable"):
        engine.embed_text("red image")
