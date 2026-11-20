import hashlib
from pathlib import Path

import onnx
import pytest
from onnx import TensorProto, helper
from tokenizers import Tokenizer, models, pre_tokenizers

from openconnect_knowledge.embeddings import (
    EmbeddingProfile,
    OnnxEmbeddingEngine,
    _reject_external_tensors,
)
from openconnect_worker_common.models.runtime_cache import (
    RuntimeModelAsset,
    RuntimeModelCacheManager,
    RuntimeModelManifest,
)


@pytest.fixture
def cached_model(tmp_path: Path):
    tokenizer = Tokenizer(models.WordLevel(
        {"[UNK]": 0, "[PAD]": 1, "query": 2, "passage": 3, ":": 4,
         "tree": 5, "wood": 6, "forest": 7}, unk_token="[UNK]"
    ))
    tokenizer.pre_tokenizer = pre_tokenizers.Whitespace()
    token_file = tmp_path / "tokenizer.json"
    tokenizer.save(str(token_file))
    graph = helper.make_graph([
        helper.make_node("Cast", ["input_ids"], ["float_ids"], to=TensorProto.FLOAT),
        helper.make_node("Cast", ["attention_mask"], ["float_mask"], to=TensorProto.FLOAT),
        helper.make_node("Unsqueeze", ["float_ids", "axes"], ["ids3"]),
        helper.make_node("Unsqueeze", ["float_mask", "axes"], ["mask3"]),
        helper.make_node("Concat", ["ids3", "mask3", "ids3"], ["hidden"], axis=2),
    ], "fixture", [
        helper.make_tensor_value_info("input_ids", TensorProto.INT64, ["batch", "tokens"]),
        helper.make_tensor_value_info("attention_mask", TensorProto.INT64, ["batch", "tokens"]),
    ], [helper.make_tensor_value_info("hidden", TensorProto.FLOAT, ["batch", "tokens", 3])],
        initializer=[helper.make_tensor("axes", TensorProto.INT64, [1], [2])])
    model = helper.make_model(graph, opset_imports=[helper.make_opsetid("", 17)], ir_version=9)
    blobs = {"model.onnx": model.SerializeToString(), "tokenizer.json": token_file.read_bytes()}
    manifest = RuntimeModelManifest(workerKind="knowledge", modelId="test/fixture",
        immutableRevision="a" * 40, license="mit", assets=[
            RuntimeModelAsset(relativePath=path, sha256=hashlib.sha256(data).hexdigest(),
                              sizeBytes=len(data)) for path, data in blobs.items()
        ])
    cache = RuntimeModelCacheManager(tmp_path / "cache", "knowledge")
    result = cache.provision_or_verify(manifest,
        asset_fetcher=lambda asset, target: target.write_bytes(blobs[asset.relativePath]))
    assert result.ready
    return cache, manifest, result.revision_dir


def test_real_onnx_cpu_embeddings_and_restart(cached_model) -> None:
    cache, manifest, _ = cached_model
    profile = EmbeddingProfile("model.onnx", "tokenizer.json", dimension=3, max_tokens=3)
    engine = OnnxEmbeddingEngine(cache, manifest, profile)
    batch = engine.embed(["tree wood", "forest tree wood forest"], input_kind="query")
    assert len(batch.items) == 2
    assert batch.items[0].norm == pytest.approx(1.0)
    assert [item.truncated for item in batch.items] == [False, True]
    assert len(batch.items[0].vector) == 3
    restarted = OnnxEmbeddingEngine(cache, manifest, profile).embed(["tree wood"], input_kind="query")
    assert restarted.items[0].vector == pytest.approx(batch.items[0].vector)
    assert restarted.space_id == batch.space_id


def test_e5_prefix_and_space_separation(cached_model) -> None:
    cache, manifest, _ = cached_model
    profile = EmbeddingProfile("model.onnx", "tokenizer.json", 3, 8, "e5")
    engine = OnnxEmbeddingEngine(cache, manifest, profile)
    query = engine.embed(["tree"], input_kind="query")
    passage = engine.embed(["tree"], input_kind="passage")
    assert query.items[0].vector != passage.items[0].vector
    assert query.space_id == passage.space_id
    other = OnnxEmbeddingEngine(cache, manifest, EmbeddingProfile("model.onnx", "tokenizer.json", 3, 8))
    assert other.embed(["tree"], input_kind="query").space_id != query.space_id


def test_dimension_mismatch_and_tampering_fail_closed(cached_model) -> None:
    cache, manifest, revision = cached_model
    engine = OnnxEmbeddingEngine(cache, manifest, EmbeddingProfile("model.onnx", "tokenizer.json", 4, 8))
    with pytest.raises(RuntimeError, match="model_output_invalid"):
        engine.embed(["tree"], input_kind="query")
    (revision / "assets" / "model.onnx").write_bytes(b"corrupt")
    with pytest.raises(RuntimeError, match="model_unavailable"):
        engine.embed(["tree"], input_kind="query")


@pytest.mark.parametrize("texts", [[], [""], ["x" * 8193], ["x"] * 33])
def test_invalid_batches_rejected(cached_model, texts) -> None:
    cache, manifest, _ = cached_model
    engine = OnnxEmbeddingEngine(cache, manifest, EmbeddingProfile("model.onnx", "tokenizer.json", 3, 8))
    with pytest.raises(ValueError, match="invalid_input"):
        engine.embed(texts, input_kind="query")


def test_external_tensor_references_rejected() -> None:
    tensor = onnx.TensorProto()
    tensor.data_location = onnx.TensorProto.EXTERNAL
    entry = tensor.external_data.add()
    entry.key = "location"
    entry.value = "../../outside"
    graph = onnx.GraphProto()
    graph.initializer.append(tensor)
    with pytest.raises(ValueError, match="external_tensor_unsupported"):
        _reject_external_tensors(graph)
