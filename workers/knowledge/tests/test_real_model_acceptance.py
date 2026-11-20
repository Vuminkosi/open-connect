"""Opt-in smoke test for the pinned upstream MiniLM model; no quality claim."""

import json
import os
import socket
from datetime import datetime, timedelta, timezone

import pytest
from fastapi.testclient import TestClient

from openconnect_knowledge.app import app
from openconnect_worker_common.models.initialize import InitializerConfig, initialize
from openconnect_worker_common.security.signing import sign_bytes

REVISION = "1110a243fdf4706b3f48f1d95db1a4f5529b4d41"
ROOT = f"https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/{REVISION}/"
pytestmark = pytest.mark.skipif(
    os.environ.get("OC_RUN_REAL_MODEL_TESTS") != "1",
    reason="real model downloads require explicit opt-in",
)


def test_model_provisions_and_serves_without_network_after_restart(tmp_path, monkeypatch):
    manifest = {
        "workerKind": "knowledge", "modelId": "sentence-transformers/all-MiniLM-L6-v2",
        "immutableRevision": REVISION, "license": "apache-2.0", "assets": [
            {"relativePath": "model.onnx", "sizeBytes": 90405214,
             "sha256": "6fd5d72fe4589f189f8ebc006442dbb529bb7ce38f8082112682524616046452"},
            {"relativePath": "tokenizer.json", "sizeBytes": 466247, "kind": "tokenizer",
             "sha256": "be50c3628f2bf5bb5e3a7f17b1f74611b2561a3a27eeab05e5aa30f411572037"},
        ],
    }
    budget = {"memoryMbAvailable": 1, "vramMbAvailable": 0, "cpuMillicoresAvailable": 1}
    request = {"memoryMb": 1, "cpuMillicores": 1}
    init = InitializerConfig.model_validate({
        "manifest": manifest,
        "sources": {
            "model.onnx": {"url": ROOT + "onnx/model.onnx", "mediaTypes": ["application/octet-stream"]},
            "tokenizer.json": {"url": ROOT + "tokenizer.json",
                               "mediaTypes": ["text/plain", "application/json", "application/octet-stream"]},
        },
        "allowedHosts": ["huggingface.co", "us.aws.cdn.hf.co"],
        "resourceRequest": request, "resourceBudget": budget,
        "maxDownloadBytes": 100000000, "downloadTimeoutSeconds": 120,
    })
    cache = tmp_path / "cache"
    result = initialize(init, cache, provisioning="first-start-if-missing", offline=False)
    assert result.ready and result.downloads_attempted == 2

    def deny(*args, **kwargs):
        raise AssertionError("network forbidden after initialization")

    monkeypatch.setattr(socket, "getaddrinfo", deny)
    monkeypatch.setattr(socket, "create_connection", deny)
    reused = initialize(init, cache, provisioning="first-start-if-missing", offline=True)
    assert reused.ready and reused.cache_hit and reused.downloads_attempted == 0
    config = {"manifest": manifest, "profile": {
        "modelAsset": "model.onnx", "tokenizerAsset": "tokenizer.json", "dimension": 384,
        "maxTokens": 256}, "resourceRequest": request, "resourceBudget": budget}
    path = tmp_path / "service.json"
    path.write_text(json.dumps(config), encoding="utf-8")
    for key, value in {
        "OC_KNOWLEDGE_ENABLED": "true", "OC_KNOWLEDGE_CONFIG_PATH": str(path),
        "OC_MODEL_CACHE_DIR": str(cache), "OC_MODEL_DEVICE": "cpu", "OC_WORKER_ENGINE": "real",
        "OC_MODEL_PROVISIONING": "first-start-if-missing",
        "OPENCONNECT_WORKER_TOKEN": "temporary-token", "OPENCONNECT_WORKER_SIGNING_KEY": "ab" * 32,
    }.items():
        monkeypatch.setenv(key, value)
    now = datetime.now(timezone.utc)
    command = {"schemaVersion": "openconnect.v1", "taskId": "smoke", "kind": "knowledge.embed",
               "tenantId": "smoke-tenant", "issuedAt": (now - timedelta(seconds=1)).isoformat(),
               "expiresAt": (now + timedelta(minutes=1)).isoformat(), "payload": {
                   "inputKind": "query", "texts": ["A dog runs in a park", "A puppy plays outside",
                                                      "The stock market crashed"]}}
    body = json.dumps(command).encode()
    headers = {"Authorization": "Bearer temporary-token",
               "X-OpenConnect-Signature": sign_bytes(body, bytes.fromhex("ab" * 32))}
    results = []
    for _ in range(2):
        with TestClient(app) as client:
            assert client.get("/health/ready").status_code == 200
            response = client.post("/internal/v1/execute", content=body, headers=headers)
            assert response.status_code == 200
            results.append(response.json()["result"])
    assert results[0]["space_id"] == results[1]["space_id"]
    assert results[0]["items"] == results[1]["items"]
    a, b, c = [item["vector"] for item in results[0]["items"]]
    assert len(a) == 384
    assert sum(x*y for x,y in zip(a,b)) > sum(x*y for x,y in zip(a,c))
