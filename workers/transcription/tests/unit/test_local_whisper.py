import dataclasses
import base64
import hashlib
from types import SimpleNamespace

import faster_whisper
import pytest
from fastapi.testclient import TestClient

import openconnect_transcription.app as app_module
from openconnect_transcription.engine import local_whisper
from openconnect_transcription.engine.audio import ValidatedAudio
from openconnect_transcription.runtime_cache import RuntimeModelAsset, RuntimeModelCacheManager, RuntimeModelManifest


@pytest.fixture
def provisioned(tmp_path, monkeypatch):
    files = {"model.bin": b"weights", "config.json": b"{}", "tokenizer.json": b"{}",
             "preprocessor_config.json": b"{}"}
    manifest = RuntimeModelManifest(workerKind="transcription", modelId="local/whisper", immutableRevision="v1",
        license="mit", assets=[RuntimeModelAsset(relativePath=name, sha256=hashlib.sha256(data).hexdigest(),
        sizeBytes=len(data)) for name, data in files.items()])
    (tmp_path / "model-manifest.json").write_text(manifest.model_dump_json(), encoding="utf-8")
    manager = RuntimeModelCacheManager(tmp_path, "transcription")
    result = manager.provision_or_verify(manifest, asset_fetcher=lambda asset, target: target.write_bytes(files[asset.relativePath]))
    assert result.ready
    cfg = dataclasses.replace(app_module.settings, backend="local-model", fake_engine=False, model_dir=str(tmp_path),
                              model_id="local/whisper", model_revision="v1", model_device="cpu", cloud_url=None)
    monkeypatch.setattr(local_whisper, "_loaded_key", None)
    monkeypatch.setattr(local_whisper, "_loaded_model", None)
    return cfg, result.revision_dir


def test_verified_local_model_is_loaded_offline_and_transcribes(provisioned, monkeypatch):
    cfg, _ = provisioned
    calls = []

    class Model:
        def __init__(self, path, **options):
            calls.append((path, options))

        def transcribe(self, audio, **options):
            assert len(audio) == 1600
            assert options["language"] == "en"
            return iter([SimpleNamespace(start=0.0, end=0.1, text=" hello ")]), None

    monkeypatch.setattr(faster_whisper, "WhisperModel", Model)
    model = local_whisper.load_model(cfg, "cpu")
    assert model is not None
    assert calls[0][1]["local_files_only"] is True
    audio = ValidatedAudio(b"\0\0" * 1600, "audio/pcm", 16000, 1, 16, 100)
    assert local_whisper.transcribe(model, audio, "en-US").transcript == "hello"
    assert local_whisper.load_model(cfg, "cpu") is model
    assert len(calls) == 1


@pytest.mark.parametrize("rate,channels", [(8000, 1), (16000, 2), (48000, 2)])
def test_audio_is_resampled_to_mono_16khz(rate, channels):
    class Model:
        def transcribe(self, audio, **options):
            assert len(audio) == 1600
            return iter([SimpleNamespace(start=0.0, end=0.1, text="hello")]), None

    audio = ValidatedAudio(b"\0\0" * (rate // 10) * channels, "audio/pcm", rate, channels, 16, 100)
    assert local_whisper.transcribe(Model(), audio, "en").transcript == "hello"


def test_tampered_model_is_unavailable_even_after_loading(provisioned, monkeypatch):
    cfg, revision = provisioned
    monkeypatch.setattr(faster_whisper, "WhisperModel", lambda *args, **kwargs: object())
    assert local_whisper.load_model(cfg, "cpu") is not None
    (revision / "assets" / "tokenizer.json").write_text("tampered", encoding="utf-8")
    assert local_whisper.load_model(cfg, "cpu") is None


def test_unverified_extra_model_file_is_rejected(provisioned, monkeypatch):
    cfg, revision = provisioned
    monkeypatch.setattr(faster_whisper, "WhisperModel", lambda *args, **kwargs: object())
    (revision / "assets" / "extra.json").write_text("{}", encoding="utf-8")
    assert local_whisper.load_model(cfg, "cpu") is None


def test_local_api_executes_verified_adapter(provisioned, monkeypatch):
    cfg, _ = provisioned

    class Model:
        def __init__(self, path, **options):
            pass

        def transcribe(self, audio, **options):
            return iter([SimpleNamespace(start=0.0, end=0.1, text="spoken text")]), None

    monkeypatch.setattr(faster_whisper, "WhisperModel", Model)
    monkeypatch.setattr(app_module, "settings", dataclasses.replace(cfg, worker_token="local-token"))
    client = TestClient(app_module.app)
    assert client.get("/health/ready").status_code == 200
    response = client.post("/internal/v1/execute", headers={"Authorization": "Bearer local-token"}, json={
        "schemaVersion": "openconnect.v1", "requestId": "123e4567-e89b-42d3-a456-426614174000",
        "input": {"audioBase64": base64.b64encode(b"\0\0" * 1600).decode(), "mimeType": "audio/pcm",
                  "sampleRateHz": 16000, "channels": 1, "bitsPerSample": 16, "durationMs": 100, "locale": "en"},
    })
    assert response.status_code == 200
    assert response.json()["transcript"] == "spoken text"


def test_audio_inspection_requires_local_real_model_and_binds_audio(provisioned, monkeypatch):
    cfg, _ = provisioned

    class Model:
        def __init__(self, path, **options):
            pass

        def transcribe(self, audio, **options):
            return iter([SimpleNamespace(start=0.0, end=0.1, text="spoken text")]), None

    monkeypatch.setattr(faster_whisper, "WhisperModel", Model)
    cfg = dataclasses.replace(cfg, worker_token="local-token")
    monkeypatch.setattr(app_module, "settings", cfg)
    client = TestClient(app_module.app)
    audio = b"\0\0" * 1600
    body = {"schemaVersion": "openconnect.v1", "requestId": "123e4567-e89b-42d3-a456-426614174000",
            "input": {"audioBase64": base64.b64encode(audio).decode(), "mimeType": "audio/pcm",
                      "sampleRateHz": 16000, "channels": 1, "bitsPerSample": 16, "durationMs": 100, "locale": "en"}}
    url = "/internal/v1/audio-inspection/transcribe"
    assert client.post(url, json=body).status_code == 401
    headers = {"Authorization": "Bearer local-token"}
    result = client.post(url, headers=headers, json=body)
    assert result.status_code == 200
    assert result.headers["cache-control"] == "no-store"
    assert result.json()["audioSha256"] == hashlib.sha256(audio).hexdigest()
    assert result.json()["transcript"] == "spoken text"
    assert client.post(url, headers={**headers, "Content-Type": "application/json"},
                       content='{"schemaVersion":"openconnect.v1","schemaVersion":"openconnect.v1"}').status_code == 400
    assert client.post(url, headers={**headers, "Content-Encoding": "gzip"}, json=body).status_code == 415
    assert client.post(url, headers={**headers, "Content-Length": "7002049"}, json=body).status_code == 413
    for changed in (dataclasses.replace(cfg, fake_engine=True),
                    dataclasses.replace(cfg, cloud_url="https://provider.example")):
        monkeypatch.setattr(app_module, "settings", changed)
        assert client.post(url, headers=headers, json=body).status_code == 503
