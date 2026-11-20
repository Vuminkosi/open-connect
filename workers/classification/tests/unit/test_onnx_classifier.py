import hashlib
import json
from dataclasses import replace

import numpy as np
import pytest
from fastapi.testclient import TestClient
from onnx import TensorProto, helper, numpy_helper
from tokenizers import Tokenizer, models, pre_tokenizers

import openconnect_classification.app as app_module
from openconnect_classification.onnx_classifier import BundleUnavailableError, TokenLimitError, load_bundle


def bundle(tmp_path, **overrides):
    tokenizer = Tokenizer(models.WordLevel({"[UNK]": 0, "hello": 1, "world": 2}, unk_token="[UNK]"))
    tokenizer.pre_tokenizer = pre_tokenizers.Whitespace()
    tokenizer_bytes = tokenizer.to_str().encode()
    (tmp_path / "tokenizer.json").write_bytes(tokenizer_bytes)
    inputs = [helper.make_tensor_value_info("input_ids", TensorProto.INT64, [1, "tokens"])]
    outputs = [helper.make_tensor_value_info("logits", TensorProto.FLOAT, [1, 4])]
    axes = numpy_helper.from_array(np.asarray([1], dtype=np.int64), name="axes")
    nodes = [helper.make_node("Cast", ["input_ids"], ["cast"], to=TensorProto.FLOAT),
             helper.make_node("ReduceSum", ["cast", "axes"], ["sum"], keepdims=1),
             helper.make_node("Neg", ["sum"], ["negative"]),
             helper.make_node("Concat", ["sum", "negative", "sum", "negative"], ["logits"], axis=1)]
    model = helper.make_model(helper.make_graph(nodes, "synthetic", inputs, outputs, [axes]),
                              opset_imports=[helper.make_opsetid("", 13)])
    model.ir_version = 10
    model_bytes = model.SerializeToString()
    (tmp_path / "model.onnx").write_bytes(model_bytes)
    contract = {"schemaVersion": "openconnect.classification.onnx/v1", "modelId": "synthetic-test-model",
                "revision": "synthetic-v1", "license": "apache-2.0", "labelSetVersion": "v1",
                "labels": ["safe", "spam", "toxic", "sensitive"], "activation": "sigmoid", "maxTokens": 8,
                "outputName": "logits", "modelSha256": hashlib.sha256(model_bytes).hexdigest(),
                "tokenizerSha256": hashlib.sha256(tokenizer_bytes).hexdigest()}
    contract.update(overrides)
    encoded = json.dumps(contract, separators=(",", ":")).encode()
    (tmp_path / "classification.onnx.json").write_bytes(encoded)
    return hashlib.sha256(encoded).hexdigest()


def test_real_onnx_inference_uses_explicit_label_mapping_and_changes_with_input(monkeypatch, tmp_path):
    digest = bundle(tmp_path)
    model = load_bundle(str(tmp_path), digest, False)
    assert model.scores("hello")["safe"] != model.scores("hello world")["safe"]
    with pytest.raises(TokenLimitError):
        model.scores(" ".join(["hello"] * 9))
    monkeypatch.setenv("HF_HUB_OFFLINE", "1")
    monkeypatch.setenv("OC_CLASSIFICATION_BUNDLE_SHA256", digest)
    monkeypatch.setattr(app_module, "settings", replace(app_module.settings, worker_token="test-token", model_dir=tmp_path,
                        fake_engine=False, is_production=False, model_device="cpu"))
    client = TestClient(app_module.app)
    assert client.get("/health/ready").json()["modelRevision"] == "synthetic-v1"
    capability = client.get("/internal/v1/capabilities", headers={"Authorization": "Bearer test-token"}).json()
    assert capability["engine"] == "real" and capability["capabilities"][0]["labelSets"] == ["v1"]
    response = client.post("/internal/v1/execute", headers={"Authorization": "Bearer test-token"},
                           json={"taskId": "fixture", "input": {"text": "hello world"}})
    assert response.status_code == 200
    assert response.json()["result"]["engine"] == "real"
    assert response.json()["result"]["modelRevision"] == "synthetic-v1"
    assert client.post("/internal/v1/execute", headers={"Authorization": "Bearer test-token"},
                       json={"taskId": "fixture", "input": {"text": " ".join(["hello"] * 9)}}).status_code == 413


def test_bundle_fails_closed_on_wrong_pin_digest_and_taxonomy(tmp_path):
    digest = bundle(tmp_path)
    with pytest.raises(BundleUnavailableError):
        load_bundle(str(tmp_path), "0" * 64, False)
    (tmp_path / "model.onnx").write_bytes(b"tampered")
    with pytest.raises(BundleUnavailableError):
        load_bundle(str(tmp_path), digest, False)
    digest = bundle(tmp_path, labels=["safe", "spam", "toxic", "unknown"])
    with pytest.raises(BundleUnavailableError):
        load_bundle(str(tmp_path), digest, False)
