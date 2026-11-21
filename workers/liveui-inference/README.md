# OpenConnect LiveUI Inference Worker

Optional, server-side CPU inference for LiveUI action plans. The worker exposes a versioned HTTP API and does not connect to browsers, application databases, or model-provided URLs. Deploy it on the Gateway's private container network with no published host port. The Gateway authenticates requests with `OPENCONNECT_WORKER_TOKEN`; keep that secret in the deployment secret store and pass it only to the server-side worker client.

## Model configuration

The initial default is the Qwen3-0.6B Q4_K_M GGUF artifact from `bartowski/Qwen_Qwen3-0.6B-GGUF`, pinned to repository revision `60b85c0e3d8fe0f6474f406922a26d12aca4550d` and SHA-256 `9acfc1e001311f34b4252001b626f2e466d592a42065f66571bff3790d4e1b14`. The source Qwen3-0.6B model is Apache-2.0 licensed. The quantized file is a separately converted artifact; review its upstream model card and applicable license before changing deployment policy. [Source model card](https://huggingface.co/Qwen/Qwen3-0.6B), [pinned GGUF artifact](https://huggingface.co/bartowski/Qwen_Qwen3-0.6B-GGUF/tree/60b85c0e3d8fe0f6474f406922a26d12aca4550d).

Set `MODEL_ID`, `MODEL_REVISION`, `MODEL_FILENAME`, and `MODEL_SHA256` to replace the model without modifying OpenConnect packages or the LiveUI plan schema. Revision and digest are mandatory immutable pins when inference is enabled. Provision the artifact at `MODEL_PATH` before starting the worker. Automatic downloads are disabled by default; an operator may explicitly set `LIVEUI_ALLOW_MODEL_DOWNLOAD=true` for non-Cloud provisioning. The worker verifies SHA-256 before loading the file. Liveness remains available during initialization; readiness is reported only after the model is loaded. No model weights are included in the image. To replace a model without deleting or overwriting persisted data, configure a new model-specific `MODEL_PATH`, then retire older files through a reviewed volume maintenance process.

## Runtime configuration

`MODEL_CONTEXT_SIZE`, `MODEL_THREADS`, `MAX_CONCURRENT_INFERENCES`, `QUEUE_CAPACITY`, and `REQUEST_TIMEOUT_SECONDS` bound memory and CPU use. The worker loads one model instance per process. Benchmark CPU threads, context and queue settings on the deployment host before increasing concurrency; generation runs in a bounded thread pool. A timed-out native generation may continue until llama.cpp returns, so shutdown can wait for an in-flight native call.

The API is `POST /v1/liveui/inference/plan`; readiness is `/health/ready` and liveness is `/health/live`. Requests include only the current intent, route, allowed target/action pairs, schema version and output limits. Exact status, search, and unambiguous highlight commands use bounded deterministic routing; the response provider is `deterministic`. Other requests use the configured model and report its configured provider ID. The worker uses an explicit ChatML format and does not evaluate the model file's embedded Jinja template. It constrains output with llama.cpp's JSON schema response mode and rejects plans with unknown capabilities. OpenConnect validates the result again and authorizes each action before execution. Worker errors use HTTP 401, 422, 429, 503 and 504 for authentication, invalid plans, queue saturation, unavailable inference and timeout respectively.

The model can return an available but unrelated action when no capability matches the request. Schema and capability validation cannot establish that an action expresses the user's intent. Applications must treat plans as proposals and apply their own authorization and confirmation requirements before consequential actions; an empty model plan is rejected as an invalid plan.

## Compose

The root `docker-compose-all.yml` includes the `liveui-inference` worker and persistent `oc_liveui_models` volume. Set `LIVEUI_WORKER_TOKEN` and provision the model before starting:

```powershell
docker compose -f docker-compose-all.yml up -d --build liveui-inference
```

The service has no published host port. The Todo Blazor Server host uses `http://liveui-inference:8080` and enables its worker provider when `LIVEUI_WORKER_TOKEN` is set; the credential is passed only to server-side containers and is not included in browser assets. Leave it unset to keep worker inference disabled. Do not remove the model volume during ordinary deployments.

## Tests

Install `requirements-dev.txt`, then run `pytest -q`. Tests use a disposable temporary directory and do not download model weights.

The .NET worker-client acceptance test is opt-in. Start a ready worker, set `OPENCONNECT_LIVEUI_WORKER_ENDPOINT` and `OPENCONNECT_LIVEUI_WORKER_TOKEN` in the test process, then run:

```powershell
dotnet test tests/Unit/OpenConnect.LiveUI.Blazor.Tests/OpenConnect.LiveUI.Blazor.Tests.csproj -c Release --filter FullyQualifiedName~LiveUIWorkerRuntimeAcceptanceTests
```

This test sends a non-deterministic-path planning request through the production .NET worker provider and validates the returned plan. It requires a configured, authenticated worker and model; without those environment variables, the test is reported as skipped.
