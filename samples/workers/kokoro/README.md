# Kokoro worker offline contract sample

The current worker is a local CPU Kokoro engine with one supported voice, `af_heart`. The contract tier tests real WAV/PCM codecs, input validation, fixed voice configuration, epoch metadata and literal pipeline input. It does **not** verify model-live synthesis, speech quality, downstream stale-chunk disposal or the runtime cache lifecycle.

## Run the verified offline contracts

From the repository root, in an activated Python 3.12 virtual environment:

```sh
python -m pip install -r samples/workers/shared/requirements-contract.txt
./samples/workers/shared/scripts/verify-no-build-models.sh kokoro
./samples/workers/shared/scripts/run-one.sh kokoro --tier contract --provider sqlite
```

These tests use numeric codec samples and a fail-closed pipeline double, not fabricated successful speech. They guard against model provisioning. The static policy check is not a Docker build or live synthesis test.

## Current runtime configuration

These settings are read by [the worker configuration](../../../workers/kokoro/src/openconnect_kokoro/config.py), not promises of offline execution:

| Setting | Default / constraint |
|---|---|
| `OPENCONNECT_WORKER_TOKEN` | No default token; configure authentication for an authorized live deployment. |
| `KOKORO_VOICES` or `OC_KOKORO_VOICE` | Only `af_heart` is accepted; multi-voice lists fail validation. |
| `OC_MODEL_CACHE_DIR` or `MODEL_DIR` | `/models`; storage location, **not** a no-download switch. |
| `OC_KOKORO_MODEL` | `hexgrad/Kokoro-82M` |
| `OC_ENVIRONMENT` | `Production` |

CPU execution, default voice `af_heart`, and native 24 kHz mono audio are fixed by the implementation. WAV and PCM are supported; MP3 is rejected. Input must contain 1–2000 characters.

`OC_WORKER_ENGINE`, `OC_MODEL_PROVISIONING`, `OC_MODEL_OFFLINE` and `KOKORO_DEFAULT_VOICE` are not supported controls in this implementation. In particular, setting them does not select a fake engine or prevent downloads.

## Live execution is not validated by this sample

The historical [compose file](compose.e2e.yaml), smoke script and cache-matrix script predate the real-engine migration. Their legacy voice/provisioning settings do not match the current implementation; do not use them as validated launch or offline-safety instructions. Readiness or synthesis can provision missing artifacts, so live execution requires separately reviewed configuration, approved model assets and explicit authorization.

No model-live, container startup, or cache-lifecycle success should be inferred from a passing contract job. The contract runner prints its fixture/test results; those logs are the evidence for this tier.

If a separately authorized compose run is active, `docker compose -f samples/workers/kokoro/compose.e2e.yaml down --remove-orphans` preserves its volumes. Cache deletion is not part of the contract procedure.
