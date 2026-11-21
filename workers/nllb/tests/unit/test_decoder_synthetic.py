"""Optional offline runtime smoke test; the random model has no translation quality."""

import hashlib
import os
import shutil

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
        bos_token_id=0, pad_token_id=1, eos_token_id=2,
    ))
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
    inputs = decoder._tokenizer("hello", return_tensors="pt")
    target = decoder._tokenizer.convert_tokens_to_ids("fra_Latn")
    with torch.inference_mode():
        generated = decoder._model.generate(**inputs, forced_bos_token_id=target, max_new_tokens=2,
                                            num_beams=1, do_sample=False)
    assert generated.shape[0] == 1
    assert int(generated[0][1]) == target
