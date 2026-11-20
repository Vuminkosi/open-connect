# Classification worker: model setup

The real adapter loads an operator-provided ONNX bundle from `MODEL_DIR` (default `/models`). It does not download models; `HF_HUB_OFFLINE=1` is required. Mount the bundle read-only and set `OC_CLASSIFICATION_BUNDLE_SHA256` to the lowercase SHA-256 of the exact `classification.onnx.json` bytes. The directory contains:

- `classification.onnx.json`: the pinned bundle contract shown below.
- `model.onnx`: the ONNX graph, at most 512 MiB.
- `tokenizer.json`: a Hugging Face tokenizers JSON file, at most 8 MiB.

```json
{
  "schemaVersion": "openconnect.classification.onnx/v1",
  "modelId": "operator-selected-model",
  "revision": "operator-pinned-revision",
  "license": "apache-2.0",
  "labelSetVersion": "v1",
  "labels": ["safe", "spam", "toxic", "sensitive"],
  "activation": "sigmoid",
  "maxTokens": 512,
  "outputName": "logits",
  "modelSha256": "<lowercase SHA-256 of model.onnx>",
  "tokenizerSha256": "<lowercase SHA-256 of tokenizer.json>"
}
```

Labels are in model output order and must contain the complete named version from `label_sets.py`. Select `sigmoid` for independent logits or `softmax` for mutually exclusive logits as appropriate to the model. The graph must accept rank-two int64 `input_ids`, optionally `attention_mask` and `token_type_ids`, and return a finite floating-point tensor of shape `[1, number of labels]` at `outputName`. Only CPU execution is supported. Inputs exceeding `maxTokens` are rejected, never silently truncated. The worker checks the pinned hashes, model interface and a startup inference probe before advertising the configured label set. Other licenses require `LICENSE_ACCEPTED=1`.

The pin verifies bundle integrity, not its provenance, safety, taxonomy semantics or predictive quality. Operators must select and evaluate an appropriately licensed model and validate its tokenizer, output ordering, activation and suitability for their data. No production model is supplied or accepted by the repository. The legacy `manifest.json` checkpoint format alone cannot enable real inference. CI may use `OPENCONNECT_FAKE_ENGINE=1`; its keyword results are explicitly tagged fake and are not production inference.

For Cloud classification, register `ModelDigest` and set `CLOUD_CLASSIFICATION_MODEL_DIGEST` to `sha256:` followed by the pinned bundle-contract digest. The gateway verifies the digest reported by the worker and the deployment registry before dispatch and when creating a claim. This digest binds the contract, model and tokenizer hashes; it does not attest an untrusted worker or prove model quality.

For the standalone classification Compose stack, populate the `oc_classification_models` volume and provide `OC_CLASSIFICATION_BUNDLE_SHA256`. The initializer checks the named files and contract pin; the worker performs full asset, license and runtime validation. To enable Cloud claim consumption, configure `OPENCONNECT_CLOUD_CONSUMPTION_ENABLED=true`, `OPENCONNECT_CLOUD_AUTHORITY_ORIGIN`, and the three absolute `OPENCONNECT_CLOUD_AUTHORITY_CA_FILE`, `OPENCONNECT_CLOUD_WORKER_CERT_FILE` and `OPENCONNECT_CLOUD_WORKER_KEY_FILE` paths inside `/run/openconnect-cloud-worker`. Set `CLOUD_CLASSIFICATION_WORKER_SECRETS_DIR` to the deployment-managed directory containing these files. Grant the container user read access and keep the worker certificate distinct from gateway credentials. Cloud dispatch still requires a trusted private mutual-TLS endpoint in front of the worker; Compose does not provide that termination or provision certificates.
