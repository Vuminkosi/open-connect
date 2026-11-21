"""Optional offline runtime smoke test; the random model has no translation quality."""

import hashlib
import os
import shutil
from dataclasses import replace

import pytest

from openconnect_nllb.decoder import OfflineNllbDecoder
from openconnect_nllb.decoder_bundle import resolve_decoder_bundle
from openconnect_nllb.languages import SUPPORTED_LANGUAGES
from openconnect_nllb.runtime_cache import RuntimeModelAsset, RuntimeModelCacheManager, RuntimeModelManifest


@pytest.mark.skipif(
    os.environ.get("OC_NLLB_SYNTHETIC_DECODER_TEST") != "1",
    reason="Requires the explicitly provisioned pinned torch and transformers runtime.",
)
def test_offline_decoder_loads_and_generates_with_synthetic_model(tmp_path, monkeypatch):
    torch = pytest.importorskip("torch")
    transformers = pytest.importorskip("transformers")
    monkeypatch.setenv("HF_HUB_OFFLINE", "1")
    monkeypatch.setenv("TRANSFORMERS_OFFLINE", "1")
    torch.manual_seed(42)
    vocabulary = ["<s>", "<pad>", "</s>", "<unk>", "<mask>", "h", "e", "l", "o", "▁", *SUPPORTED_LANGUAGES]
    tokenizer = transformers.NllbTokenizer(
        vocab={token: index for index, token in enumerate(vocabulary)}, merges=[], src_lang="eng_Latn"
    )
    model = transformers.M2M100ForConditionalGeneration(transformers.M2M100Config(
        vocab_size=len(tokenizer), d_model=16, encoder_layers=1, decoder_layers=1,
        encoder_attention_heads=2, decoder_attention_heads=2, encoder_ffn_dim=32,
        decoder_ffn_dim=32, max_position_embeddings=1024, decoder_start_token_id=2,
        bos_token_id=0, pad_token_id=1, eos_token_id=2, tie_word_embeddings=False,
    ))
    model.eval()
    # Force a short, deterministic synthetic output; this is not a trained model.
    with torch.inference_mode():
        inputs = tokenizer("Hello.", return_tensors="pt")
        target = tokenizer.convert_tokens_to_ids("fra_Latn")
        hidden = model(**inputs, decoder_input_ids=torch.tensor([[2, target]]),
                       output_hidden_states=True).decoder_hidden_states[-1][0, -1]
        model.lm_head.weight.zero_()
        model.lm_head.weight[5].copy_(hidden * 1000)
    model.generation_config.eos_token_id = 5
    source = tmp_path / "synthetic-source"
    source.mkdir()
    tokenizer.save_pretrained(source)
    model.save_pretrained(source, safe_serialization=True)
    kinds = {"config.json": "config", "tokenizer_config.json": "config", "model.safetensors": "weights", "tokenizer.json": "tokenizer"}
    assets = []
    for file in source.iterdir():
        content = file.read_bytes()
        assets.append(RuntimeModelAsset(relativePath=file.name, sha256=hashlib.sha256(content).hexdigest(),
                                        sizeBytes=len(content), kind=kinds.get(file.name, "config")))
    manifest = RuntimeModelManifest(workerKind="nllb", modelId="synthetic/nllb", immutableRevision="test",
                                    license="mit", assets=assets)
    manager = RuntimeModelCacheManager(tmp_path / "cache", "nllb")

    def copy_asset(asset, target):
        shutil.copyfile(source / asset.relativePath, target)

    assert manager.provision_or_verify(manifest, asset_fetcher=copy_asset).ready
    bundle, reason = resolve_decoder_bundle(tmp_path / "cache", manifest.modelId, license_accepted=False)
    assert reason is None and bundle is not None
    decoder = OfflineNllbDecoder(bundle)
    assert decoder.translate("Hello.", "eng_Latn", "fra_Latn") == "h"
    inputs = decoder._tokenizer("hello", return_tensors="pt")
    target = decoder._tokenizer.convert_tokens_to_ids("fra_Latn")
    with torch.inference_mode():
        generated = decoder._model.generate(**inputs, forced_bos_token_id=target, max_new_tokens=2,
                                            num_beams=1, do_sample=False)
    assert generated.shape[0] == 1
    assert int(generated[0][1]) == target

    from fastapi.testclient import TestClient
    import openconnect_nllb.app as app_module

    monkeypatch.setattr(app_module, "settings", replace(
        app_module.settings, worker_token="test-token", model_dir=tmp_path / "cache",
        fake_engine=False, model_id=manifest.modelId, model_device="cpu", commercial_use=False,
    ))
    monkeypatch.setattr(app_module, "_cached_key", None)
    monkeypatch.setattr(app_module, "_cached_decoder", None)
    import openconnect_nllb.init_models as init_models

    monkeypatch.setattr(init_models.Settings, "from_environment", lambda: app_module.settings)
    assert init_models.main() == 0
    client = TestClient(app_module.app)
    assert client.get("/health/ready").json()["ready"] is True
    response = client.post("/internal/v1/execute", headers={"Authorization": "Bearer test-token"}, json={
        "schemaVersion": "openconnect.v1", "taskId": "synthetic-test",
        "input": {"text": "Hello.", "sourceLang": "eng_Latn", "targetLang": "fra_Latn"},
    })
    assert response.status_code == 200
    assert response.json()["result"]["translatedText"] == "h"
    assert response.json()["result"]["engine"] == "real"
