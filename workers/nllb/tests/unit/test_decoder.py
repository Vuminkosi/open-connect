from contextlib import nullcontext
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
from types import ModuleType, SimpleNamespace
import time

import pytest

import openconnect_nllb.decoder as decoder_module
from openconnect_nllb.decoder import DecoderInputError, DecoderUnavailable, OfflineNllbDecoder
from openconnect_nllb.decoder_bundle import DecoderBundle
from openconnect_nllb.languages import SUPPORTED_LANGUAGES


@pytest.fixture
def runtime(monkeypatch, tmp_path):
    events = []

    class Tokenizer:
        unk_token_id = 999
        eos_token_id = 2
        src_lang = None

        def convert_tokens_to_ids(self, token):
            return SUPPORTED_LANGUAGES.index(token) + 10 if token in SUPPORTED_LANGUAGES else 999

        def __call__(self, text, **kwargs):
            events.append(("input", self.src_lang, text, kwargs))
            time.sleep(0.01)
            return {"input_ids": SimpleNamespace(shape=(1, 3))}

        def decode(self, tokens, **kwargs):
            events.append(("decode", tokens, kwargs))
            return "bonjour"

    class Model:
        config = SimpleNamespace(model_type="m2m_100")

        def eval(self):
            events.append(("eval",))

        def generate(self, **kwargs):
            events.append(("generate", kwargs))
            return [[0, 11, 2]]

    class Loader:
        def __init__(self, value):
            self.value = value

        def from_pretrained(self, path, **kwargs):
            events.append(("load", path, kwargs))
            return self.value

    torch = ModuleType("torch")
    torch.inference_mode = nullcontext
    transformers = ModuleType("transformers")
    transformers.AutoTokenizer = Loader(Tokenizer())
    transformers.AutoModelForSeq2SeqLM = Loader(Model())
    transformers.NllbTokenizer = Tokenizer
    monkeypatch.setitem(__import__("sys").modules, "torch", torch)
    monkeypatch.setitem(__import__("sys").modules, "transformers", transformers)
    monkeypatch.setattr(decoder_module, "_runtime_versions_valid", lambda: True)
    monkeypatch.setenv("HF_HUB_OFFLINE", "1")
    bundle = DecoderBundle(Path(tmp_path), "model", "revision", "a" * 64, "b" * 64)
    return bundle, events


def test_decoder_loads_local_safetensors_and_sets_language_tokens(runtime):
    bundle, events = runtime
    decoder = OfflineNllbDecoder(bundle)
    assert decoder.translate("hello", "eng_Latn", "fra_Latn") == "bonjour"
    loads = [event for event in events if event[0] == "load"]
    assert len(loads) == 2
    assert all(event[2]["local_files_only"] and not event[2]["trust_remote_code"] for event in loads)
    assert loads[1][2]["use_safetensors"] is True
    assert next(event for event in events if event[0] == "input")[1] == "eng_Latn"
    generation = next(event[1] for event in events if event[0] == "generate")
    assert generation["forced_bos_token_id"] == SUPPORTED_LANGUAGES.index("fra_Latn") + 10
    assert generation["max_new_tokens"] == 256
    assert generation["do_sample"] is False


def test_decoder_rejects_network_enabled_and_invalid_pair(runtime, monkeypatch):
    bundle, _ = runtime
    monkeypatch.delenv("HF_HUB_OFFLINE")
    with pytest.raises(DecoderUnavailable):
        OfflineNllbDecoder(bundle)
    monkeypatch.setenv("HF_HUB_OFFLINE", "1")
    decoder = OfflineNllbDecoder(bundle)
    with pytest.raises(DecoderInputError):
        decoder.translate("hello", "eng_Latn", "unknown")


def test_concurrent_requests_keep_language_state_isolated(runtime):
    bundle, events = runtime
    decoder = OfflineNllbDecoder(bundle)
    with ThreadPoolExecutor(max_workers=2) as pool:
        results = list(pool.map(
            lambda item: decoder.translate(*item),
            [("hello", "eng_Latn", "fra_Latn"), ("jambo", "swh_Latn", "eng_Latn")],
        ))
    assert results == ["bonjour", "bonjour"]
    operations = [event for event in events if event[0] in {"input", "generate", "decode"}]
    assert [event[0] for event in operations] == ["input", "generate", "decode"] * 2
    assert {(event[1], event[2]) for event in operations if event[0] == "input"} == {
        ("eng_Latn", "hello"), ("swh_Latn", "jambo")
    }
