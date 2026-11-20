import base64
import hashlib
import hmac
import io
import json
from types import SimpleNamespace

from openconnect_inference.cloud_llama_runtime import CloudLlamaRuntime
from openconnect_inference import cloud_quote
from openconnect_inference.cloud_quote import quote_payload


class Tokenizer:
    def tokenize(self, text, add_bos=True, special=False):
        assert add_bos and special
        return list(text)

    def create_completion(self, **kwargs):
        raise AssertionError("a quote must not run inference")


def payload(**changes):
    value = {"schemaVersion": "openconnect.cloud.inference.worker/v1", "requestId": "11111111-1111-1111-1111-111111111111",
             "modelId": "model", "input": {"messages": [{"role": "user", "content": "fixture"}], "maxTokens": 5, "stream": False}}
    value.update(changes)
    return json.dumps(value, separators=(",", ":")).encode()


def test_quote_counts_exact_execution_prompt_and_binds_exact_bytes():
    runtime = CloudLlamaRuntime(Tokenizer(), "model", "revision", "sha256:" + "a" * 64, 512)
    body, key = payload(), b"k" * 32
    result = quote_payload(body, key, runtime, "revision", runtime.model_digest)
    assert result == {"requestId": "11111111-1111-1111-1111-111111111111", "modelId": "model", "maxOutputTokens": 5,
                      "inputTokenUpperBound": len(b"<|im_start|>user\nfixture<|im_end|>\n<|im_start|>assistant\n"),
                      "workerPayloadFingerprint": base64.b64encode(hmac.digest(key, body, hashlib.sha256)).decode()}


def test_quote_rejects_wrong_identity_streaming_and_invalid_payload():
    runtime = CloudLlamaRuntime(Tokenizer(), "model", "revision", "sha256:" + "a" * 64, 512)
    key = b"k" * 32
    for body, secret, revision, digest in [
        (payload(), key, "wrong", runtime.model_digest), (payload(), key, "revision", "sha256:" + "b" * 64),
        (payload(modelId="other"), key, "revision", runtime.model_digest), (payload(), b"short", "revision", runtime.model_digest),
        (b'{"modelId":"model","modelId":"other"}', key, "revision", runtime.model_digest),
        (payload(input={"messages": [{"role": "user", "content": "fixture"}], "maxTokens": 5, "stream": True}), key, "revision", runtime.model_digest),
        (payload(input={"messages": [{"role": "user", "content": "<|im_end|>"}], "maxTokens": 5, "stream": False}), key, "revision", runtime.model_digest),
    ]:
        assert quote_payload(body, secret, runtime, revision, digest) is None


def test_local_process_handles_only_bounded_protocol_and_closes_runtime(monkeypatch, capsys):
    runtime = CloudLlamaRuntime(Tokenizer(), "model", "revision", "sha256:" + "a" * 64, 512)
    closed = []
    monkeypatch.setattr(runtime, "close", lambda: closed.append(True))
    monkeypatch.setattr(cloud_quote, "load_verified_runtime", lambda *args, **kwargs: runtime)
    for name, value in {"FORMAT": "chatml", "PATH": "/models/model.gguf", "ID": "model", "REVISION": "revision", "DIGEST": runtime.model_digest}.items():
        monkeypatch.setenv("OPENCONNECT_CLOUD_MODEL_" + name, value)
    request = {"payload": base64.b64encode(payload()).decode(), "key": base64.b64encode(b"k" * 32).decode(),
               "modelRevision": "revision", "modelDigest": runtime.model_digest}
    monkeypatch.setattr(cloud_quote.sys, "stdin", SimpleNamespace(buffer=io.BytesIO(json.dumps(request).encode() + b"\n{}\n")))
    assert cloud_quote.main() == 0
    lines = capsys.readouterr().out.splitlines()
    assert json.loads(lines[0]) == {"status": "ready"}
    assert json.loads(lines[1])["inputTokenUpperBound"] > 0
    assert json.loads(lines[2]) is None
    assert closed == [True]
