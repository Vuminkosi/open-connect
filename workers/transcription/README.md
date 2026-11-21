# Transcription worker

The worker supports final transcription with an explicitly provisioned local CTranslate2 Whisper model. It does not download models. Obtain a compatible model through an approved process and supply its license and integrity metadata before enabling local execution. The synthetic engine is for explicit non-production testing only.

Prepare a JSON manifest with `workerKind` set to `transcription`, an exact `modelId` and `immutableRevision`, an approved `license`, and an `assets` array. Each asset specifies its `relativePath`, SHA-256 in `sha256`, and byte length in `sizeBytes`. The manifest must cover `model.bin`, `config.json`, `tokenizer.json`, and any other files in the model directory, including `preprocessor_config.json` when supplied. Without that optional file, the runtime uses its default preprocessing settings. The local importer copies and verifies each asset; it does not contact a model registry.

```sh
python -m openconnect_transcription.provision_local \
  --source /path/to/model --manifest /path/to/manifest.json --cache /models
```

Configure `TRANSCRIPTION_BACKEND=local-model`, `OC_WORKER_ENGINE=real`, `OC_TRANSCRIPTION_MODEL` to the manifest model ID, `TRANSCRIPTION_MODEL_REVISION` to its revision, and `OC_MODEL_CACHE_DIR` to the same cache directory. Set `OC_TRANSCRIPTION_FILE_URL` to an empty value to disable the optional cloud leg. The worker remains unready if the runtime cannot load the verified model. CPU is selected with `OC_MODEL_DEVICE=cpu`; GPU execution additionally requires a compatible runtime and device.

Automated adapter tests use a simulated model. They do not establish the accuracy, licensing, or performance of an operator-supplied model.
