import hashlib

import pytest
from onnx import TensorProto, helper
from tokenizers import Tokenizer, models, pre_tokenizers, processors

from openconnect_knowledge.reranker import Candidate, OnnxReranker
from openconnect_worker_common.models.runtime_cache import RuntimeModelAsset, RuntimeModelCacheManager, RuntimeModelManifest


@pytest.fixture
def engine(tmp_path):
    tokenizer = Tokenizer(models.WordLevel({"[UNK]": 0, "[PAD]": 1, "[CLS]": 2, "[SEP]": 3,
                                            "tree": 4, "wood": 5, "forest": 6}, unk_token="[UNK]"))
    tokenizer.pre_tokenizer = pre_tokenizers.Whitespace()
    tokenizer.post_processor = processors.TemplateProcessing(
        single="[CLS] $A [SEP]", pair="[CLS] $A [SEP] $B:1 [SEP]:1",
        special_tokens=[("[CLS]", 2), ("[SEP]", 3)])
    token_file = tmp_path / "tokenizer.json"
    tokenizer.save(str(token_file))
    graph = helper.make_graph([
        helper.make_node("Cast", ["input_ids"], ["ids"], to=TensorProto.FLOAT),
        helper.make_node("Cast", ["attention_mask"], ["mask"], to=TensorProto.FLOAT),
        helper.make_node("Mul", ["ids", "mask"], ["masked"]),
        helper.make_node("ReduceSum", ["masked", "axes"], ["logits"], keepdims=1),
    ], "fixture", [helper.make_tensor_value_info("input_ids", TensorProto.INT64, ["batch", "tokens"]),
                    helper.make_tensor_value_info("attention_mask", TensorProto.INT64, ["batch", "tokens"])],
        [helper.make_tensor_value_info("logits", TensorProto.FLOAT, ["batch", 1])],
        initializer=[helper.make_tensor("axes", TensorProto.INT64, [1], [1])])
    model = helper.make_model(graph, opset_imports=[helper.make_opsetid("", 17)], ir_version=9)
    blobs = {"model.onnx": model.SerializeToString(), "tokenizer.json": token_file.read_bytes()}
    manifest = RuntimeModelManifest(workerKind="knowledge", modelId="test/rerank", immutableRevision="a" * 40,
        license="mit", assets=[RuntimeModelAsset(relativePath=path, sha256=hashlib.sha256(data).hexdigest(),
                                                sizeBytes=len(data)) for path, data in blobs.items()])
    cache = RuntimeModelCacheManager(tmp_path / "cache", "knowledge")
    revision = cache.provision_or_verify(manifest,
        asset_fetcher=lambda asset, target: target.write_bytes(blobs[asset.relativePath]))
    assert revision.ready
    return OnnxReranker(cache, manifest, model_asset="model.onnx", tokenizer_asset="tokenizer.json", max_tokens=8), revision


def test_cpu_scores_pair_inputs_and_preserves_tie_order(engine):
    model, _ = engine
    result = model.rank("tree", [Candidate("a", "wood"), Candidate("b", "forest"), Candidate("c", "wood")])
    assert [item.id for item in result.candidates] == ["b", "a", "c"]
    assert result.score_type == "raw_logit" and result.model_id == "test/rerank"
    assert result.candidates[1].score == result.candidates[2].score


def test_long_pairs_are_rejected_not_silently_truncated(engine):
    model, _ = engine
    with pytest.raises(ValueError, match="token_limit_exceeded"):
        model.rank("tree tree tree", [Candidate("a", "wood wood wood")])


def test_duplicate_identifiers_and_corrupt_cache_fail_closed(engine):
    model, revision = engine
    with pytest.raises(ValueError, match="invalid_input"):
        model.rank("tree", [Candidate("a", "wood"), Candidate("a", "forest")])
    (revision.revision_dir / "assets" / "model.onnx").write_bytes(b"corrupt")
    with pytest.raises(RuntimeError, match="model_unavailable"):
        model.rank("tree", [Candidate("a", "wood")])


@pytest.mark.parametrize("candidate_id", ["  ", "bad\x00id", "\ud800"])
def test_invalid_candidate_identifiers_are_rejected_before_inference(engine, candidate_id):
    model, _ = engine
    with pytest.raises(ValueError, match="invalid_input"):
        model.rank("tree", [Candidate(candidate_id, "wood")])


@pytest.mark.parametrize("model_id", ["  ", "bad\x00model", "\ud800"])
def test_invalid_model_identity_does_not_load(engine, model_id):
    model, _ = engine
    manifest = model._manifest.model_copy(update={"modelId": model_id})
    with pytest.raises(ValueError, match="invalid_profile"):
        OnnxReranker(model._cache, manifest, model_asset="model.onnx", tokenizer_asset="tokenizer.json")
