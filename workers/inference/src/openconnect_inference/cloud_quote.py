"""Local token accounting for a Cloud gateway's already-inspected payload."""
from __future__ import annotations

import base64
import hashlib
import hmac
import json
import os
import sys
from pathlib import Path

from openconnect_inference.cloud_execution import InferenceRuntime, _decode, _json, _Payload, _valid_id
from openconnect_inference.cloud_llama_runtime import load_verified_runtime


def quote_payload(payload: bytes, key: bytes, runtime: InferenceRuntime,
                  model_revision: str, model_digest: str) -> dict[str, object] | None:
    """Count using the same model and prompt formatter as execution; never generate."""
    if (not isinstance(payload, bytes) or not 0 < len(payload) <= 131072 or
            not isinstance(key, bytes) or len(key) != 32 or runtime is None):
        return None
    try:
        if runtime.model_revision != model_revision or runtime.model_digest != model_digest:
            return None
        request = _Payload.model_validate(_json(payload))
        if (not _valid_id(request.requestId) or request.modelId != runtime.model_id or request.input.stream or
                not 0 < sum(len(message.content) for message in request.input.messages) <= 65536):
            return None
        prepared = runtime.prepare(request)
        if type(prepared.input_tokens) is not int or not 0 < prepared.input_tokens <= 8192:
            return None
        return {"requestId": request.requestId, "modelId": request.modelId,
                "maxOutputTokens": request.input.maxTokens, "inputTokenUpperBound": prepared.input_tokens,
                "workerPayloadFingerprint": base64.b64encode(hmac.digest(key, payload, hashlib.sha256)).decode("ascii")}
    except Exception:
        # Tokenizer errors may contain message content; no diagnostics cross this boundary.
        return None


def main() -> int:
    """Serve bounded local stdin requests after verifying a provisioned model once."""
    try:
        if os.environ.get("OPENCONNECT_CLOUD_MODEL_FORMAT") != "chatml":
            return 1
        runtime = load_verified_runtime(
            Path(os.environ["OPENCONNECT_CLOUD_MODEL_PATH"]), os.environ["OPENCONNECT_CLOUD_MODEL_ID"],
            os.environ["OPENCONNECT_CLOUD_MODEL_REVISION"], os.environ["OPENCONNECT_CLOUD_MODEL_DIGEST"],
            int(os.environ.get("OPENCONNECT_CLOUD_MODEL_CONTEXT_TOKENS", "4096")),
            int(os.environ.get("OPENCONNECT_CLOUD_MODEL_THREADS", "2")),
            license_accepted=os.environ.get("OPENCONNECT_CLOUD_MODEL_LICENSE_ACCEPTED", "false").lower() == "true",
            artifact_reviewed=os.environ.get("OPENCONNECT_CLOUD_MODEL_ARTIFACT_REVIEWED", "false").lower() == "true")
    except Exception:
        return 1
    try:
        print('{"status":"ready"}', flush=True)
        while True:
            line = sys.stdin.buffer.readline(180001)
            if not line:
                return 0
            if len(line) > 180000 or not line.endswith(b"\n"):
                return 1
            try:
                request = _json(line)
                if not isinstance(request, dict) or set(request) != {"payload", "key", "modelRevision", "modelDigest"}:
                    result = None
                else:
                    result = quote_payload(_decode(request["payload"]), _decode(request["key"]), runtime,
                                           request["modelRevision"], request["modelDigest"])
            except Exception:
                result = None
            print(json.dumps(result, separators=(",", ":")), flush=True)
    finally:
        runtime.close()


if __name__ == "__main__":
    raise SystemExit(main())
