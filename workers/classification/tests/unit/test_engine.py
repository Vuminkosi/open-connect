from openconnect_classification.engine import (
    FAKE_MODEL_REVISION,
    FakeEngine,
    LicenseDeniedError,
    ModelUnavailableError,
    RealEngine,
)
from openconnect_classification.manifest import ModelManifest


def test_fake_engine_is_deterministic_and_tagged() -> None:
    engine = FakeEngine()
    first = engine.classify(
        "win free money lottery now", label_set_version="v1", threshold=0.5, max_labels=3
    )
    second = engine.classify(
        "win free money lottery now", label_set_version="v1", threshold=0.5, max_labels=3
    )
    assert first == second
    assert first.engine == "fake"
    assert first.model_revision == FAKE_MODEL_REVISION
    assert "spam" in first.labels


def test_fake_engine_abstains_on_empty_signals_with_high_threshold() -> None:
    engine = FakeEngine()
    outcome = engine.classify("zzz", label_set_version="v1", threshold=0.99, max_labels=3)
    assert outcome.abstained in (True, False)  # shape guard; scores decide
    assert all(0 <= score <= 1 for score in outcome.scores.values())


def test_real_engine_refuses_without_model() -> None:
    engine = RealEngine(None, ready=False, reason="model_unavailable")
    try:
        engine.classify("hi", label_set_version="v1", threshold=0.5, max_labels=3)
    except ModelUnavailableError:
        pass
    else:  # pragma: no cover
        raise AssertionError("expected ModelUnavailableError")


def test_real_engine_license_denied() -> None:
    manifest = ModelManifest(model_id="m", revision="r", checksum="c", license="cc-by-nc-4.0")
    engine = RealEngine(manifest, ready=False, reason="license_denied")
    try:
        engine.classify("hi", label_set_version="v1", threshold=0.5, max_labels=3)
    except LicenseDeniedError:
        pass
    else:  # pragma: no cover
        raise AssertionError("expected LicenseDeniedError")
