import pytest

import openconnect_nllb.cloud_config as cloud_config


@pytest.fixture(autouse=True)
def clear_cached_client():
    cloud_config.configured_client.cache_clear()
    yield
    cloud_config.configured_client.cache_clear()


def test_disabled_by_default_and_partial_configuration_fails_closed(monkeypatch):
    monkeypatch.delenv("OPENCONNECT_CLOUD_CONSUMPTION_ENABLED", raising=False)
    assert cloud_config.configured_client() is None
    cloud_config.configured_client.cache_clear()
    monkeypatch.setenv("OPENCONNECT_CLOUD_CONSUMPTION_ENABLED", "true")
    with pytest.raises(ValueError):
        cloud_config.configured_client()
    monkeypatch.setenv("OPENCONNECT_CLOUD_CONSUMPTION_ENABLED", "unexpected")
    with pytest.raises(ValueError):
        cloud_config.configured_client()
