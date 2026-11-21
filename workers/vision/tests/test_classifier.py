import hashlib
import io

import numpy as np
import onnx
import pytest
from onnx import TensorProto, helper
from PIL import Image

from openconnect_vision.classifier import OnnxImageClassifier, _prepare_image
from openconnect_worker_common.models.runtime_cache import (
    RuntimeModelAsset, RuntimeModelCacheManager, RuntimeModelManifest,
)


@pytest.fixture
def classifier(tmp_path):
    values = np.zeros((1, 1000), dtype=np.float32)
    values[0, 2] = 3
    values[0, 7] = 2
    graph = helper.make_graph([helper.make_node("Constant", [], ["logits"],
        value=onnx.numpy_helper.from_array(values))], "fixture", [
        helper.make_tensor_value_info("image", TensorProto.FLOAT, ["batch_size", 3, 224, 224])], [
        helper.make_tensor_value_info("logits", TensorProto.FLOAT, [1, 1000])])
    model = helper.make_model(graph, opset_imports=[helper.make_opsetid("", 17)], ir_version=9)
    blobs = {"model.onnx": model.SerializeToString(),
             "labels.txt": "\n".join(f"class-{index}" for index in range(1000)).encode()}
    manifest = RuntimeModelManifest(workerKind="vision", modelId="fixture/vision", immutableRevision="a" * 40,
        license="apache-2.0", assets=[RuntimeModelAsset(relativePath=name, sha256=hashlib.sha256(value).hexdigest(),
                                    sizeBytes=len(value)) for name, value in blobs.items()])
    cache = RuntimeModelCacheManager(tmp_path / "cache", "vision")
    result = cache.provision_or_verify(manifest,
        asset_fetcher=lambda asset, target: target.write_bytes(blobs[asset.relativePath]))
    assert result.ready
    engine = OnnxImageClassifier(cache, manifest, model_asset="model.onnx", labels_asset="labels.txt",
                                 taxonomy_version="fixture-v1", taxonomy_approved=True)
    return engine, result.revision_dir


def image(width=320, height=240):
    output = io.BytesIO()
    Image.new("RGB", (width, height), "white").save(output, format="PNG")
    return output.getvalue()


def test_real_onnx_cpu_classification_preserves_model_and_taxonomy(classifier):
    engine, _ = classifier
    result = engine.classify(image(), top_k=3)
    assert (result.width, result.height) == (320, 240)
    assert [item.class_index for item in result.classifications] == [2, 7, 0]
    assert result.classifications[0].label == "class-2"
    assert result.classifications[0].score > result.classifications[1].score
    assert result.model_id == "fixture/vision" and len(result.model_fingerprint) == 64


def test_preprocessing_is_bounded_and_normalized():
    result, width, height = _prepare_image(image())
    assert result.shape == (1, 3, 224, 224) and result.dtype == np.float32
    assert (width, height) == (320, 240)
    assert result[0, 0, 0, 0] == pytest.approx((1 - .485) / .229)
    with pytest.raises(ValueError):
        _prepare_image(image(1, 100))


@pytest.mark.parametrize("value", [b"", b"<html/>", b"\x89PNG\r\n\x1a\ninvalid"])
def test_invalid_images_fail_closed(value):
    with pytest.raises(ValueError):
        _prepare_image(value)


def test_corrupted_cache_refuses_inference(classifier):
    engine, revision = classifier
    (revision / "assets" / "labels.txt").write_text("corrupt")
    with pytest.raises(RuntimeError, match="model_unavailable"):
        engine.classify(image())


def test_taxonomy_requires_explicit_operator_approval(classifier, monkeypatch):
    engine, _ = classifier
    monkeypatch.setattr(engine._cache, "verify_for_request", lambda *a, **k: pytest.fail("cache accessed"))
    with pytest.raises(ValueError, match="invalid_profile"):
        OnnxImageClassifier(engine._cache, engine._manifest, model_asset="model.onnx",
                            labels_asset="labels.txt", taxonomy_version="fixture-v1")


@pytest.mark.parametrize("top_k", [0, 21, True])
def test_top_k_is_bounded(classifier, top_k):
    engine, _ = classifier
    with pytest.raises(ValueError):
        engine.classify(image(), top_k=top_k)


@pytest.mark.parametrize("taxonomy", ["  ", "bad\x00version", "x" * 129, "\ud800"])
def test_invalid_taxonomy_never_becomes_ready(classifier, taxonomy):
    engine, _ = classifier
    with pytest.raises(ValueError, match="invalid_profile"):
        OnnxImageClassifier(engine._cache, engine._manifest, model_asset="model.onnx",
                            labels_asset="labels.txt", taxonomy_version=taxonomy, taxonomy_approved=True)


@pytest.mark.parametrize("label", ["  ", "bad\x00label", "x" * 257])
def test_invalid_labels_never_become_ready(classifier, monkeypatch, label):
    engine, revision = classifier
    labels_path = revision / "assets" / "labels.txt"
    original_read = type(labels_path).read_bytes
    labels = [f"class-{index}" for index in range(1000)]
    labels[1] = label
    def read_bytes(path):
        return "\n".join(labels).encode() if path == labels_path else original_read(path)
    monkeypatch.setattr(type(labels_path), "read_bytes", read_bytes)
    with pytest.raises(ValueError, match="invalid_taxonomy"):
        OnnxImageClassifier(engine._cache, engine._manifest, model_asset="model.onnx",
                            labels_asset="labels.txt", taxonomy_version="fixture-v1", taxonomy_approved=True)
