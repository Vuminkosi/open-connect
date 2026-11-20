from openconnect_inference import cloud_runtime


def test_disabled_or_unconfigured_runtime_never_loads_model(monkeypatch):
    calls = []
    monkeypatch.setattr(cloud_runtime, "load_verified_runtime", lambda *args, **kwargs: calls.append(args))
    monkeypatch.setenv("OPENCONNECT_CLOUD_INFERENCE_ENABLED", "false")
    assert cloud_runtime.load_cloud_runtime() is None
    monkeypatch.setenv("OPENCONNECT_CLOUD_INFERENCE_ENABLED", "true")
    assert cloud_runtime.load_cloud_runtime() is None
    assert calls == []


def test_runtime_requires_approval_and_passes_pinned_configuration(monkeypatch, tmp_path):
    monkeypatch.setenv("OPENCONNECT_CLOUD_INFERENCE_ENABLED", "true")
    monkeypatch.setenv("OPENCONNECT_CLOUD_MODEL_FORMAT", "chatml")
    monkeypatch.setenv("OC_WORKER_ENGINE", "real")
    monkeypatch.setenv("OPENCONNECT_FAKE_ENGINE", "0")
    monkeypatch.setenv("OPENCONNECT_CLOUD_MODEL_PATH", str(tmp_path / "model.gguf"))
    monkeypatch.setenv("OPENCONNECT_CLOUD_MODEL_ID", "registry/model-a")
    monkeypatch.setenv("OPENCONNECT_CLOUD_MODEL_REVISION", "revision-1")
    monkeypatch.setenv("OPENCONNECT_CLOUD_MODEL_DIGEST", "sha256:" + "a" * 64)
    monkeypatch.setenv("OPENCONNECT_CLOUD_MODEL_LICENSE_ACCEPTED", "true")
    monkeypatch.setenv("OPENCONNECT_CLOUD_MODEL_ARTIFACT_REVIEWED", "true")
    monkeypatch.setattr(cloud_runtime, "configured_client", lambda: object())
    marker = object()
    seen = []
    def loader(*args, **kwargs):
        seen.append((args, kwargs))
        return marker
    monkeypatch.setattr(cloud_runtime, "load_verified_runtime", loader)
    assert cloud_runtime.load_cloud_runtime() is marker
    assert seen[0][0][1:4] == ("registry/model-a", "revision-1", "sha256:" + "a" * 64)
    assert seen[0][1] == {"license_accepted": True, "artifact_reviewed": True}
    monkeypatch.setenv("OPENCONNECT_FAKE_ENGINE", "1")
    assert cloud_runtime.load_cloud_runtime() is None
    monkeypatch.setenv("OPENCONNECT_FAKE_ENGINE", "0")
    monkeypatch.setenv("OPENCONNECT_CLOUD_MODEL_CONTEXT_TOKENS", "bad")
    assert cloud_runtime.load_cloud_runtime() is None


def test_application_lifespan_installs_and_closes_runtime(monkeypatch):
    from fastapi.testclient import TestClient
    from openconnect_inference.app import app

    class Runtime:
        closed = False
        def close(self):
            self.closed = True
    runtime = Runtime()
    monkeypatch.setattr(cloud_runtime, "load_cloud_runtime", lambda: runtime)
    with TestClient(app):
        assert app.state.cloud_inference_runtime is runtime
    assert runtime.closed
    assert app.state.cloud_inference_runtime is None
