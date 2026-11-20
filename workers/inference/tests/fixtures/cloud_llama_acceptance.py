"""Opt-in offline smoke test for an already-provisioned approved GGUF model."""
import json
import os
from pathlib import Path

from openconnect_inference.cloud_execution import _Payload
from openconnect_inference.cloud_llama_runtime import load_verified_runtime

runtime = load_verified_runtime(Path(os.environ["OC_CLOUD_TEST_MODEL_PATH"]), os.environ["OC_CLOUD_TEST_MODEL_ID"],
    os.environ["OC_CLOUD_TEST_MODEL_REVISION"], os.environ["OC_CLOUD_TEST_MODEL_DIGEST"], 512, 2,
    license_accepted=True, artifact_reviewed=True)
payload = _Payload.model_validate({"schemaVersion": "openconnect.cloud.inference.worker/v1",
    "requestId": "11111111-1111-1111-1111-111111111111", "modelId": runtime.model_id,
    "input": {"messages": [{"role": "user", "content": "Say hello."}], "maxTokens": 8, "stream": False}})
prepared = runtime.prepare(payload)
output, count = prepared.generate(8)
assert prepared.input_tokens > 0 and 0 <= count <= 8 and output
print(json.dumps({"inputTokens": prepared.input_tokens, "outputTokens": count, "bounded": True}))
