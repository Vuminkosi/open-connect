"""Opt-in smoke test for the pinned upstream MiniLM model; no quality claim."""

import json
import os
import socket
from datetime import datetime, timedelta, timezone

import pytest
from fastapi.testclient import TestClient

from openconnect_knowledge.app import app
from openconnect_knowledge.profiles import minilm_configs
from openconnect_worker_common.models.initialize import initialize
from openconnect_worker_common.models.resource_admission import ResourceBudget, ResourceRequest
from openconnect_worker_common.security.signing import sign_bytes

pytestmark = pytest.mark.skipif(
    os.environ.get("OC_RUN_REAL_MODEL_TESTS") != "1",
    reason="real model downloads require explicit opt-in",
)


def test_model_provisions_and_serves_without_network_after_restart(tmp_path, monkeypatch):
    init, service = minilm_configs(
        ResourceRequest(memoryMb=1, cpuMillicores=1),
        ResourceBudget(memoryMbAvailable=1, vramMbAvailable=0, cpuMillicoresAvailable=1),
        allowed_hosts=["huggingface.co", "us.aws.cdn.hf.co"],
        timeout_seconds=120, disk_reserve_bytes=0,
    )
    cache = tmp_path / "cache"
    result = initialize(init, cache, provisioning="first-start-if-missing", offline=False)
    assert result.ready and result.downloads_attempted == 2

    def deny(*args, **kwargs):
        raise AssertionError("network forbidden after initialization")

    monkeypatch.setattr(socket, "getaddrinfo", deny)
    monkeypatch.setattr(socket, "create_connection", deny)
    reused = initialize(init, cache, provisioning="first-start-if-missing", offline=True)
    assert reused.ready and reused.cache_hit and reused.downloads_attempted == 0
    path = tmp_path / "service.json"
    path.write_text(service.model_dump_json(), encoding="utf-8")
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
