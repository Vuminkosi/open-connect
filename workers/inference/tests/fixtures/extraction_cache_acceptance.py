"""Opt-in offline test of real extraction against a disposable verified cache."""

import asyncio
import json
import os
import shutil
from datetime import datetime, timedelta, timezone
from pathlib import Path
from tempfile import TemporaryDirectory

from openconnect_inference.extraction_profiles import FILENAME, qwen_configs
from openconnect_worker_common.models.resource_admission import ResourceBudget, ResourceRequest
from openconnect_worker_common.models.runtime_cache import RuntimeModelCacheManager
from openconnect_worker_common.security.signing import sign_bytes

if os.environ.get("OC_RUN_EXTRACTION_CACHE_FIXTURE") != "1":
    raise RuntimeError("fixture_disabled")

source = Path(os.environ["OC_EXTRACTION_FIXTURE_MODEL"])
request = ResourceRequest(memoryMb=2048, cpuMillicores=2000)
budget = ResourceBudget(memoryMbAvailable=2048, cpuMillicoresAvailable=2000, vramMbAvailable=0)
init, serve = qwen_configs(request, budget, artifact_reviewed=True, artifact_license="fixture-license", allowed_hosts=["huggingface.co"],
                           timeout_seconds=60, disk_reserve_bytes=0, threads=2)
with TemporaryDirectory(prefix="openconnect-extraction-") as directory:
    root = Path(directory)
    cache = RuntimeModelCacheManager(root / "cache", "inference")
    def copy_local(asset, destination):
        assert asset.relativePath == FILENAME
        shutil.copyfile(source, destination)
    provisioned = cache.provision_or_verify(init.manifest, provisioning="first-start-if-missing", offline=False,
        license_accepted=True, asset_fetcher=copy_local)
    assert provisioned.ready and provisioned.revision_dir is not None
    config = root / "serve.json"
    config.write_text(serve.model_dump_json(), encoding="utf-8")
    os.environ.update({"OC_EXTRACTION_ENABLED": "true", "OC_WORKER_ENGINE": "real", "OC_MODEL_DEVICE": "cpu",
                       "OC_EXTRACTION_CONFIG_PATH": str(config), "OC_MODEL_CACHE_DIR": str(root / "cache")})
    schema = {"type": "object", "additionalProperties": False, "required": ["total"],
              "properties": {"total": {"type": "number"}}}
    os.environ.update({"OC_ENVIRONMENT": "Production", "OPENCONNECT_ENV": "production", "OC_INFERENCE_MODEL": "fixture-local",
                       "OC_INFERENCE_API_BASE_URL": "", "OPENCONNECT_INFERENCE_TOKEN": "fixture-token",
                       "OPENCONNECT_WORKER_SIGNING_KEY": "ab" * 32})
    from openconnect_inference.app import app

    now = datetime.now(timezone.utc)
    command = {"schemaVersion": "openconnect.v1", "taskId": "fixture", "kind": "documents.extract",
               "tenantId": "01ARZ3NDEKTSV4RRFFQ69G5FAV", "issuedAt": (now - timedelta(seconds=1)).isoformat(),
               "expiresAt": (now + timedelta(seconds=30)).isoformat(),
               "payload": {"document": "Invoice total: 42", "extractionSchema": schema}}
    body = json.dumps(command).encode()
    scope = {"type": "http", "asgi": {"version": "3.0"}, "http_version": "1.1", "method": "POST",
             "scheme": "http", "path": "/internal/v1/extract", "raw_path": b"/internal/v1/extract",
             "query_string": b"", "headers": [(b"authorization", b"Bearer fixture-token"),
                 (b"x-openconnect-signature", sign_bytes(body, bytes.fromhex("ab" * 32)).encode()),
                 (b"content-type", b"application/json")], "server": ("test", 80), "client": ("test", 1)}
    sent = []
    async def receive():
        return {"type": "http.request", "body": body, "more_body": False}
    async def send(message):
        sent.append(message)
    async def exercise():
        async with app.router.lifespan_context(app):
            runtime = app.state.extraction_runtime
            assert runtime is not None and runtime.verify()
            capabilities_scope = dict(scope, method="GET", path="/internal/v1/capabilities",
                                      raw_path=b"/internal/v1/capabilities")
            await app(capabilities_scope, receive, send)
            advertised = json.loads(b"".join(item.get("body", b"") for item in sent if item["type"] == "http.response.body"))
            assert "documents.extract" in advertised["capabilities"]
            sent.clear()
            await app(scope, receive, send)
            assert sent[0]["status"] == 200
            response = json.loads(b"".join(item.get("body", b"") for item in sent if item["type"] == "http.response.body"))
            assert response["result"]["fields"]["total"]["value"] == 42
            with (provisioned.revision_dir / "assets" / FILENAME).open("r+b") as cached:
                cached.seek(0)
                cached.write(b"X")
            assert not runtime.verify()
            sent.clear()
            await app(scope, receive, send)
            assert sent[0]["status"] == 503
            sent.clear()
            await app(capabilities_scope, receive, send)
            advertised = json.loads(b"".join(item.get("body", b"") for item in sent if item["type"] == "http.response.body"))
            assert "documents.extract" not in advertised["capabilities"]
        assert app.state.extraction_runtime is None
    asyncio.run(exercise())
    print("verified cache, signed in-process route, real CPU extraction and corruption detection passed")
