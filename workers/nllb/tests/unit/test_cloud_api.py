import base64
import json
from uuid import uuid4

from fastapi.testclient import TestClient

import openconnect_nllb.app as app_module
from openconnect_nllb.config import Settings
from openconnect_nllb.manifest import ModelManifest, Readiness
from openconnect_nllb.schemas import ExecuteResponse, TranslateResult

client = TestClient(app_module.app)
TOKEN = {"Authorization": "Bearer test-token"}
REVISION = "revision-1"
TOKENIZER = "a" * 64


class ClaimClient:
    def __init__(self, deployment, consumption="consumed", completion="completed"):
        self.deployment = deployment
        self.consumption = consumption
        self.completion = completion
        self.events = []

    def translation_ready(self):
        self.events.append("ready")
        return self.deployment

    def consume(self, *_args):
        self.events.append("consume")
        return self.consumption

    def complete(self, *_args):
        self.events.append("complete")
        return self.completion


def configure(monkeypatch, tmp_path, *, fake=False, consumption="consumed", completion="completed"):
    deployment = str(uuid4())
    claim_client = ClaimClient(deployment, consumption, completion)
    settings = Settings(worker_token="test-token", model_dir=tmp_path, fake_engine=fake, is_production=False,
                        model_device="cpu", max_text_chars=5000, license_accepted=False)
    monkeypatch.setattr(app_module, "settings", settings)
    monkeypatch.setattr(app_module, "configured_client", lambda: claim_client)
    monkeypatch.setenv("OPENCONNECT_CLOUD_DEPLOYMENT_ID", deployment)
    manifest = ModelManifest("model", REVISION, "b" * 64, TOKENIZER, "mit")
    monkeypatch.setattr(app_module, "_real_readiness", lambda: (Readiness(True, None, manifest), None, object()))

    def execute(request):
        claim_client.events.append("execute")
        return ExecuteResponse(taskId=request.taskId, status="completed", result=TranslateResult(
            translatedText="bonjour", sourceLang=request.input.sourceLang, targetLang=request.input.targetLang,
            modelRevision=REVISION, tokenizerHash=TOKENIZER, engine="real"))

    monkeypatch.setattr(app_module, "run_translation", execute)
    return claim_client


def envelope():
    payload = json.dumps({"schemaVersion": "openconnect.v1", "taskId": str(uuid4()),
        "input": {"text": "hello", "sourceLang": "eng_Latn", "targetLang": "fra_Latn"}}).encode()
    return {"schemaVersion": "openconnect.cloud.worker.translation/v1", "reservationId": str(uuid4()),
            "claimId": str(uuid4()), "payload": base64.b64encode(payload).decode(),
            "key": base64.b64encode(b"k" * 32).decode()}


def test_cloud_mode_blocks_legacy_execution_and_requires_worker_auth(monkeypatch, tmp_path):
    configure(monkeypatch, tmp_path)
    assert client.get("/internal/cloud/v1/translation/ready").status_code == 401
    legacy = {"schemaVersion": "openconnect.v1", "taskId": "legacy", "input":
              {"text": "hello", "sourceLang": "eng_Latn", "targetLang": "fra_Latn"}}
    assert client.post("/internal/v1/execute", headers=TOKEN, json=legacy).status_code == 503


def test_cloud_execution_is_fenced_and_settled(monkeypatch, tmp_path):
    claim_client = configure(monkeypatch, tmp_path)
    assert client.get("/internal/cloud/v1/translation/ready", headers=TOKEN).json()["status"] == "ready"
    claim_client.events.clear()
    response = client.post("/internal/cloud/v1/translation/execute", headers=TOKEN, json=envelope())
    assert response.status_code == 200
    assert response.json()["result"]["translatedText"] == "bonjour"
    assert claim_client.events == ["ready", "consume", "execute", "complete"]


def test_cloud_refuses_fake_identity_mismatch_and_ambiguous_claim(monkeypatch, tmp_path):
    claim_client = configure(monkeypatch, tmp_path, fake=True)
    assert client.get("/internal/cloud/v1/translation/ready", headers=TOKEN).status_code == 503
    claim_client = configure(monkeypatch, tmp_path)
    claim_client.deployment = str(uuid4())
    assert client.get("/internal/cloud/v1/translation/ready", headers=TOKEN).status_code == 503
    claim_client = configure(monkeypatch, tmp_path, consumption="already_consumed")
    response = client.post("/internal/cloud/v1/translation/execute", headers=TOKEN, json=envelope())
    assert response.status_code == 503
    assert claim_client.events == ["ready", "consume"]


def test_cloud_rejects_encoded_and_oversized_bodies(monkeypatch, tmp_path):
    configure(monkeypatch, tmp_path)
    response = client.post("/internal/cloud/v1/translation/execute",
        headers={**TOKEN, "Content-Type": "application/json", "Content-Encoding": "gzip"}, content=b"{}")
    assert response.status_code == 415
    response = client.post("/internal/cloud/v1/translation/execute",
        headers={**TOKEN, "Content-Type": "application/json"}, content=b"x" * 100001)
    assert response.status_code == 413
