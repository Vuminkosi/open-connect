from openconnect_nllb.engine import (
    FakeEngine,
    LanguageUnsupportedError,
    LicenseDeniedError,
    ModelUnavailableError,
    RealEngine,
)
from openconnect_nllb.manifest import ModelManifest


def test_fake_engine_tags_non_production() -> None:
    outcome = FakeEngine().translate("hello", source_lang="eng_Latn", target_lang="fra_Latn")
    assert outcome.engine == "fake"
    assert outcome.translated_text.startswith("[fra_Latn fake]")
    assert outcome.model_revision.startswith("fake-")


def test_fake_engine_rejects_unknown_pair() -> None:
    try:
        FakeEngine().translate("hi", source_lang="eng_Latn", target_lang="xx_Yyyy")
    except LanguageUnsupportedError:
        pass
    else:  # pragma: no cover
        raise AssertionError("expected LanguageUnsupportedError")


def test_real_engine_license_denied() -> None:
    manifest = ModelManifest(
        model_id="m", revision="r", checksum="c", tokenizer_hash="t", license="cc-by-nc-4.0"
    )
    engine = RealEngine(manifest, ready=False, reason="license_denied")
    try:
        engine.translate("hi", source_lang="eng_Latn", target_lang="fra_Latn")
    except LicenseDeniedError:
        pass
    else:  # pragma: no cover
        raise AssertionError("expected LicenseDeniedError")


def test_real_engine_never_fetches_remotely(monkeypatch, tmp_path) -> None:
    manifest = ModelManifest(
        model_id="m", revision="r", checksum="c", tokenizer_hash="t", license="mit"
    )
    engine = RealEngine(manifest, ready=True, reason=None)
    monkeypatch.delenv("HF_HUB_OFFLINE", raising=False)
    try:
        engine.translate("hi", source_lang="eng_Latn", target_lang="fra_Latn")
    except ModelUnavailableError:
        pass
    else:  # pragma: no cover
        raise AssertionError("expected ModelUnavailableError")
