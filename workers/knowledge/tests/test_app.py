import asyncio
import json
import threading
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timedelta, timezone
from pathlib import Path

import pytest
from fastapi.testclient import TestClient
from jsonschema import Draft202012Validator, FormatChecker

from openconnect_knowledge.app import app
from openconnect_knowledge.app import ExecuteRequest, RerankRequest, SearchRequest, _Busy, _before_expiry, _execute_model, _slot
from openconnect_knowledge import app as app_module
from openconnect_worker_common.security.signing import sign_bytes

KEY = bytes.fromhex("ab" * 32)


def test_shared_embedding_request_fixtures_match_request_model():
    root = Path(__file__).resolve().parents[3] / "contracts" / "fixtures"
    ExecuteRequest.model_validate_json((root / "valid" / "knowledge-embed-request.json").read_bytes())
    with pytest.raises(ValueError):
        ExecuteRequest.model_validate_json((root / "invalid" / "knowledge-embed-request.json").read_bytes())


def test_shared_search_request_fixtures_match_request_model():
    root = Path(__file__).resolve().parents[3] / "contracts" / "fixtures"
    SearchRequest.model_validate_json((root / "valid" / "knowledge-search-request.json").read_bytes())
    with pytest.raises(ValueError):
        SearchRequest.model_validate_json((root / "invalid" / "knowledge-search-request.json").read_bytes())


def test_shared_rerank_request_fixtures_match_request_model():
    root = Path(__file__).resolve().parents[3] / "contracts" / "fixtures"
    RerankRequest.model_validate_json((root / "valid" / "knowledge-rerank-request.json").read_bytes())
    with pytest.raises(ValueError):
        RerankRequest.model_validate_json((root / "invalid" / "knowledge-rerank-request.json").read_bytes())


@pytest.fixture
def service(cached_model, tmp_path, monkeypatch):
    cache, manifest, revision = cached_model
    config = {"manifest": manifest.model_dump(), "resourceRequest": {
        "memoryMb": 1, "cpuMillicores": 1,
    }, "resourceBudget": {"memoryMbAvailable": 2, "vramMbAvailable": 0,
                          "cpuMillicoresAvailable": 2}, "profile": {
        "modelAsset": "model.onnx", "tokenizerAsset": "tokenizer.json",
        "dimension": 3, "maxTokens": 8,
    }}
    config_path = tmp_path / "service.json"
    config_path.write_text(json.dumps(config))
    monkeypatch.setenv("OC_KNOWLEDGE_ENABLED", "true")
    monkeypatch.setenv("OC_MODEL_DEVICE", "cpu")
    monkeypatch.setenv("OC_KNOWLEDGE_CONFIG_PATH", str(config_path))
    monkeypatch.setenv("OC_MODEL_CACHE_DIR", str(cache.cache_root))
    monkeypatch.setenv("OPENCONNECT_WORKER_TOKEN", "test-token")
    monkeypatch.setenv("OPENCONNECT_WORKER_SIGNING_KEY", KEY.hex())
    with TestClient(app) as client:
        yield client, revision


def payload():
    now = datetime.now(timezone.utc)
    return {"schemaVersion": "openconnect.v1", "taskId": "task-1", "kind": "knowledge.embed",
            "tenantId": "tenant-1", "issuedAt": (now - timedelta(seconds=1)).isoformat(),
            "expiresAt": (now + timedelta(minutes=1)).isoformat(),
            "payload": {"inputKind": "query", "texts": ["tree", "forest"]}}


def signed(client, data):
    body = json.dumps(data).encode()
    return client.post("/internal/v1/execute", content=body, headers={
        "Authorization": "Bearer test-token", "X-OpenConnect-Signature": sign_bytes(body, KEY),
    })


def test_authenticated_real_cpu_execution_and_cache_failure(service):
    client, revision = service
    assert client.get("/health/ready").status_code == 200
    assert client.get("/internal/v1/capabilities").status_code == 401
    response = client.get("/internal/v1/capabilities", headers={"Authorization": "Bearer test-token"})
    assert response.json()["capabilities"][0]["id"] == "knowledge.embed"
    response = signed(client, payload())
    assert response.status_code == 200
    assert len(response.json()["result"]["items"]) == 2
    schema_dir = Path(__file__).resolve().parents[3] / "contracts" / "jsonschema"
    for document, name in [(payload(), "knowledge-embed-request.json"),
                           (response.json(), "knowledge-embed-response.json")]:
        schema = json.loads((schema_dir / name).read_text(encoding="utf-8"))
        Draft202012Validator.check_schema(schema)
        Draft202012Validator(schema, format_checker=FormatChecker()).validate(document)
    result = response.json()["result"]
    assert all(len(item["vector"]) == result["dimension"] for item in result["items"])
    (revision / "assets" / "model.onnx").write_bytes(b"corrupt")
    assert client.get("/health/ready").status_code == 503
    assert signed(client, payload()).status_code == 503


def test_invalid_signature_and_expired_request_fail_closed(service):
    client, _ = service
    body = json.dumps(payload()).encode()
    assert client.post("/internal/v1/execute", content=body, headers={
        "Authorization": "Bearer test-token", "X-OpenConnect-Signature": "0" * 64,
    }).status_code == 401
    request = payload()
    request["expiresAt"] = (datetime.now(timezone.utc) - timedelta(seconds=1)).isoformat()
    assert signed(client, request).status_code == 422


@pytest.mark.parametrize("field", ["tenantId", "inputKind"])
def test_signed_request_with_duplicate_json_properties_is_rejected(service, field):
    client, _ = service
    raw = json.dumps(payload()).replace(f'"{field}":', f'"{field}": "conflict", "{field}":', 1).encode()
    response = client.post("/internal/v1/execute", content=raw, headers={
        "Authorization": "Bearer test-token", "X-OpenConnect-Signature": sign_bytes(raw, KEY),
    })
    assert response.status_code == 422


@pytest.mark.parametrize("change", ["unknown", "empty_text", "unknown_kind"])
def test_contract_rejects_invalid_request_in_schema_and_api(service, change):
    client, _ = service
    request = payload()
    if change == "unknown":
        request["payload"]["unknown"] = "private-sentinel"
    elif change == "empty_text":
        request["payload"]["texts"] = [""]
    else:
        request["kind"] = "knowledge.rerank"
    schema_path = (Path(__file__).resolve().parents[3] / "contracts" / "jsonschema"
                   / "knowledge-embed-request.json")
    schema = json.loads(schema_path.read_text(encoding="utf-8"))
    assert list(Draft202012Validator(schema).iter_errors(request))
    response = signed(client, request)
    assert response.status_code == 422
    assert "private-sentinel" not in response.text


def test_disabled_does_not_load_or_advertise_model(monkeypatch, tmp_path):
    monkeypatch.setenv("OC_KNOWLEDGE_ENABLED", "false")
    monkeypatch.setenv("OC_MODEL_CACHE_DIR", str(tmp_path / "absent"))
    monkeypatch.setenv("OPENCONNECT_WORKER_TOKEN", "test-token")
    monkeypatch.setenv("OPENCONNECT_WORKER_SIGNING_KEY", KEY.hex())
    with TestClient(app) as client:
        assert client.get("/health/ready").status_code == 503
        assert client.get("/internal/v1/capabilities", headers={
            "Authorization": "Bearer test-token"}).json() == {"capabilities": []}
    assert not (tmp_path / "absent").exists()


def test_disabled_provisioning_does_not_load_existing_cache(cached_model, monkeypatch):
    cache, _, _ = cached_model
    monkeypatch.setenv("OC_KNOWLEDGE_ENABLED", "true")
    monkeypatch.setenv("OC_MODEL_PROVISIONING", "disabled")
    monkeypatch.setenv("OC_MODEL_CACHE_DIR", str(cache.cache_root))
    monkeypatch.setenv("OPENCONNECT_WORKER_TOKEN", "test-token")
    monkeypatch.setenv("OPENCONNECT_WORKER_SIGNING_KEY", KEY.hex())
    with TestClient(app) as client:
        assert client.get("/health/ready").status_code == 503
        assert client.get("/internal/v1/capabilities", headers={
            "Authorization": "Bearer test-token"}).json() == {"capabilities": []}


@pytest.mark.parametrize("reason", ["insufficient_memory", "gpu_required", "cuda_requested"])
def test_resource_denial_prevents_loading(cached_model, tmp_path, monkeypatch, reason):
    cache, manifest, _ = cached_model
    request = {"memoryMb": 10, "cpuMillicores": 1, "gpuRequired": reason == "gpu_required"}
    config = {"manifest": manifest.model_dump(), "profile": {
        "modelAsset": "model.onnx", "tokenizerAsset": "tokenizer.json", "dimension": 3,
        "maxTokens": 8}, "resourceRequest": request, "resourceBudget": {
            "memoryMbAvailable": 1 if reason == "insufficient_memory" else 20,
            "vramMbAvailable": 0, "cpuMillicoresAvailable": 2, "gpuAvailable": True}}
    path = tmp_path / "service.json"
    path.write_text(json.dumps(config))
    monkeypatch.setenv("OC_KNOWLEDGE_ENABLED", "true")
    monkeypatch.setenv("OC_KNOWLEDGE_CONFIG_PATH", str(path))
    monkeypatch.setenv("OC_MODEL_CACHE_DIR", str(cache.cache_root))
    monkeypatch.setenv("OC_MODEL_DEVICE", "cuda" if reason == "cuda_requested" else "cpu")
    monkeypatch.setenv("OPENCONNECT_WORKER_TOKEN", "test-token")
    monkeypatch.setenv("OPENCONNECT_WORKER_SIGNING_KEY", KEY.hex())
    with TestClient(app) as client:
        assert client.get("/health/ready").status_code == 503
        assert client.get("/internal/v1/capabilities", headers={
            "Authorization": "Bearer test-token"}).json() == {"capabilities": []}


def test_rerank_ready_independently_and_requires_signed_bounded_request(engine, tmp_path, monkeypatch):
    model, revision = engine
    config = {"manifest": model._manifest.model_dump(), "resourceRequest": {"memoryMb": 1, "cpuMillicores": 1},
              "resourceBudget": {"memoryMbAvailable": 2, "vramMbAvailable": 0, "cpuMillicoresAvailable": 2},
              "profile": {"modelAsset": "model.onnx", "tokenizerAsset": "tokenizer.json", "maxTokens": 8}}
    path = tmp_path / "rerank.json"
    path.write_text(json.dumps(config))
    monkeypatch.setenv("OC_KNOWLEDGE_ENABLED", "false")
    monkeypatch.setenv("OC_RERANK_ENABLED", "true")
    monkeypatch.setenv("OC_RERANK_CONFIG_PATH", str(path))
    monkeypatch.setenv("OC_MODEL_CACHE_DIR", str(model._cache.cache_root))
    monkeypatch.setenv("OC_MODEL_DEVICE", "cpu")
    monkeypatch.setenv("OPENCONNECT_WORKER_TOKEN", "test-token")
    monkeypatch.setenv("OPENCONNECT_WORKER_SIGNING_KEY", KEY.hex())
    request = payload()
    request["kind"] = "knowledge.rerank"
    request["payload"] = {"query": "tree", "candidates": [{"id": "a", "text": "wood"},
                                                              {"id": "b", "text": "forest"}]}
    with TestClient(app) as client:
        assert client.get("/health/ready").status_code == 200
        capabilities = client.get("/internal/v1/capabilities", headers={"Authorization": "Bearer test-token"})
        assert [item["id"] for item in capabilities.json()["capabilities"]] == ["knowledge.rerank"]
        assert signed(client, payload()).status_code == 503
        response = signed(client, request)
        assert response.status_code == 200
        schema_dir = Path(__file__).resolve().parents[3] / "contracts" / "jsonschema"
        for value, name in [(request, "knowledge-rerank-request.json"),
                            (response.json(), "knowledge-rerank-response.json")]:
            schema = json.loads((schema_dir / name).read_text(encoding="utf-8"))
            Draft202012Validator.check_schema(schema)
            Draft202012Validator(schema, format_checker=FormatChecker()).validate(value)
        assert [item["id"] for item in response.json()["result"]["candidates"]] == ["b", "a"]
        assert response.json()["result"]["score_type"] == "raw_logit"
        request["payload"]["candidates"].append({"id": "a", "text": "tree"})
        assert signed(client, request).status_code == 422
        (revision.revision_dir / "assets" / "model.onnx").write_bytes(b"corrupt")
        assert client.get("/health/ready").status_code == 503


def test_inference_slot_remains_owned_until_native_call_returns():
    entered, release = threading.Event(), threading.Event()

    class SlowEngine:
        def rank(self, query, candidates):
            entered.set()
            assert release.wait(timeout=5)
            return "done"

    class Command:
        kind = "knowledge.rerank"
        expiresAt = datetime.now(timezone.utc) + timedelta(minutes=1)
        payload = type("Payload", (), {"query": "test", "candidates": []})()

    with ThreadPoolExecutor(max_workers=1) as executor:
        running = executor.submit(_execute_model, SlowEngine(), Command())
        try:
            assert entered.wait(timeout=5)
            with pytest.raises(_Busy):
                _execute_model(SlowEngine(), Command())
        finally:
            release.set()
        assert running.result(timeout=5) == "done"


def test_expired_queued_inference_never_calls_model():
    class Engine:
        def embed(self, *args, **kwargs):
            pytest.fail("expired request reached the model")

    class Command:
        kind = "knowledge.embed"
        expiresAt = datetime.now(timezone.utc) - timedelta(seconds=1)

    with pytest.raises(ValueError, match="expired_request"):
        _execute_model(Engine(), Command())


def test_expired_queued_database_operation_does_not_execute():
    calls = []

    def operation(*args, **kwargs):
        calls.append((args, kwargs))
        return "ok"

    with pytest.raises(ValueError, match="expired_request"):
        _before_expiry(datetime.now(timezone.utc) - timedelta(seconds=1), operation, "value", flag=True)
    assert calls == []
    assert _before_expiry(datetime.now(timezone.utc) + timedelta(seconds=5), operation, "value", flag=True) == "ok"
    assert calls == [(("value",), {"flag": True})]


def test_signed_delete_expiring_in_queue_does_not_mutate_index(service, monkeypatch):
    client, _ = service
    calls = []

    class Index:
        def delete(self, *args, **kwargs):
            calls.append("delete")
            return 1

    original = app_module.run_in_threadpool

    async def delayed(function, *args, **kwargs):
        if function is _before_expiry:
            await asyncio.sleep(max(0, (args[0] - datetime.now(timezone.utc)).total_seconds()) + .02)
        return await original(function, *args, **kwargs)

    monkeypatch.setenv("OC_INDEX_DELETE_ENABLED", "true")
    monkeypatch.setattr(app.state, "index", Index())
    monkeypatch.setattr(app_module, "run_in_threadpool", delayed)
    now = datetime.now(timezone.utc)
    request = {"schemaVersion": "openconnect.v1", "taskId": "delete", "kind": "knowledge.index.delete",
        "tenantId": "01ARZ3NDEKTSV4RRFFQ69G5FAV", "issuedAt": (now - timedelta(seconds=1)).isoformat(),
        "expiresAt": (now + timedelta(milliseconds=300)).isoformat(), "payload": {"applicationId": "app",
        "collectionId": "collection", "documentId": "doc", "expectedGeneration": 0}}

    assert signed(client, request).status_code == 422
    assert calls == []


def test_model_timeout_holds_slot_until_inference_finishes(service, monkeypatch):
    client, _ = service
    entered, release, finished = threading.Event(), threading.Event(), threading.Event()
    engine = app.state.engine
    original = engine.embed

    def slow(*args, **kwargs):
        entered.set()
        try:
            assert release.wait(5)
            return original(*args, **kwargs)
        finally:
            finished.set()

    monkeypatch.setattr(engine, "embed", slow)
    request = payload()
    request["expiresAt"] = (datetime.now(timezone.utc) + timedelta(milliseconds=300)).isoformat()
    try:
        assert signed(client, request).status_code == 422
        assert entered.is_set()
        assert signed(client, payload()).status_code == 429
    finally:
        release.set()
        assert finished.wait(5)
        assert _slot.acquire(timeout=5)
        _slot.release()
