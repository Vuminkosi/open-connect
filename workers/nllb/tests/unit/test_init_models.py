from pathlib import Path

import openconnect_nllb.init_models as init_models
from openconnect_nllb.config import Settings
from openconnect_nllb.decoder_bundle import DecoderBundle


def settings(tmp_path, **changes):
    values = dict(worker_token=None, model_dir=tmp_path, fake_engine=False, is_production=False,
                  model_device="cpu", max_text_chars=5000, license_accepted=False)
    values.update(changes)
    return Settings(**values)


def test_init_requires_selected_model(monkeypatch, tmp_path, capsys):
    monkeypatch.setattr(init_models.Settings, "from_environment", lambda: settings(tmp_path))
    assert init_models.main() == 1
    assert "OC_NLLB_MODEL_ID" in capsys.readouterr().out


def test_init_uses_verified_bundle_and_license_context(monkeypatch, tmp_path):
    monkeypatch.setattr(init_models.Settings, "from_environment", lambda: settings(
        tmp_path, model_id="model", license_accepted=True, commercial_use=True
    ))
    calls = []

    def resolve(*args, **kwargs):
        calls.append((args, kwargs))
        return DecoderBundle(Path(tmp_path), "model", "rev", "a" * 64, "b" * 64), None

    monkeypatch.setattr(init_models, "resolve_decoder_bundle", resolve)
    assert init_models.main() == 0
    assert calls[0][1] == {"license_accepted": True, "commercial_use": True}


def test_fake_init_does_not_require_or_create_model(monkeypatch, tmp_path):
    monkeypatch.setattr(init_models.Settings, "from_environment", lambda: settings(tmp_path, fake_engine=True))
    assert init_models.main() == 0
    assert list(tmp_path.iterdir()) == []
